/* MovieHub player. Plays the ISP URL directly in an HTML5 <video>.
 * Fallback chain when playback fails: direct URL -> range-aware proxy (/media/stream) -> ffmpeg remux (/media/remux, if configured) -> clear message.
 * Audio tracks: native video.audioTracks where the browser has it; otherwise server-side remux when ffmpeg is configured;
 * otherwise the tracks are listed read-only with an explanation. No control is shown that does nothing. */
(function () {
  'use strict';
  var cfgEl = document.getElementById('player-config');
  if (!cfgEl) return;
  var cfg = JSON.parse(cfgEl.textContent);

  var $ = function (id) { return document.getElementById(id); };
  var player = $('player'), video = $('video');
  var el = {
    play: $('play'), mute: $('mute'), volume: $('volume'), seek: $('seek'), buffered: $('buffered'),
    cur: $('t-cur'), dur: $('t-dur'), fs: $('fs'),
    btnSub: $('btn-sub'), btnAudio: $('btn-audio'), btnSpeed: $('btn-speed'),
    menuSub: $('menu-sub'), menuAudio: $('menu-audio'), menuSpeed: $('menu-speed'),
    msg: $('player-message'), msgTitle: $('msg-title'), msgText: $('msg-text'), retry: $('msg-retry'), status: $('stream-status')
  };

  var mode = 'direct';          // direct | proxy | remux
  var offset = 0;               // seconds already skipped by the remux stream (-ss)
  var audioIndex = null;        // chosen ffprobe audio stream index (remux mode)
  var triedModes = {};
  var loadTimer = null;
  var nativeAudio = false;
  var knownDuration = cfg.duration || 0;

  function fmt(s) {
    if (!isFinite(s) || s < 0) s = 0;
    var h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = Math.floor(s % 60);
    return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(sec).padStart(2, '0');
  }
  function duration() { return knownDuration || (isFinite(video.duration) ? video.duration + offset : 0); }
  function position() { return offset + (video.currentTime || 0); }
  function setStatus(t) { if (el.status) el.status.textContent = t; }

  // ------------------------------------------------------------ source handling
  function urlFor(m, startAt) {
    if (m === 'direct') return cfg.directUrl;
    if (m === 'proxy') return cfg.proxyUrl;
    var q = [];
    if (audioIndex !== null) q.push('audio=' + audioIndex);
    if (startAt > 0) q.push('t=' + startAt.toFixed(2));
    return cfg.remuxUrl + (q.length ? '?' + q.join('&') : '');
  }

  function load(m, startAt, autoplay) {
    mode = m; triedModes[m] = true;
    offset = m === 'remux' ? (startAt || 0) : 0;
    var src = urlFor(m, startAt || 0);
    if (!src) { fail('This movie\'s source address is not approved or missing.'); return; }
    hideMessage();
    player.classList.add('is-loading');
    video.src = src;
    video.load();
    if (m !== 'remux' && startAt > 0) {
      video.addEventListener('loadedmetadata', function once() { video.removeEventListener('loadedmetadata', once); video.currentTime = startAt; });
    }
    if (autoplay) video.play().catch(function () { /* autoplay blocked: user presses play */ });
    clearTimeout(loadTimer);
    // A server that accepts the connection but never delivers metadata would otherwise spin forever.
    loadTimer = setTimeout(function () { if (video.readyState < 1) onError('timeout'); }, 25000);
    setStatus(m === 'direct' ? 'Streaming directly from the source server.'
      : m === 'proxy' ? 'Streaming through the application (the browser could not reach the source directly).'
      : 'Compatibility mode: the server is converting the stream with ffmpeg.');
  }

  function onError(reason) {
    clearTimeout(loadTimer);
    var at = position();
    var next = null;
    if (mode === 'direct' && !triedModes.proxy) next = 'proxy';
    else if (mode !== 'remux' && cfg.remuxEnabled && !triedModes.remux) next = 'remux';
    if (next) { load(next, at, !video.paused || true); return; }

    var e = video.error;
    var why = (e && e.code === 3) ? 'The video could not be decoded (damaged data or an unsupported codec).'
            : (e && e.code === 2) ? 'A network error interrupted the stream. The source server may be offline.'
            : 'This browser cannot play this file, or the source server is unreachable.';
    var hint = cfg.remuxEnabled ? '' :
      ' Containers such as MKV/AVI and codecs such as HEVC or AC3 often are not supported by browsers. An administrator can enable compatibility mode by setting Media:FfmpegPath.';
    fail(why + hint + (cfg.videoCodec ? ' (video: ' + cfg.videoCodec + (cfg.audioCodec ? ', audio: ' + cfg.audioCodec : '') + ', container: ' + cfg.extension.replace('.', '') + ')' : ''));
  }

  function fail(text) {
    player.classList.remove('is-loading');
    el.msgTitle.textContent = 'Cannot play this video';
    el.msgText.textContent = text;
    el.msg.classList.add('is-on');
  }
  function hideMessage() { el.msg.classList.remove('is-on'); }

  video.addEventListener('error', function () { onError('media'); });
  video.addEventListener('loadedmetadata', function () {
    clearTimeout(loadTimer);
    player.classList.remove('is-loading');
    if (!knownDuration && isFinite(video.duration)) knownDuration = video.duration;
    detectNativeAudio();
    renderAudioMenu();
    updateTime();
  });
  video.addEventListener('waiting', function () { player.classList.add('is-loading'); });
  ['playing', 'canplay', 'seeked'].forEach(function (ev) { video.addEventListener(ev, function () { player.classList.remove('is-loading'); }); });
  el.retry.addEventListener('click', function () { triedModes = {}; load('direct', position(), true); });

  // ------------------------------------------------------------ basic controls
  function togglePlay() { if (video.paused) video.play().catch(function () {}); else video.pause(); }
  el.play.addEventListener('click', togglePlay);
  video.addEventListener('click', togglePlay);
  video.addEventListener('dblclick', toggleFs);
  video.addEventListener('play', function () { el.play.textContent = '❚❚'; el.play.setAttribute('aria-label', 'Pause'); });
  video.addEventListener('pause', function () { el.play.textContent = '▶'; el.play.setAttribute('aria-label', 'Play'); player.classList.remove('is-idle'); });

  function updateTime() {
    var d = duration(), p = position();
    el.cur.textContent = fmt(p);
    el.dur.textContent = fmt(d);
    if (!seeking && d > 0) el.seek.value = Math.round((p / d) * 1000);
    try {
      var b = video.buffered;
      if (b.length && d > 0) {
        var end = offset + b.end(b.length - 1);
        el.buffered.style.width = Math.min(100, (end / d) * 100) + '%';
      }
    } catch (_) { /* buffered not available yet */ }
  }
  video.addEventListener('timeupdate', updateTime);
  video.addEventListener('progress', updateTime);
  video.addEventListener('durationchange', updateTime);

  var seeking = false;
  el.seek.addEventListener('input', function () { seeking = true; el.cur.textContent = fmt((el.seek.value / 1000) * duration()); });
  el.seek.addEventListener('change', function () {
    seeking = false;
    var target = (el.seek.value / 1000) * duration();
    seekTo(target);
  });
  function seekTo(t) {
    t = Math.max(0, Math.min(duration() || t, t));
    if (mode === 'remux') {
      // The remux stream is not seekable: restart it at the requested position (ffmpeg -ss).
      var wasPlaying = !video.paused;
      load('remux', t, wasPlaying || true);
    } else {
      video.currentTime = t;
    }
  }

  el.volume.addEventListener('input', function () { video.volume = Number(el.volume.value); video.muted = video.volume === 0; });
  el.mute.addEventListener('click', function () { video.muted = !video.muted; });
  video.addEventListener('volumechange', function () {
    el.mute.textContent = video.muted || video.volume === 0 ? '🔇' : video.volume < 0.5 ? '🔉' : '🔊';
    el.volume.value = video.muted ? 0 : video.volume;
  });

  function toggleFs() {
    if (document.fullscreenElement) document.exitFullscreen();
    else if (player.requestFullscreen) player.requestFullscreen();
    else if (video.webkitEnterFullscreen) video.webkitEnterFullscreen(); // iOS Safari
  }
  el.fs.addEventListener('click', toggleFs);
  document.addEventListener('fullscreenchange', function () { player.classList.toggle('is-full', !!document.fullscreenElement); });

  // ------------------------------------------------------------ menus
  var menus = [[el.btnSub, el.menuSub], [el.btnAudio, el.menuAudio], [el.btnSpeed, el.menuSpeed]];
  menus.forEach(function (pair) {
    pair[0].addEventListener('click', function (e) {
      e.stopPropagation();
      var open = pair[1].classList.contains('is-open');
      closeMenus();
      if (!open) pair[1].classList.add('is-open');
    });
  });
  function closeMenus() { menus.forEach(function (p) { p[1].classList.remove('is-open'); }); }
  document.addEventListener('click', function (e) { if (!e.target.closest('.menu')) closeMenus(); });

  function item(menu, label, on, handler) {
    var b = document.createElement('button');
    b.type = 'button'; b.textContent = label;
    if (on) b.classList.add('is-on');
    b.addEventListener('click', function () { handler(); closeMenus(); });
    menu.appendChild(b);
  }
  function heading(menu, text) { var h = document.createElement('h4'); h.textContent = text; menu.appendChild(h); }
  function note(menu, text) { var d = document.createElement('div'); d.className = 'note'; d.textContent = text; menu.appendChild(d); }

  // speed
  var speeds = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];
  function renderSpeed() {
    el.menuSpeed.innerHTML = ''; heading(el.menuSpeed, 'Speed');
    speeds.forEach(function (s) {
      item(el.menuSpeed, s === 1 ? 'Normal' : s + '×', video.playbackRate === s, function () { video.playbackRate = s; });
    });
    el.btnSpeed.textContent = video.playbackRate + '×';
  }
  video.addEventListener('ratechange', renderSpeed);
  renderSpeed();

  // subtitles (external files, served as WebVTT from this site so the browser accepts them)
  var activeSub = -1;
  function buildSubtitleTracks() {
    cfg.subtitles.forEach(function (s, i) {
      if (!s.supported) return;
      var t = document.createElement('track');
      t.kind = 'subtitles'; t.label = s.lang || s.file; t.srclang = (s.lang || 'und').slice(0, 2).toLowerCase();
      t.src = '/media/subtitle/' + s.id;
      t.dataset.idx = i;
      video.appendChild(t);
    });
    Array.prototype.forEach.call(video.textTracks, function (tt) { tt.mode = 'disabled'; });
  }
  function setSubtitle(i) {
    activeSub = i;
    var idx = 0;
    Array.prototype.forEach.call(video.querySelectorAll('track'), function (t) {
      var on = Number(t.dataset.idx) === i;
      t.track.mode = on ? 'showing' : 'disabled';
      idx++;
    });
    renderSubMenu();
  }
  function renderSubMenu() {
    el.menuSub.innerHTML = ''; heading(el.menuSub, 'Subtitles');
    var usable = cfg.subtitles.filter(function (s) { return s.supported; });
    if (!usable.length) {
      el.btnSub.disabled = true;
      el.btnSub.title = cfg.embeddedSubtitles ? 'Subtitles embedded in the file cannot be shown by the browser.' : 'No subtitles available';
      return;
    }
    item(el.menuSub, 'Off', activeSub === -1, function () { setSubtitle(-1); });
    cfg.subtitles.forEach(function (s, i) {
      if (!s.supported) return;
      item(el.menuSub, (s.lang || 'Unknown') + ' — ' + s.file, activeSub === i, function () { setSubtitle(i); });
    });
    if (cfg.embeddedSubtitles) note(el.menuSub, cfg.embeddedSubtitles + ' embedded subtitle track(s) exist in the file but browsers cannot display those.');
  }
  buildSubtitleTracks();
  renderSubMenu();

  // audio
  function detectNativeAudio() {
    nativeAudio = !!(video.audioTracks && video.audioTracks.length > 1);
  }
  function label(a, i) {
    var parts = [a.lang || a.title || ('Track ' + (i + 1))];
    if (a.codec) parts.push(a.codec.toUpperCase());
    if (a.channels) parts.push(a.channels === 6 ? '5.1' : a.channels === 2 ? 'stereo' : a.channels + 'ch');
    return parts.join(' · ');
  }
  function renderAudioMenu() {
    el.menuAudio.innerHTML = ''; heading(el.menuAudio, 'Audio');
    var tracks = cfg.audio || [];

    if (tracks.length <= 1 && !nativeAudio) {
      // Nothing to choose: hide the button instead of showing a dead control.
      el.btnAudio.parentElement.style.display = 'none';
      return;
    }
    el.btnAudio.parentElement.style.display = '';

    if (nativeAudio) {
      for (var i = 0; i < video.audioTracks.length; i++) {
        (function (idx) {
          var t = video.audioTracks[idx];
          item(el.menuAudio, t.label || t.language || ('Track ' + (idx + 1)), t.enabled, function () {
            for (var k = 0; k < video.audioTracks.length; k++) video.audioTracks[k].enabled = (k === idx);
            renderAudioMenu();
          });
        })(i);
      }
      el.btnAudio.disabled = false;
    } else if (cfg.remuxEnabled) {
      var current = audioIndex !== null ? audioIndex : (tracks.find(function (a) { return a.isDefault; }) || tracks[0]).index;
      tracks.forEach(function (a, i) {
        item(el.menuAudio, label(a, i), a.index === current, function () {
          audioIndex = a.index;
          triedModes = { remux: true };
          load('remux', position(), true);
        });
      });
      note(el.menuAudio, 'Switching re-encodes the audio on the server, so playback restarts at the current position.');
      el.btnAudio.disabled = false;
    } else {
      tracks.forEach(function (a, i) { var b = document.createElement('div'); b.className = 'note'; b.textContent = '• ' + label(a, i); el.menuAudio.appendChild(b); });
      note(el.menuAudio, 'This file has several audio tracks, but this browser cannot switch between them. Safari can; for other browsers an administrator can set Media:FfmpegPath to enable server-side switching.');
      el.btnAudio.disabled = false;
      el.btnAudio.title = 'Audio tracks (switching not supported in this browser)';
    }
  }

  // ------------------------------------------------------------ idle controls + keyboard
  var idleTimer;
  function wake() {
    player.classList.remove('is-idle');
    clearTimeout(idleTimer);
    if (!video.paused) idleTimer = setTimeout(function () { player.classList.add('is-idle'); }, 3000);
  }
  ['mousemove', 'touchstart', 'keydown'].forEach(function (ev) { player.addEventListener(ev, wake, { passive: true }); });
  video.addEventListener('play', wake);

  player.addEventListener('keydown', function (e) {
    if (e.target.tagName === 'INPUT' && e.target.type !== 'range') return;
    var handled = true;
    switch (e.key) {
      case ' ': case 'k': togglePlay(); break;
      case 'ArrowRight': seekTo(position() + 10); break;
      case 'ArrowLeft': seekTo(position() - 10); break;
      case 'ArrowUp': video.volume = Math.min(1, video.volume + 0.1); break;
      case 'ArrowDown': video.volume = Math.max(0, video.volume - 0.1); break;
      case 'm': video.muted = !video.muted; break;
      case 'f': toggleFs(); break;
      default: handled = false;
    }
    if (handled) e.preventDefault();
  });

  load('direct', 0, false);
})();
