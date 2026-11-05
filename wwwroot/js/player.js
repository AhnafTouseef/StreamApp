/* MovieHub player.
 *
 * Source chain: direct ISP URL -> range-aware proxy (/media/stream) -> ffmpeg remux (/media/remux) -> clear message.
 *
 * Audio:     files with several audio tracks start with the English one. Browsers other than Safari cannot switch tracks themselves,
 *            so the server remuxes the stream with the wanted track (ffmpeg, auto-detected). Native video.audioTracks is used when it exists.
 * Subtitles: rendered by this script (not by <track>), so they stay correct after a remux restart. Tracks inside the video file are extracted
 *            by the server once (/media/embsub) and load automatically; a dropped or chosen file is the fallback when a movie has none,
 *            and can be shifted/stretched in time. Server subtitle files and dropped files go through the same parser.
 * Speed:     continuous 0.25x - 4x slider (logarithmic, so 1x sits in the middle) that snaps to round values.
 */
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
    msg: $('player-message'), msgTitle: $('msg-title'), msgText: $('msg-text'), retry: $('msg-retry'), status: $('stream-status'),
    subs: $('subs'), toast: $('player-toast'), file: $('sub-file'),
    tuner: $('tuner'), tunerClose: $('tuner-close'), tunerTrack: $('tuner-track'), tunerOffset: $('tuner-offset'),
    tunerSlider: $('tuner-slider'), tunerSync: $('tuner-sync'), tunerScale: $('tuner-scale'), tunerReset: $('tuner-reset'),
    sizeDown: $('size-down'), sizeUp: $('size-up'), sizeRead: $('size-read')
  };

  var mode = 'direct';          // direct | proxy | remux
  var prevMode = null;          // where to go back to if a user-chosen audio remux fails
  var offset = 0;               // seconds already skipped by the remux stream (-ss)
  var audioIndex = null;        // chosen ffprobe audio stream index (remux mode)
  var triedModes = {};
  var loadTimer = null;
  var nativeAudio = false;
  var lazyRemux = false;        // English start needs a remux: it begins on the first Play, so a page view never occupies an ffmpeg slot
  var knownDuration = cfg.duration || 0;
  var audioTracks = cfg.audio || [];

  var store = {
    get: function (k) { try { return window.localStorage.getItem(k); } catch (_) { return null; } },
    set: function (k, v) { try { window.localStorage.setItem(k, v); } catch (_) { /* storage unavailable */ } }
  };

  function fmt(s) {
    if (!isFinite(s) || s < 0) s = 0;
    var h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = Math.floor(s % 60);
    return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(sec).padStart(2, '0');
  }
  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
  function duration() { return knownDuration || (isFinite(video.duration) ? video.duration + offset : 0); }
  function position() { return offset + (video.currentTime || 0); }
  function setStatus(t) { if (el.status) el.status.textContent = t; }

  var toastTimer = null;
  function toast(text, actionLabel, action) {
    el.toast.innerHTML = '';
    var s = document.createElement('span'); s.textContent = text; el.toast.appendChild(s);
    if (actionLabel && action) {
      var b = document.createElement('button'); b.type = 'button'; b.textContent = actionLabel;
      b.addEventListener('click', function () { action(); el.toast.classList.remove('is-on'); });
      el.toast.appendChild(b);
    }
    el.toast.classList.add('is-on');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.toast.classList.remove('is-on'); }, actionLabel ? 7000 : 4000);
  }

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
    video.load(); // note: load() resets playbackRate to defaultPlaybackRate, which setRate() keeps in sync
    if (m !== 'remux' && startAt > 0) {
      video.addEventListener('loadedmetadata', function once() { video.removeEventListener('loadedmetadata', once); video.currentTime = startAt; });
    }
    if (autoplay) video.play().catch(function () { /* autoplay blocked: user presses play */ });
    clearTimeout(loadTimer);
    // A server that accepts the connection but never delivers metadata would otherwise spin forever.
    loadTimer = setTimeout(function () { if (video.readyState < 1) onError('timeout'); }, m === 'remux' ? 45000 : 25000);
    setStatus(m === 'direct' ? 'Streaming directly from the source server.'
      : m === 'proxy' ? 'Streaming through the application (the browser could not reach the source directly).'
      : 'Compatibility mode: the server is converting the stream with ffmpeg (needed for audio switching and some formats).');
  }

  function onError() {
    if (lazyRemux) return;   // the source was removed on purpose while waiting for Play
    clearTimeout(loadTimer);
    var at = position();

    // A remux the viewer asked for (audio switch / English start) failed: go back to plain playback instead of showing an error.
    if (mode === 'remux' && audioIndex !== null && prevMode) {
      var back = prevMode; prevMode = null; audioIndex = null;
      toast('Could not switch the audio track for this file. Playing the original audio.');
      renderAudioMenu();
      load(back, at, true);
      return;
    }

    var next = null;
    if (mode === 'direct' && !triedModes.proxy) next = 'proxy';
    else if (mode !== 'remux' && cfg.remuxEnabled && !triedModes.remux) next = 'remux';
    if (next) { load(next, at, true); return; }

    var e = video.error;
    var why = (e && e.code === 3) ? 'The video could not be decoded (damaged data or an unsupported codec).'
            : (e && e.code === 2) ? 'A network error interrupted the stream. The source server may be offline.'
            : 'This browser cannot play this file, or the source server is unreachable.';
    var hint = cfg.remuxEnabled ? '' :
      ' Containers such as MKV/AVI and codecs such as HEVC or AC3 often are not supported by browsers. Install FFmpeg on the server (or set Media:FfmpegPath) to enable compatibility mode.';
    fail(why + hint + (cfg.videoCodec ? ' (video: ' + cfg.videoCodec + (cfg.audioCodec ? ', audio: ' + cfg.audioCodec : '') + ', container: ' + String(cfg.extension || '').replace('.', '') + ')' : ''));
  }

  function fail(text) {
    player.classList.remove('is-loading');
    el.msgTitle.textContent = 'Cannot play this video';
    el.msgText.textContent = text;
    el.msg.classList.add('is-on');
  }
  function hideMessage() { el.msg.classList.remove('is-on'); }

  video.addEventListener('error', function () { onError(); });
  video.addEventListener('loadedmetadata', function () {
    clearTimeout(loadTimer);
    player.classList.remove('is-loading');
    if (!knownDuration && isFinite(video.duration)) knownDuration = video.duration;
    detectNativeAudio();
    preferEnglishNatively();
    renderAudioMenu();
    updateTime();
  });
  video.addEventListener('waiting', function () { player.classList.add('is-loading'); });
  ['playing', 'canplay', 'seeked'].forEach(function (ev) { video.addEventListener(ev, function () { player.classList.remove('is-loading'); }); });
  el.retry.addEventListener('click', function () { triedModes = {}; prevMode = null; load('direct', position(), true); });

  // ------------------------------------------------------------ basic controls
  function togglePlay() {
    if (lazyRemux) { lazyRemux = false; load('remux', 0, true); return; }
    if (video.paused) video.play().catch(function () {}); else video.pause();
  }
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
    seekTo((el.seek.value / 1000) * duration());
  });
  function seekTo(t) {
    t = Math.max(0, Math.min(duration() || t, t));
    if (lazyRemux) { lazyRemux = false; load('remux', t, true); return; }
    if (mode === 'remux') {
      // The remux stream is not seekable: restart it at the requested position (ffmpeg -ss).
      load('remux', t, true);
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
  document.addEventListener('click', function (e) { if (!e.target.closest('.menu') && !e.target.closest('.tuner')) closeMenus(); });

  function item(menu, label, on, handler, keepOpen) {
    var b = document.createElement('button');
    b.type = 'button'; b.textContent = label; b.title = label;
    if (on) b.classList.add('is-on');
    b.addEventListener('click', function () { handler(); if (!keepOpen) closeMenus(); });
    menu.appendChild(b);
    return b;
  }
  function heading(menu, text) { var h = document.createElement('h4'); h.textContent = text; menu.appendChild(h); }
  function note(menu, text) { var d = document.createElement('div'); d.className = 'note'; d.textContent = text; menu.appendChild(d); }

  // ------------------------------------------------------------ speed: continuous 0.25x-4x, logarithmic, snapping to round values
  var SPEED_MIN = 0.25, SPEED_MAX = 4, SPEED_SPAN = Math.log(SPEED_MAX / SPEED_MIN);
  var SNAPS = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3, 3.5, 4];
  var SNAP_RADIUS = 0.04;        // +-4 % of the speed pulls the slider onto the round value
  var speedUi = {};

  function posToRate(p) { return SPEED_MIN * Math.exp(SPEED_SPAN * p / 1000); }
  function rateToPos(r) { return Math.round(1000 * Math.log(r / SPEED_MIN) / SPEED_SPAN); }
  function nearestSnap(r) {
    var best = SNAPS[0];
    SNAPS.forEach(function (s) { if (Math.abs(Math.log(r / s)) < Math.abs(Math.log(r / best))) best = s; });
    return best;
  }
  function snapRate(r) { var s = nearestSnap(r); return Math.abs(Math.log(r / s)) <= SNAP_RADIUS ? s : r; }
  function fmtRate(r) { return (Math.round(r * 100) / 100).toString() + '×'; }
  function setRate(r) {
    r = Math.round(clamp(r, SPEED_MIN, SPEED_MAX) * 100) / 100;
    video.defaultPlaybackRate = r;   // survives video.load() when the source is swapped (audio switch, remux seek)
    video.playbackRate = r;
    if ('preservesPitch' in video) video.preservesPitch = true;
  }
  function stepSnap(dir) {
    var cur = video.playbackRate, target = null, i;
    if (dir > 0) { for (i = 0; i < SNAPS.length; i++) if (SNAPS[i] > cur + 0.001) { target = SNAPS[i]; break; } }
    else { for (i = SNAPS.length - 1; i >= 0; i--) if (SNAPS[i] < cur - 0.001) { target = SNAPS[i]; break; } }
    if (target !== null) setRate(target);
  }

  function buildSpeedMenu() {
    var m = el.menuSpeed;
    m.innerHTML = '';
    heading(m, 'Speed');
    var head = document.createElement('div'); head.className = 'speed-head';
    speedUi.read = document.createElement('output'); speedUi.read.className = 'speed-read';
    var reset = document.createElement('button'); reset.type = 'button'; reset.className = 'mini'; reset.textContent = 'Reset to 1×';
    reset.addEventListener('click', function () { setRate(1); });
    head.appendChild(speedUi.read); head.appendChild(reset); m.appendChild(head);

    var wrap = document.createElement('div'); wrap.className = 'speed-slider';
    var range = document.createElement('input');
    range.type = 'range'; range.min = 0; range.max = 1000; range.step = 1; range.value = rateToPos(1);
    range.setAttribute('aria-label', 'Playback speed, 0.25 to 4 times');
    wrap.appendChild(range);
    var ticks = document.createElement('div'); ticks.className = 'speed-ticks';
    SNAPS.forEach(function (s) {
      var t = document.createElement('button'); t.type = 'button'; t.className = 'tick' + ([0.25, 0.5, 1, 2, 4].indexOf(s) >= 0 ? ' labelled' : '');
      t.style.left = (rateToPos(s) / 10) + '%';
      t.dataset.rate = s;
      t.setAttribute('aria-label', s + ' times speed');
      var lab = document.createElement('span'); lab.textContent = s + '×'; t.appendChild(lab);
      t.addEventListener('click', function () { setRate(s); });
      ticks.appendChild(t);
    });
    wrap.appendChild(ticks);
    m.appendChild(wrap);
    note(m, 'Drag for any speed; the slider snaps to round values. Keys: < slower, > faster.');

    range.addEventListener('input', function () { setRate(snapRate(posToRate(Number(range.value)))); });
    range.addEventListener('keydown', function (e) {
      // Arrow keys on a snapping slider would be pulled straight back to the same round value, so they step the speed directly.
      var d = 0;
      if (e.key === 'ArrowRight' || e.key === 'ArrowUp') d = 1; else if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') d = -1;
      if (!d) return;
      e.preventDefault(); e.stopPropagation();
      setRate(video.playbackRate + d * (e.shiftKey ? 0.25 : 0.05));
    });
    speedUi.range = range; speedUi.ticks = ticks;
  }
  function refreshSpeedUi() {
    var r = video.playbackRate;
    el.btnSpeed.textContent = fmtRate(r);
    el.btnSpeed.classList.toggle('is-on', Math.abs(r - 1) > 0.001);
    if (!speedUi.range) return;
    speedUi.read.textContent = fmtRate(r) + (Math.abs(r - 1) < 0.001 ? ' (normal)' : '');
    speedUi.range.value = rateToPos(clamp(r, SPEED_MIN, SPEED_MAX));
    Array.prototype.forEach.call(speedUi.ticks.children, function (t) { t.classList.toggle('is-on', Math.abs(Number(t.dataset.rate) - r) < 0.001); });
  }
  video.addEventListener('ratechange', refreshSpeedUi);
  buildSpeedMenu();
  refreshSpeedUi();

  // ------------------------------------------------------------ subtitles
  var ENTITIES = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': '\'', '&apos;': '\'', '&nbsp;': ' ', '&lrm;': '', '&rlm;': '' };

  function decodeBytes(buf) {
    var u8 = new Uint8Array(buf);
    if (u8.length >= 2 && u8[0] === 0xFF && u8[1] === 0xFE) return new TextDecoder('utf-16le').decode(u8.subarray(2));
    if (u8.length >= 2 && u8[0] === 0xFE && u8[1] === 0xFF) return new TextDecoder('utf-16be').decode(u8.subarray(2));
    try { return new TextDecoder('utf-8', { fatal: true }).decode(u8).replace(/^﻿/, ''); }
    catch (_) { return new TextDecoder('windows-1252').decode(u8); } // old Western-European files
  }

  function parseTime(str) {
    var m = /^(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[.,](\d{1,3}))?$/.exec(String(str).trim());
    if (!m) return NaN;
    var ms = m[4] ? Number((m[4] + '00').slice(0, 3)) : 0;
    return (m[1] ? Number(m[1]) * 3600 : 0) + Number(m[2]) * 60 + Number(m[3]) + ms / 1000;
  }

  // Keeps <i>, <b>, <u>; every other tag and ASS override block is dropped. The result is only ever shown through textContent.
  function cleanText(t) {
    t = t.replace(/\{\\[^}]*\}/g, '')
         .replace(/<(?!\/?[ibu]\s*>)[^>]*>/gi, '')
         .replace(/&(?:amp|lt|gt|quot|apos|nbsp|lrm|rlm|#39);/g, function (m) { return ENTITIES[m]; });
    return t.replace(/\n{2,}/g, '\n').trim();
  }

  function parseSrtVtt(text) {
    var lines = text.replace(/\r\n?/g, '\n').split('\n'), cues = [], i = 0;
    while (i < lines.length) {
      var arrow = lines[i].indexOf('-->');
      if (arrow < 0) { i++; continue; }
      var start = parseTime(lines[i].slice(0, arrow)), end = parseTime(lines[i].slice(arrow + 3).trim().split(/\s+/)[0]);
      var buf = [];
      i++;
      while (i < lines.length && lines[i].trim() !== '') {
        if (lines[i].indexOf('-->') >= 0) { if (buf.length && /^\d+$/.test(buf[buf.length - 1].trim())) buf.pop(); break; }
        buf.push(lines[i]); i++;
      }
      var txt = cleanText(buf.join('\n'));
      if (isFinite(start) && isFinite(end) && txt) cues.push({ start: start, end: end, text: txt });
    }
    return cues;
  }

  function parseAss(text) {
    var lines = text.replace(/\r\n?/g, '\n').split('\n'), inEvents = false, fmtCols = null, cues = [];
    var defaults = ['layer', 'start', 'end', 'style', 'name', 'marginl', 'marginr', 'marginv', 'effect', 'text'];
    lines.forEach(function (raw) {
      var t = raw.trim();
      if (/^\[events\]/i.test(t)) { inEvents = true; return; }
      if (/^\[/.test(t)) { inEvents = false; return; }
      if (!inEvents) return;
      if (/^format:/i.test(t)) { fmtCols = t.slice(7).split(',').map(function (s) { return s.trim().toLowerCase(); }); return; }
      if (!/^dialogue:/i.test(t)) return;
      var cols = fmtCols || defaults, rest = t.slice(9).replace(/^\s+/, ''), parts = [];
      for (var k = 0; k < cols.length - 1; k++) {
        var c = rest.indexOf(',');
        if (c < 0) break;
        parts.push(rest.slice(0, c)); rest = rest.slice(c + 1);
      }
      parts.push(rest);
      var start = parseTime(parts[cols.indexOf('start')] || ''), end = parseTime(parts[cols.indexOf('end')] || '');
      var txt = parts[cols.indexOf('text')] || '';
      if (/\{[^}]*\\p[1-9]/.test(txt)) return; // vector drawing, not text
      txt = cleanText(txt.replace(/\{[^}]*\}/g, '').replace(/\\N/g, '\n').replace(/\\n/g, '\n').replace(/\\h/g, ' '));
      if (isFinite(start) && isFinite(end) && txt) cues.push({ start: start, end: end, text: txt });
    });
    return cues;
  }

  function parseSubtitle(text) {
    var looksAss = /\[Script Info\]|\[Events\]|^\s*Dialogue:/mi.test(text.slice(0, 20000));
    var cues = looksAss ? parseAss(text) : parseSrtVtt(text);
    if (!cues.length) cues = looksAss ? parseSrtVtt(text) : parseAss(text);
    cues.sort(function (a, b) { return a.start - b.start; });
    return cues;
  }

  // ---- tracks: server-side external files plus files the viewer dropped
  var subTracks = [];            // { key, label, kind, id?, cues|null, offset, scale, state: 'idle'|'loading'|'error' }
  var activeTrack = null;
  var lastActiveKey = null;      // for the C shortcut
  var subSize = Number(store.get('mh.subsize')) || 1;

  (cfg.subtitles || []).forEach(function (s) {
    if (!s.supported) return;
    subTracks.push({ key: 's' + s.id, label: (s.lang || 'Subtitle') + ' — ' + s.file, kind: 'server', id: s.id, cues: null, offset: 0, scale: 1, state: 'idle' });
  });

  function timingKey(t) { return 'mh.subtiming.' + cfg.fileId + '.' + t.key; }
  function loadTiming(t) {
    var raw = store.get(timingKey(t));
    if (!raw) return;
    try { var o = JSON.parse(raw); t.offset = Number(o.o) || 0; t.scale = Number(o.s) || 1; } catch (_) { /* ignore corrupt value */ }
  }
  function saveTiming(t) { store.set(timingKey(t), JSON.stringify({ o: t.offset, s: t.scale })); }
  subTracks.forEach(loadTiming);

  function trackByKey(k) { return subTracks.filter(function (t) { return t.key === k; })[0] || null; }

  var userTouched = false;       // once the viewer picks a subtitle (or Off) themselves, nothing is switched on for them any more
  function activate(key, auto) {
    if (!auto) { userTouched = true; store.set('mh.subs.on', key ? '1' : '0'); }   // remembered: subtitles you turned on stay on for the next movie
    if (!key) { activeTrack = null; renderSubs(true); renderSubMenu(); refreshTuner(); refreshSubButton(); return; }
    var t = trackByKey(key);
    if (!t) return;
    if (t.state === 'preparing') { toast('Preparing this subtitle from the video file (first time only). It turns on by itself when ready.'); pendingKey = key; return; }
    activeTrack = t; lastActiveKey = key;
    renderSubMenu(); refreshTuner(); refreshSubButton();
    if (t.cues) { renderSubs(true); kickSubs(); return; }
    if (t.kind !== 'server' && t.kind !== 'embedded') return;
    t.state = 'loading'; renderSubMenu();
    fetch(t.kind === 'server' ? '/media/subtitle/' + t.id : t.url).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.arrayBuffer();
    }).then(function (buf) {
      t.cues = parseSubtitle(decodeBytes(buf));
      t.state = t.cues.length ? 'idle' : 'error';
      if (!t.cues.length) toast('That subtitle file contains no readable lines.');
    }).catch(function () {
      t.state = 'error';
      toast('Could not load the subtitle file from the server.');
    }).then(function () {
      if (activeTrack === t && t.state === 'error') activeTrack = null;
      renderSubMenu(); renderSubs(true); kickSubs(); refreshTuner(); refreshSubButton();
    });
  }

  // ---- showing cues
  var shown = [];
  function cuesAt(t, cues) {
    var lo = 0, hi = cues.length - 1, idx = -1;
    while (lo <= hi) { var mid = (lo + hi) >> 1; if (cues[mid].start <= t) { idx = mid; lo = mid + 1; } else hi = mid - 1; }
    var out = [];
    for (var k = idx; k >= 0 && k > idx - 8; k--) if (cues[k].end > t) out.push(cues[k]);
    return out.reverse();
  }
  function setLineContent(node, text) {
    // <i>/<b>/<u> become styled spans; everything else is plain text (never HTML).
    var flags = { i: false, b: false, u: false };
    text.split(/(<\/?[ibu]\s*>)/i).forEach(function (part) {
      var m = /^<(\/?)([ibu])\s*>$/i.exec(part);
      if (m) { flags[m[2].toLowerCase()] = !m[1]; return; }
      if (!part) return;
      var span = document.createElement('span');
      span.textContent = part;
      if (flags.i) span.style.fontStyle = 'italic';
      if (flags.b) span.style.fontWeight = '700';
      if (flags.u) span.style.textDecoration = 'underline';
      node.appendChild(span);
    });
  }
  function renderSubs(force) {
    var cues = [];
    if (activeTrack && activeTrack.cues) {
      var tc = (position() - activeTrack.offset) / activeTrack.scale; // player time -> time inside the subtitle file
      cues = cuesAt(tc, activeTrack.cues);
    }
    var same = !force && cues.length === shown.length && cues.every(function (c, i) { return c === shown[i]; });
    if (same) return;
    shown = cues;
    el.subs.innerHTML = '';
    cues.forEach(function (c) {
      c.text.split('\n').forEach(function (line) {
        var d = document.createElement('div'); d.className = 'sub-line';
        setLineContent(d, line);
        el.subs.appendChild(d);
      });
    });
  }
  var raf = 0;
  function subTick() { raf = 0; renderSubs(false); if (activeTrack && !video.paused) raf = requestAnimationFrame(subTick); }
  function kickSubs() { if (!raf) raf = requestAnimationFrame(subTick); }
  ['play', 'playing'].forEach(function (ev) { video.addEventListener(ev, kickSubs); });
  ['seeked', 'timeupdate', 'pause', 'ratechange', 'loadedmetadata'].forEach(function (ev) { video.addEventListener(ev, function () { renderSubs(false); }); });

  function applySubSize() {
    el.subs.style.setProperty('--sub-scale', String(subSize));
    el.sizeRead.textContent = Math.round(subSize * 100) + '%';
  }
  applySubSize();

  // ---- subtitles stored inside the video file: the server extracts them once (ffmpeg -> WebVTT, cached on disk); they then behave like any other track
  var LANGS = { eng: 'English', en: 'English', hin: 'Hindi', hi: 'Hindi', ben: 'Bengali', bn: 'Bengali', spa: 'Spanish', es: 'Spanish', fre: 'French', fra: 'French', fr: 'French', ger: 'German', deu: 'German', de: 'German',
    ita: 'Italian', it: 'Italian', jpn: 'Japanese', ja: 'Japanese', kor: 'Korean', ko: 'Korean', chi: 'Chinese', zho: 'Chinese', zh: 'Chinese', ara: 'Arabic', ar: 'Arabic', rus: 'Russian', ru: 'Russian',
    por: 'Portuguese', pt: 'Portuguese', tur: 'Turkish', tr: 'Turkish', tam: 'Tamil', tel: 'Telugu', mal: 'Malayalam', urd: 'Urdu', kan: 'Kannada', nld: 'Dutch', dut: 'Dutch', pol: 'Polish', swe: 'Swedish', ind: 'Indonesian', tha: 'Thai', vie: 'Vietnamese' };
  var embInfo = { preparing: false, failed: false, unavailable: false, unsupported: 0 };
  var pendingKey = null, autoDone = false, embPolls = 0, embTimer = null;

  function isEnglishText(lang, title) {
    return /(^|[^a-z])(english|eng|en)([^a-z]|$)/i.test((lang || '') + ' ' + (title || ''));
  }
  function embLabel(t, n) {
    var lang = t.lang ? (LANGS[String(t.lang).toLowerCase()] || t.lang) : '';
    if (/^und$/i.test(lang)) lang = '';
    var title = t.title || '';
    var base = title && lang && title.toLowerCase().indexOf(lang.toLowerCase()) >= 0 ? title : [lang, title].filter(Boolean).join(' · ');
    return (base || 'Subtitle ' + n) + ' (in video)';
  }
  function applyEmbedded(tracks) {
    embInfo = { preparing: false, failed: false, unavailable: false, unsupported: 0 };
    var n = 0;
    tracks.forEach(function (e) {
      n++;
      if (!e.supported) { embInfo.unsupported++; return; }
      var key = 'e' + e.index, t = trackByKey(key);
      var state = e.state === 'ready' ? 'idle' : e.state === 'preparing' ? 'preparing' : 'error';
      if (e.state === 'failed') embInfo.failed = true;
      if (e.state === 'unavailable') embInfo.unavailable = true;
      if (e.state === 'preparing') embInfo.preparing = true;
      if (!t) {
        t = { key: key, label: embLabel(e, n), kind: 'embedded', url: '/media/embsub/' + cfg.fileId + '/' + e.index, lang: e.lang, title: e.title, cues: null, offset: 0, scale: 1, state: state };
        loadTiming(t); subTracks.push(t);
      } else if (t.state === 'preparing' || t.state === 'error') { t.state = state; }
    });
    renderSubMenu();
    if (pendingKey) { var pt = trackByKey(pendingKey); if (pt && pt.state === 'idle') { var k = pendingKey; pendingKey = null; activate(k, true); } }
    autoSubtitles();
  }
  function bestEnglishTrack() {
    var c = subTracks.filter(function (t) { return t.kind !== 'file' && t.state !== 'error' && isEnglishText(t.lang || t.label, t.title || t.label); });
    function rank(t) { var x = (t.title || t.label || '').toLowerCase(); return (/forced|commentary/.test(x) ? 4 : 0) + (/sdh|hearing|cc/.test(x) ? 1 : 0) + (t.kind === 'embedded' ? 0 : 0.5); }
    c.sort(function (a, b) { return rank(a) - rank(b); });
    return c[0] || null;
  }
  // Subtitles come on by themselves when they will actually be needed: the movie will not be played with English audio, or the viewer left them on last time.
  function wantSubtitles() {
    var saved = store.get('mh.subs.on');
    if (saved === '1') return true;
    if (saved === '0') return false;
    var target = audioTracks.filter(function (a) { return a.index === (cfg.preferredAudio != null ? cfg.preferredAudio : currentAudioIndex()); })[0];
    return !!target && !isEnglishText(target.lang, target.title);
  }
  function autoSubtitles() {
    if (autoDone || userTouched || activeTrack) return;
    var best = bestEnglishTrack();
    if (!best) return;
    if (best.state === 'preparing') return;      // check again when it is ready
    autoDone = true;
    if (wantSubtitles()) activate(best.key, true);
  }
  function loadEmbedded() {
    if (embTimer) { clearTimeout(embTimer); embTimer = null; }
    fetch('/media/embsub/' + cfg.fileId, { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
      .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(function (d) {
        applyEmbedded(d.tracks || []);
        if (embInfo.preparing && ++embPolls < 600) embTimer = setTimeout(loadEmbedded, 3000);
      })
      .catch(function () { /* the menu still offers file loading */ });
  }
  if (cfg.embeddedSubtitles || !cfg.inspected) loadEmbedded();
  autoSubtitles();

  // ---- subtitle menu
  function refreshSubButton() {
    el.btnSub.classList.toggle('is-on', !!activeTrack);
    el.btnSub.title = activeTrack ? 'Subtitles: ' + activeTrack.label + ' (C to turn off)' : 'Subtitles (C)';
  }
  function renderSubMenu() {
    var m = el.menuSub; m.innerHTML = '';
    heading(m, 'Subtitles');
    item(m, 'Off', !activeTrack, function () { activate(null); });
    subTracks.forEach(function (t) {
      var label = t.label + (t.state === 'loading' ? ' (loading…)' : t.state === 'error' ? ' (failed)' : '');
      item(m, label, activeTrack === t, function () { activate(t.key); });
    });
    item(m, '＋ Load subtitle file…', false, function () { el.file.click(); });
    item(m, '⏱ Timing & size…', false, function () { openTuner(); });
    var emb = embInfo;
    if (emb.preparing) note(m, 'Preparing the subtitles inside the video… (first time only; they are saved on the server afterwards)');
    if (emb.failed) note(m, 'The subtitles inside this video could not be read. You can still load a subtitle file.');
    if (emb.unavailable) note(m, 'Subtitles inside the video need ffmpeg on the server (Admin › Dashboard › Download FFmpeg).');
    if (emb.unsupported) note(m, emb.unsupported + ' image-based subtitle track(s) (Blu-ray / DVD style) cannot be turned into text, so they cannot be shown here.');
    if (!subTracks.length && !emb.preparing && !emb.unsupported && !emb.unavailable) note(m, 'No subtitles found for this movie. Load a .srt, .vtt or .ass file, or drop one onto the video.');
    var tip = document.getElementById('sub-tip');
    if (tip) tip.hidden = !!subTracks.length || emb.preparing;
  }
  renderSubMenu();
  refreshSubButton();

  // ---- loading a file the viewer chose or dropped
  function loadSubtitleFile(file) {
    if (!file) return;
    if (!/\.(srt|vtt|ass|ssa)$/i.test(file.name)) { toast('Unsupported file type. Use .srt, .vtt, .ass or .ssa.'); return; }
    if (file.size > 5 * 1024 * 1024) { toast('That file is too large to be a subtitle file.'); return; }
    file.arrayBuffer().then(function (buf) {
      var cues = parseSubtitle(decodeBytes(buf));
      if (!cues.length) { toast('No subtitle lines could be read from ' + file.name + '.'); return; }
      var key = 'f:' + file.name + ':' + file.size;
      var t = trackByKey(key);
      if (!t) {
        t = { key: key, label: file.name, kind: 'file', cues: cues, offset: 0, scale: 1, state: 'idle' };
        loadTiming(t);                       // the same file dropped again on a later visit keeps its tuning
        subTracks.push(t);
      } else { t.cues = cues; }
      activate(key);
      toast('Subtitles loaded: ' + file.name + ' (' + cues.length + ' lines)', 'Adjust timing', openTuner);
    }).catch(function () { toast('Could not read ' + file.name + '.'); });
  }
  el.file.addEventListener('change', function () { loadSubtitleFile(el.file.files[0]); el.file.value = ''; });

  var dropZone = player.closest('.watch-page') || player;
  var dragDepth = 0;
  function hasFiles(e) { return e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types || [], 'Files') >= 0; }
  dropZone.addEventListener('dragenter', function (e) { if (!hasFiles(e)) return; e.preventDefault(); dragDepth++; player.classList.add('is-dropping'); });
  dropZone.addEventListener('dragover', function (e) { if (!hasFiles(e)) return; e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; });
  dropZone.addEventListener('dragleave', function (e) { if (!hasFiles(e)) return; dragDepth = Math.max(0, dragDepth - 1); if (!dragDepth) player.classList.remove('is-dropping'); });
  dropZone.addEventListener('drop', function (e) {
    if (!hasFiles(e)) return;
    e.preventDefault(); dragDepth = 0; player.classList.remove('is-dropping');
    var files = Array.prototype.slice.call(e.dataTransfer.files || []);
    var pick = files.filter(function (f) { return /\.(srt|vtt|ass|ssa)$/i.test(f.name); })[0] || files[0];
    loadSubtitleFile(pick);
  });
  // A file dropped anywhere else would make the browser navigate away from the movie.
  window.addEventListener('dragover', function (e) { if (hasFiles(e)) e.preventDefault(); });
  window.addEventListener('drop', function (e) { if (hasFiles(e)) e.preventDefault(); });

  // ---- timing & size panel
  function openTuner() { el.tuner.hidden = false; refreshTuner(); wake(); }
  function closeTuner() { el.tuner.hidden = true; }
  el.tunerClose.addEventListener('click', closeTuner);
  function fmtOffset(o) { return (o < 0 ? '−' : '+') + Math.abs(o).toFixed(2) + ' s'; }
  function refreshTuner() {
    var t = activeTrack;
    el.tuner.classList.toggle('is-inactive', !t);
    el.tunerTrack.textContent = t ? t.label : 'No subtitle selected. Pick one from the CC menu or drop a file.';
    el.tunerOffset.textContent = fmtOffset(t ? t.offset : 0);
    el.tunerSlider.value = t ? clamp(t.offset, -60, 60) : 0;
    var best = '1', bestD = Infinity;
    Array.prototype.forEach.call(el.tunerScale.options, function (o) { var d = Math.abs(Number(o.value) - (t ? t.scale : 1)); if (d < bestD) { bestD = d; best = o.value; } });
    el.tunerScale.value = best;
    [].forEach.call(el.tuner.querySelectorAll('button[data-step], #tuner-slider, #tuner-sync, #tuner-scale, #tuner-reset'), function (c) { c.disabled = !t; });
  }
  function setTiming(o, s) {
    var t = activeTrack; if (!t) return;
    t.offset = clamp(Math.round(o * 100) / 100, -3600, 3600);
    if (s !== undefined) t.scale = s;
    saveTiming(t); refreshTuner(); renderSubs(true);
  }
  function nudge(d) { if (activeTrack) setTiming(activeTrack.offset + d); }
  Array.prototype.forEach.call(el.tuner.querySelectorAll('button[data-step]'), function (b) {
    b.addEventListener('click', function () { nudge(Number(b.dataset.step)); });
  });
  el.tunerSlider.addEventListener('input', function () { setTiming(Number(el.tunerSlider.value)); });
  el.tunerScale.addEventListener('change', function () {
    var t = activeTrack; if (!t) return;
    // Keep the line that is on screen right now where it is while the time scale changes.
    var p = position(), fileTime = (p - t.offset) / t.scale, s = Number(el.tunerScale.value);
    setTiming(p - fileTime * s, s);
  });
  el.tunerSync.addEventListener('click', function () {
    var t = activeTrack; if (!t || !t.cues || !t.cues.length) return;
    var p = position(), best = null, bestD = Infinity;
    t.cues.forEach(function (c) { var d = Math.abs(c.start * t.scale + t.offset - p); if (d < bestD) { bestD = d; best = c; } });
    if (!best || bestD > 120) { toast('No subtitle line within two minutes of this moment.'); return; }
    setTiming(p - best.start * t.scale);
    toast('Aligned: “' + best.text.replace(/\s+/g, ' ').slice(0, 48) + '” starts now (' + fmtOffset(activeTrack.offset) + ').');
  });
  el.tunerReset.addEventListener('click', function () { setTiming(0, 1); });
  function setSize(v) { subSize = clamp(Math.round(v * 20) / 20, 0.5, 2.5); store.set('mh.subsize', String(subSize)); applySubSize(); }
  el.sizeDown.addEventListener('click', function () { setSize(subSize - 0.1); });
  el.sizeUp.addEventListener('click', function () { setSize(subSize + 0.1); });
  refreshTuner();

  // ------------------------------------------------------------ audio
  function detectNativeAudio() { nativeAudio = !!(video.audioTracks && video.audioTracks.length > 1); }

  function label(a, i) {
    // MKV titles often carry the useful detail ("English DD 5.1"); use the title alone when it already names the language.
    var lang = a.lang || '', title = a.title || '';
    var base = title && lang && title.toLowerCase().indexOf(lang.toLowerCase()) >= 0 ? title : [lang, title].filter(Boolean).join(' · ');
    var parts = [base || ('Track ' + (i + 1))];
    if (a.codec) parts.push(a.codec.toUpperCase());
    if (a.channels && a.channels > 1) parts.push(a.channels === 6 ? '5.1' : a.channels === 8 ? '7.1' : a.channels === 2 ? 'stereo' : a.channels + 'ch');
    return parts.join(' · ');
  }
  function currentAudioIndex() {
    if (audioIndex !== null) return audioIndex;
    if (mode === 'remux' && cfg.preferredAudio != null) return cfg.preferredAudio;   // the server also starts with English
    if (cfg.defaultAudio != null) return cfg.defaultAudio;
    return audioTracks.length ? audioTracks[0].index : null;
  }
  function isEnglishNative(t) {
    var s = ((t.language || '') + ' ' + (t.label || '')).toLowerCase();
    return /(^|[^a-z])(en|eng|english)([^a-z]|$)/.test(s);
  }
  // Safari exposes audioTracks: pick English there directly (no server work). Other browsers are handled by the remux start.
  function preferEnglishNatively() {
    if (!nativeAudio || mode === 'remux') return;
    var idx = -1, i;
    for (i = 0; i < video.audioTracks.length; i++) if (isEnglishNative(video.audioTracks[i])) { idx = i; break; }
    if (idx < 0) return;
    for (i = 0; i < video.audioTracks.length; i++) video.audioTracks[i].enabled = (i === idx);
  }

  function renderAudioMenu() {
    var m = el.menuAudio; m.innerHTML = '';
    heading(m, 'Audio');
    var many = audioTracks.length > 1;
    // The button is always present so it is never a mystery why there is no audio switch; the menu explains the situation.
    el.btnAudio.parentElement.style.display = '';
    el.btnAudio.disabled = false;
    el.btnAudio.classList.toggle('is-dim', !many && !nativeAudio);

    if (nativeAudio) {
      for (var i = 0; i < video.audioTracks.length; i++) {
        (function (idx) {
          var t = video.audioTracks[idx];
          item(m, t.label || t.language || ('Track ' + (idx + 1)), t.enabled, function () {
            for (var k = 0; k < video.audioTracks.length; k++) video.audioTracks[k].enabled = (k === idx);
            renderAudioMenu();
          });
        })(i);
      }
      return;
    }

    if (!many) {
      if (inspecting) {
        note(m, 'Reading the audio tracks from the file…');
      } else if (audioTracks.length === 1) {
        var only = document.createElement('div'); only.className = 'note'; only.textContent = '• ' + label(audioTracks[0], 0); m.appendChild(only);
        note(m, 'This file has only one audio track, so there is nothing to switch to. A different file of this movie (see the file list under the player) may have other languages.');
      } else if (!cfg.inspected) {
        note(m, 'The audio tracks of this file are unknown because the server could not read them' +
          (cfg.inspectionError ? ': ' + cfg.inspectionError : '') +
          '. An administrator can check Dashboard → Media tools (FFprobe must be found), then reopen this page.');
      } else {
        note(m, 'No audio information is available for this file.');
      }
      return;
    }

    var current = currentAudioIndex();
    if (cfg.remuxEnabled) {
      audioTracks.forEach(function (a, i) {
        item(m, label(a, i), a.index === current, function () { chooseAudio(a.index); });
      });
      note(m, 'Switching restarts playback at the current position; the server converts the sound to stereo AAC.');
    } else {
      audioTracks.forEach(function (a, i) { var b = document.createElement('div'); b.className = 'note'; b.textContent = '• ' + label(a, i); m.appendChild(b); });
      note(m, 'This file has several audio tracks, but this browser cannot switch between them and the server has no ffmpeg. Install FFmpeg (it ships with FFprobe) or set Media:FfmpegPath, then restart MovieHub.');
    }
  }

  function chooseAudio(index) {
    if (index === currentAudioIndex() && mode === 'remux') return;
    var at = lazyRemux ? 0 : position();
    lazyRemux = false;
    if (mode !== 'remux') prevMode = mode;
    audioIndex = index;
    triedModes.remux = true;
    load('remux', at, true);
    renderAudioMenu();
  }

  // ------------------------------------------------------------ idle controls + keyboard
  var idleTimer;
  function wake() {
    player.classList.remove('is-idle');
    clearTimeout(idleTimer);
    if (!video.paused && el.tuner.hidden) idleTimer = setTimeout(function () { player.classList.add('is-idle'); }, 3000);
  }
  ['mousemove', 'touchstart', 'keydown'].forEach(function (ev) { player.addEventListener(ev, wake, { passive: true }); });
  video.addEventListener('play', wake);

  player.addEventListener('keydown', function (e) {
    if (e.target.closest && (e.target.closest('.tuner') || e.target.closest('.menu'))) {
      if (e.key === 'Escape') { closeTuner(); closeMenus(); }
      return; // the panels' own controls handle their keys
    }
    if (e.target.tagName === 'INPUT' && e.target.type !== 'range') return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    var handled = true, key = e.key;
    if (e.shiftKey && e.code === 'Period') key = '>';   // layout-independent: Shift + . / Shift + ,
    if (e.shiftKey && e.code === 'Comma') key = '<';
    switch (key) {
      case ' ': case 'k': togglePlay(); break;
      case 'ArrowRight': seekTo(position() + 10); break;
      case 'ArrowLeft': seekTo(position() - 10); break;
      case 'ArrowUp': video.volume = Math.min(1, video.volume + 0.1); break;
      case 'ArrowDown': video.volume = Math.max(0, video.volume - 0.1); break;
      case 'm': video.muted = !video.muted; break;
      case 'f': toggleFs(); break;
      case 'c': case 'C':
        if (activeTrack) activate(null);
        else if (lastActiveKey || subTracks.length) activate(lastActiveKey || subTracks[0].key);
        else toast(embInfo.preparing ? 'Subtitles from the video are still being prepared.' : 'No subtitles for this movie. Load a subtitle file or drop one onto the video.');
        break;
      case 'g': case 'G': if (activeTrack) { nudge(-0.1); toast('Subtitle timing ' + fmtOffset(activeTrack.offset)); } break;
      case 'h': case 'H': if (activeTrack) { nudge(0.1); toast('Subtitle timing ' + fmtOffset(activeTrack.offset)); } break;
      case '<': stepSnap(-1); break;
      case '>': stepSnap(1); break;
      case 'Escape': closeTuner(); closeMenus(); handled = false; break;
      default: handled = false;
    }
    if (handled) e.preventDefault();
  });

  // ------------------------------------------------------------ files without stream information
  // A failed or missing ffprobe run at scan time leaves a file without tracks. Ask the server to inspect it now so the audio list appears.
  var inspecting = false;
  function startLazyEnglish() {
    clearTimeout(loadTimer);
    lazyRemux = true; mode = 'remux'; offset = 0;
    video.pause(); video.removeAttribute('src'); video.load();
    audioIndex = cfg.preferredAudio; prevMode = 'direct'; triedModes.remux = true;
    player.classList.remove('is-loading');
    setStatus('Ready. Starts with the English audio track (compatibility mode converts the stream when you press play).');
    updateTime();
  }
  function inspectNow() {
    inspecting = true; renderAudioMenu();
    fetch('/media/inspect/' + cfg.fileId, { method: 'POST', headers: { 'X-Requested-With': 'fetch' } })
      .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(function (d) {
        inspecting = false;
        cfg.inspected = d.inspected; cfg.inspectionError = d.inspectionError;
        cfg.preferredAudio = d.preferredAudio; cfg.defaultAudio = d.defaultAudio; cfg.embeddedSubtitles = d.embeddedSubtitles;
        cfg.audio = audioTracks = d.audio || [];
        if (d.embeddedSubtitles) loadEmbedded();
        if (d.videoCodec) cfg.videoCodec = d.videoCodec;
        if (d.audioCodec) cfg.audioCodec = d.audioCodec;
        if (!knownDuration && d.duration) { knownDuration = d.duration; updateTime(); }
        renderAudioMenu(); renderSubMenu();
        var needEnglish = audioTracks.length > 1 && cfg.preferredAudio != null && cfg.preferredAudio !== cfg.defaultAudio;
        if (!needEnglish) return;
        if (cfg.remuxEnabled && mode === 'direct' && video.paused && position() < 0.5 && !lazyRemux) startLazyEnglish();
        else if (currentAudioIndex() !== cfg.preferredAudio) toast('English audio found. Use the 🎧 menu to switch to it.');
        renderAudioMenu();
      })
      .catch(function () { inspecting = false; renderAudioMenu(); });
  }

  // ------------------------------------------------------------ start
  var needEnglishSwitch = audioTracks.length > 1 && cfg.preferredAudio != null && cfg.preferredAudio !== cfg.defaultAudio;
  if (needEnglishSwitch && cfg.remuxEnabled && cfg.directUrl) {
    // The file's default track is not English: start from a remux that carries the English track. It begins on first Play.
    audioIndex = cfg.preferredAudio;
    prevMode = 'direct';
    triedModes.remux = true;
    lazyRemux = true;
    mode = 'remux';
    player.classList.remove('is-loading');
    setStatus('Ready. Starts with the English audio track (compatibility mode converts the stream when you press play).');
    renderAudioMenu();
    updateTime();
  } else {
    load('direct', 0, false);
  }
  if (!cfg.inspected) inspectNow();
})();
