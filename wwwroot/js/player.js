/* StreamApp player.
 *
 * Source chain: direct ISP URL -> range-aware proxy (/media/stream) -> ffmpeg remux (/media/remux) -> clear message.
 *
 * Audio:     files with several audio tracks start with the English one. Browsers other than Safari cannot switch tracks themselves,
 *            so the server remuxes the stream with the wanted track (ffmpeg, auto-detected). Native video.audioTracks is used when it exists.
 * Subtitles: rendered by this script (not by <track>), so they stay correct after a remux restart. Tracks inside the video file are extracted
 *            by the server once (/media/embsub), kept in this browser (IndexedDB) and loaded automatically; a dropped or chosen file is the fallback when a movie has none,
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
    btnSub: $('btn-sub'), btnAudio: $('btn-audio'), btnSpeed: $('btn-speed'), btnMethod: $('btn-method'), rowSub: $('row-sub'),
    menuSub: $('menu-sub'), menuAudio: $('menu-audio'), menuSpeed: $('menu-speed'),
    valSub: $('val-sub'), valAudio: $('val-audio'), valSpeed: $('val-speed'), valMethod: $('val-method'),
    settings: $('settings'), scrim: $('settings-scrim'), setBack: $('set-back'), setTitle: $('set-title'), setClose: $('set-close'),
    bigPlay: $('big-play'), played: $('played'), seekWrap: $('seek-wrap'), seekTip: $('seek-tip'),
    msg: $('player-message'), msgTitle: $('msg-title'), msgText: $('msg-text'), retry: $('msg-retry'), status: $('stream-status'),
    subs: $('subs'), toast: $('player-toast'), file: $('sub-file'),
    tuner: $('tuner'), tunerClose: $('tuner-close'), tunerTrack: $('tuner-track'), tunerOffset: $('tuner-offset'),
    tunerSlider: $('tuner-slider'), tunerSync: $('tuner-sync'), tunerScale: $('tuner-scale'), tunerReset: $('tuner-reset'),
    sizeDown: $('size-down'), sizeUp: $('size-up'), sizeRead: $('size-read'), compatSwitch: $('compat-switch'), compatState: $('compat-state'), compatHint: $('compat-hint'), speedRow: $('speed-row'), speedBtns: $('speed-btns')
  };

  var mode = 'direct';          // direct | proxy | remux
  var prevMode = null;          // where to go back to if a user-chosen audio remux fails
  var offset = 0;               // seconds already skipped by the remux stream (-ss)
  var audioIndex = null;        // chosen ffprobe audio stream index (remux mode)
  var triedModes = {};
  var copyStart = null;          // keyframe the copied picture starts at after a seek (from the server)
  var oneShotEncode = false;     // the server said this one stream must be re-encoded (the copied picture could not start where requested)
  var autoPicture = false;      // the player itself moved this page view off a copied picture the browser could not show (only explains the status line; it locks nothing)
  // One random id per page view: the server stops this viewer's previous compatibility stream when a new one starts (reconnect, seek, method change).
  var reloadN = 0;   // reconnects so far (see recover); makes each reconnect's address different
  var sid = (function () {
    try { var a = new Uint8Array(8); crypto.getRandomValues(a); return [].map.call(a, function (x) { return ('0' + x.toString(16)).slice(-2); }).join(''); }
    catch (_) { return Date.now().toString(36) + Math.random().toString(36).slice(2, 10); }
  })();
  var nativeAudio = false;
  var lazyRemux = false;        // English start needs a remux: it begins on the first Play, so a page view never occupies an ffmpeg slot
  var knownDuration = cfg.duration || 0;
  var audioTracks = cfg.audio || [];

  var store = {
    get: function (k) { try { return window.localStorage.getItem(k); } catch (_) { return null; } },
    set: function (k, v) { try { window.localStorage.setItem(k, v); } catch (_) { /* storage unavailable */ } }
  };

  // Icons that change while playing (the fixed ones are in the page).
  function svg(path, cls) { return '<svg class="' + (cls || 'st') + '" viewBox="0 0 24 24" aria-hidden="true">' + path + '</svg>'; }
  var ICON = {
    play: svg('<path d="M8 5v14l11-7z"/>', 'fl'),
    pause: svg('<path d="M6 5h4v14H6zM14 5h4v14h-4z"/>', 'fl'),
    vol: svg('<path d="M4 9.5v5h3.5L12 18.5v-13L7.5 9.5z"/><path d="M15.5 9a4 4 0 0 1 0 6M18 6.5a7.5 7.5 0 0 1 0 11"/>'),
    volLow: svg('<path d="M4 9.5v5h3.5L12 18.5v-13L7.5 9.5z"/><path d="M15.5 9a4 4 0 0 1 0 6"/>'),
    mute: svg('<path d="M4 9.5v5h3.5L12 18.5v-13L7.5 9.5z"/><path d="M16 9.5l5 5M21 9.5l-5 5"/>'),
    fs: svg('<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>'),
    fsExit: svg('<path d="M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5"/>')
  };

  function fmt(s) {
    if (!isFinite(s) || s < 0) s = 0;
    var h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = Math.floor(s % 60);
    return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(sec).padStart(2, '0');
  }
  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
  function duration() { return knownDuration || (isFinite(video.duration) ? video.duration + offset : 0); }
  // After a jump in compatibility mode the new stream takes a moment to arrive (the server looks up where to restart first). Until it does, the clicked point is
  // the position: the bar, the clock and the subtitles go there at once instead of waiting, and the old stream playing on does not pull them back.
  var pendingPos = null, awaitingNew = false, startAbort = null;
  function position() { return pendingPos !== null ? pendingPos : offset + (video.currentTime || 0); }
  // Where to carry on after a failure. Chrome puts the clock back to 0 when a stream errors, so the last position seen while playing is used instead.
  var lastPos = 0;
  function resumePos() { return pendingPos !== null ? pendingPos : (video.error || video.readyState === 0) ? lastPos : position(); }
  function settleJump() { pendingPos = null; awaitingNew = false; }
  function setStatus(t) { if (el.status) el.status.textContent = t; }

  // What an administrator has hidden from visitors (Admin > Site): technical details and tips are simply not shown, the player works the same.
  var ui = cfg.ui || {};
  function techToast(text) { if (!ui.hideTech) toast(text); }
  function hintToast(text) { if (!ui.hideHints) toast(text); }
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
    var q = ['s=' + sid];
    if (audioIndex !== null) q.push('audio=' + audioIndex);
    if (isHevc(cfg.videoCodec)) q.push('hevc=1');   // HEVC may be copied (when the method asks for it); whether this browser can show it only advises the default method
    if (oneShotEncode || !pictureCopyOn()) q.push('video=encode');
    else if (m === 'remux' && startAt > 0 && copyStart !== null) q.push('k=' + copyStart.toFixed(3));
    if (audioCopyOn()) q.push('acopy=1');   // copy the sound as it is (the server still converts it when its timing would not line up)
    if (startAt > 0) q.push('t=' + startAt.toFixed(2));
    if (reloadN > 0) q.push('r=' + reloadN);   // a reconnect asks for a new address: Chrome may otherwise answer from what it kept of the failed one
    return cfg.remuxUrl + (q.length ? '?' + q.join('&') : '');
  }

  // Starts playback. A browser may refuse to start a movie with sound before the page was interacted with; then it starts muted (with an Unmute button) instead of staying stopped.
  function startPlay() {
    var p = video.play();
    if (!p || !p.catch) return;
    p.catch(function (err) {
      if (err && err.name === 'AbortError') return;                      // a newer load() replaced this attempt
      if (err && err.name === 'NotAllowedError' && !video.muted) {
        video.muted = true;
        video.play().then(function () {
          toast('Playing muted: your browser blocked sound.', 'Unmute', function () { video.muted = false; });
        }).catch(function () { video.muted = false; });
      }
    });
  }

  // An https page (e.g. through a Cloudflare tunnel) may not load an http:// source: the browser blocks it silently, so go straight to the server relay.
  function directUsable() { return !!cfg.directUrl && !(location.protocol === 'https:' && /^http:/i.test(cfg.directUrl)); }

  // A converted stream whose picture is copied can only begin on a keyframe. The server tells where that is, and the sound is then started at the same point,
  // so picture and sound stay together after a seek. (It may start a few seconds before the requested time, as in every player that copies the picture.)
  var loadSeq = 0;
  // What the server's ffmpeg is doing right now, so a busy CPU is explained: copying the picture is cheap, re-encoding it is not.
  function remuxDescription() {
    var codec = String(cfg.videoCodec || '').toUpperCase();
    var why = cfg.alwaysEncode ? 'the server is set to always re-encode the picture'
      : !copyPossible() ? (codec ? codec + ' video cannot be copied by the server' : 'the video format is not known')
      : !pictureCopyOn() && !pinned && !copyAdvised() ? codec + ' video cannot be copied for this browser'
      : autoPicture ? 'this browser could not show the copied picture (for example 10-bit video)'
      : oneShotEncode ? 'a copied picture cannot start exactly at the point you jumped to'
      : '';
    var sound = audioCopyOn() ? 'copies the sound when it can' : 'converts the sound';
    if (!why && !pictureCopyOn()) why = 'chosen with the stream method buttons';
    return why ? 'Compatibility mode: the server is re-encoding the picture (' + why + ') and ' + sound + '. '
                 + (cfg.hwEncoder ? 'The re-encode runs on the server\'s graphics hardware (' + cfg.hwEncoder + '), so it is lighter on the CPU.' : 'This uses a lot of server CPU.')
               : audioCopyOn() ? 'Compatibility mode: the server copies the picture and the sound (very light on CPU); the sound is converted only when its timing needs fixing.'
               : 'Compatibility mode: the server copies the picture and only converts the audio (light on CPU).';
  }

  // ------------------------------------------------------------ speed: how much the server converts
  // Fastest = picture and sound copied, Fast = sound converted, Slow = picture re-encoded (sound copied), Slowest = both converted.
  // Almost every movie works with Fastest. The slower ones are for the rare file whose picture or sound does not show or play right with a copy.
  var SPEEDS = ['fastest', 'fast', 'slow', 'slowest'];
  var SPEED_LABEL = { fastest: 'Fastest', fast: 'Fast', slow: 'Slow', slowest: 'Slowest' };
  var SPEED_KEY = 'sa.speed.' + cfg.fileId;
  var speedMode = (function () { var v = store.get(SPEED_KEY); return SPEEDS.indexOf(v) >= 0 ? v : 'fastest'; })();
  // A method the viewer chose (now or on an earlier visit) is never changed by the player: if it does not work, the player says so and leaves it, so it can be looked into.
  // The player only moves by itself off the default (Fastest), and never closes anything: every method that is possible for the movie stays selectable.
  var pinned = SPEEDS.indexOf(store.get(SPEED_KEY)) >= 0;
  // Sound this browser can play inside an MP4 as it is. The browser says so itself (AC3 is often missing, AAC and MP3 nearly never).
  var AUDIO_MP4 = { aac: 'mp4a.40.2', mp3: 'mp4a.6B', ac3: 'ac-3', eac3: 'ec-3' };
  function audioCodecOk(codec) {
    var cs = AUDIO_MP4[String(codec || '').toLowerCase()];
    try { return !!cs && !!video.canPlayType && video.canPlayType('audio/mp4; codecs="' + cs + '"') !== ''; } catch (_) { return false; }
  }
  function chosenAudioTrack() {
    var idx = currentAudioIndex();
    for (var i = 0; i < audioTracks.length; i++) if (audioTracks[i].index === idx) return audioTracks[i];
    return null;
  }
  // What the server can copy (selectable) against what this browser says it can play (only advice for the default method). The browser's own claim is wrong often enough,
  // in both directions, that it must not close an option the viewer might want to try.
  function audioCopyPossible() {
    var a = chosenAudioTrack();
    return cfg.copyAudio !== false && !!a && /^(aac|mp3|ac3|eac3)$/.test(String(a.codec || '').toLowerCase()) && a.channels >= 1 && a.channels <= 8;
  }
  function audioCopyAdvised() { var a = chosenAudioTrack(); return audioCopyPossible() && audioCodecOk(a.codec); }
  function speedAllowed(m) {
    return m === 'fastest' ? copyPossible() && audioCopyPossible()
         : m === 'fast' ? copyPossible()
         : m === 'slow' ? audioCopyPossible()
         : true;
  }
  function speedAdvised(m) {
    return m === 'fastest' ? copyAdvised() && audioCopyAdvised()
         : m === 'fast' ? copyAdvised()
         : m === 'slow' ? audioCopyAdvised()
         : true;
  }
  // The chosen speed, or the next slower one when this movie cannot do the chosen one (for example a picture the browser can only show re-encoded).
  // A method the viewer chose is used as it is (when the server can do it at all); otherwise the first method from the default that this browser is expected to play.
  function effectiveSpeed() {
    var ok = pinned ? speedAllowed : speedAdvised;
    for (var i = Math.max(0, SPEEDS.indexOf(speedMode)); i < SPEEDS.length; i++) if (ok(SPEEDS[i])) return SPEEDS[i];
    return 'slowest';
  }
  function pictureCopyOn() { var e = effectiveSpeed(); return (e === 'fastest' || e === 'fast') && copyPossible(); }
  function audioCopyOn() { var e = effectiveSpeed(); return (e === 'fastest' || e === 'slow') && audioCopyPossible(); }
  function renderSpeed() {
    if (!el.speedRow) return;
    var show = mode === 'remux' && cfg.remuxEnabled;
    el.speedRow.hidden = !show;
    if (el.btnMethod) el.btnMethod.hidden = !show;
    if (!show) { if ($('view-method').classList.contains('is-on')) showView('set-home'); return; }
    var eff = effectiveSpeed();
    setVal(el.valMethod, SPEED_LABEL[eff]);
    [].forEach.call(el.speedBtns.querySelectorAll('button'), function (b) {
      var m = b.getAttribute('data-speed'), ok = speedAllowed(m);
      b.disabled = !ok;
      b.setAttribute('aria-pressed', m === eff ? 'true' : 'false');
      b.classList.toggle('is-on', m === eff);
      b.title = !ok ? 'Not available for this movie' : speedAdvised(m) ? '' : 'This browser says it may not play this directly. You can still try it.';
    });
  }
  function setSpeed(m, quiet) {
    if (SPEEDS.indexOf(m) < 0 || !speedAllowed(m)) return;
    if (m === effectiveSpeed()) { if (!pinned) { pinned = true; speedMode = m; store.set(SPEED_KEY, m); } return; }   // the method in use, kept as the viewer's choice
    speedMode = m; pinned = true; autoPicture = false; store.set(SPEED_KEY, m);
    renderSpeed();
    if (mode === 'remux' && !lazyRemux) { if (!quiet) hintToast('Stream method: ' + SPEED_LABEL[m]); load('remux', position(), true); }
  }
  if (el.speedBtns) el.speedBtns.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('button[data-speed]'); if (b && !b.disabled) setSpeed(b.getAttribute('data-speed'));
  });
  // ------------------------------------------------------------ judging a stream by evidence, not by a stopwatch
  // A slow or interrupted connection looks, from the outside, like a codec the browser cannot play: nothing shows or sounds. So the player never judges a codec by
  // how long something took. It looks at what the browser has actually done with the data it received:
  //   - decoded sound / decoded picture frames (counters in Chrome, Edge and Safari), judged only after PLAYED_ENOUGH seconds of the movie really played,
  //     because playing that far proves the data arrived;
  //   - the picture size when the file's header has been read ('loadedmetadata'), which the browser sets as soon as it knows the picture track is usable;
  //   - the kind of error: a network error, or a decode error after both sound and picture decoded fine, is damaged or interrupted delivery, not a codec problem.
  // Delivery trouble is answered with the same stream method again (see recover); only proven decoding trouble changes the method, and only the part that failed.
  var PLAYED_ENOUGH = 1.5;
  function decodedAudio() { return typeof video.webkitAudioDecodedByteCount === 'number' ? video.webkitAudioDecodedByteCount : null; }
  function decodedFrames() {
    try { if (video.getVideoPlaybackQuality) { var q = video.getVideoPlaybackQuality(); if (q && typeof q.totalVideoFrames === 'number') return q.totalVideoFrames; } } catch (_) { /* not available */ }
    return typeof video.webkitDecodedFrameCount === 'number' ? video.webkitDecodedFrameCount : null;
  }
  var metaSeq = 0, playingSeq = 0, playedFrom = 0, healthySeq = 0, judgedSeq = 0;   // which load() reached what (see the video events below)
  function playedSeconds() { return playingSeq === loadSeq ? video.currentTime - playedFrom : 0; }

  // What the player saw when it changed a method, for whoever is working out why the cheaper one failed (browser console).
  function logStep(what) {
    try { console.info('[stream] ' + what, { mode: mode, audioDecoded: decodedAudio(), pictureFrames: decodedFrames(), played: +playedSeconds().toFixed(2), error: video.error ? video.error.message : null, size: video.videoWidth + 'x' + video.videoHeight, attempts: attempts }); } catch (_) { /* no console */ }
  }
  // Sound that is copied but the browser decodes none of it: one step down, where the sound is converted (the picture stays as it is). Only for this page view, never
  // saved: the saved speed is what the viewer chose, and a failure that is hidden on every later visit cannot be looked into. The next visit tries the cheaper method again.
  function fixSound(why, proven) {
    if (mode !== 'remux' || !audioCopyOn()) return false;
    if (pinned) { logStep('copied sound cannot be played (the method was chosen by the viewer, so it is left as it is)'); if (proven) techToast('No sound could be played with the stream method you chose. Pick a slower method if you want sound.'); return false; }
    logStep('copied sound cannot be played: converting only the sound');
    speedMode = pictureCopyOn() ? 'fast' : 'slowest';
    techToast(why);
    renderSpeed();
    load('remux', resumePos(), true);
    return true;
  }
  // A copied picture the browser cannot show: the same stream with only the picture re-encoded (the sound keeps its method). Not remembered.
  function fixPicture(why) {
    if (mode !== 'remux' || !pictureCopyOn()) return false;
    if (pinned) { logStep('copied picture cannot be shown (the method was chosen by the viewer, so it is left as it is)'); return false; }
    logStep('copied picture cannot be shown: re-encoding only the picture');
    speedMode = audioCopyOn() ? 'slow' : 'slowest'; autoPicture = true;   // in memory only; Fastest and Fast stay selectable
    techToast(why);
    renderSpeed();
    load('remux', resumePos(), true);
    return true;
  }
  // Can this browser show HEVC? Then the server may copy an HEVC picture instead of re-encoding it (much cheaper). If it turns out not to show, the usual fallback re-encodes.
  var hevcOk = (function () {
    try { var v = document.createElement('video'); return !!v.canPlayType && v.canPlayType('video/mp4; codecs="hvc1.1.6.L93.B0"') !== ''; } catch (_) { return false; }
  })();
  function isHevc(c) { c = String(c || '').toLowerCase(); return c === 'hevc' || c === 'h265' || c === 'hvc1' || c === 'hev1'; }
  function copyPossible() {   // what the server can copy: H.264, and HEVC when asked to (hevc=1); the server's own limit, and the admin's "always re-encode"
    var c = String(cfg.videoCodec || '').toLowerCase();
    return (c === 'h264' || c === 'avc1' || isHevc(c)) && !cfg.alwaysEncode;
  }
  function copyAdvised() { return copyPossible() && (!isHevc(cfg.videoCodec) || hevcOk); }

  function load(m, startAt, autoplay, viaRecover) {
    if (m === 'direct' && !directUsable()) m = 'proxy';
    mode = m; triedModes[m] = true;
    clearTimeout(retryTimer); retryTimer = null; offlineWaiting = false;
    if (!viaRecover) { attempts = 0; unopenedOk = 0; } else reloadN++;                  // a new attempt of the viewer's own (seek, method change, Retry) starts with fresh patience
    lastData = now(); starvedAt = 0; slowShown = false; gotMeta = false; lastPos = startAt || 0;
    updateCompatButton();
    var seq = ++loadSeq;
    if (startAbort) { try { startAbort.abort(); } catch (_) { } startAbort = null; }   // a lookup for an earlier jump is no longer wanted (the server stops it too)
    if (m === 'remux' && startAt > 0) { pendingPos = startAt; awaitingNew = false; updateTime(); }   // the bar goes to the clicked point now
    else settleJump();
    if (m === 'remux' && startAt > 0 && pictureCopyOn()) {
      hideMessage();
      player.classList.add('is-loading');
      // The same choices the stream will use, so the server can measure the sound's timing during this lookup instead of after it.
      var ask = '/start?t=' + startAt.toFixed(2) + (isHevc(cfg.videoCodec) ? '&hevc=1' : '')
        + (audioIndex !== null ? '&audio=' + audioIndex : '') + (audioCopyOn() ? '&acopy=1' : '');
      var ctl = typeof AbortController === 'function' ? new AbortController() : null; startAbort = ctl;
      lookupAt = now();
      fetch(cfg.remuxUrl + ask, { credentials: 'same-origin', signal: ctl ? ctl.signal : undefined })
        .then(function (r) { return r.ok ? r.json() : (r.status >= 500 ? { transient: true } : null); })
        .catch(function () { return { transient: true }; })   // no answer (connection trouble) is not an answer about the file
        .then(function (d) {
          if (seq !== loadSeq) return;                       // a newer load() took over while the lookup ran
          lookupAt = 0;
          if (d && d.transient) { recover('lookup'); return; }   // the question was not answered: ask again, do not guess "re-encode"
          var ok = d && typeof d.start === 'number' && d.start >= 0 && d.start <= startAt && !d.encode, k = ok ? d.start : startAt;
          oneShotEncode = !ok;                                       // lookup failed or the copy would start far from the requested point: re-encode this stream (exact)
          copyStart = ok ? k : null;                                 // where the copied picture really starts; the same t as asked is sent, so the server lands on it again
          begin(m, k, ok ? startAt : k, autoplay);
        });
      return;
    }
    lookupAt = 0;
    oneShotEncode = false; copyStart = null;
    begin(m, startAt, startAt, autoplay);
  }

  function begin(m, startAt, requestAt, autoplay) {
    offset = m === 'remux' ? (startAt || 0) : 0;
    if (pendingPos !== null) awaitingNew = true; else awaitingNew = false;   // the first picture of this stream ends the wait (see 'loadeddata')
    var src = urlFor(m, requestAt || 0);
    if (!src) { fail('This movie\'s source address is not approved or missing.'); return; }
    hideMessage();
    player.classList.add('is-loading');
    video.src = src;
    video.load(); // note: load() resets playbackRate to defaultPlaybackRate, which setRate() keeps in sync
    if (m !== 'remux' && startAt > 0) {
      video.addEventListener('loadedmetadata', function once() { video.removeEventListener('loadedmetadata', once); video.currentTime = startAt; });
    }
    if (autoplay) startPlay();
    statusBase = m === 'direct' ? 'Streaming directly from the source server.'
      : m === 'proxy' ? 'Streaming through the application (the browser could not reach the source directly).'
      : remuxDescription();
    setStatus(statusBase);
    renderSpeed();
  }

  // ------------------------------------------------------------ delivery: slow, interrupted or refused connections
  // A dead connection gives no error in the browser (it just stops), and a busy or restarting server answers like an unusable file. None of these says anything about
  // the file's format, so they are answered by asking for the same stream again, at the same position, with the same stream method.
  var attempts = 0;             // reconnects in a row that did not get a second of the movie played
  var MAX_ATTEMPTS = 6;         // then the connection is reported as lost; the viewer's own Retry starts again
  var retryTimer = null, offlineWaiting = false, lastFail = '', unopenedOk = 0;   // unopenedOk: streams the browser could not open although the source delivered a real MP4 each time
  var lastData = 0, starvedAt = 0, longestSilence = 0, gotMeta = false, slowShown = false, lookupAt = 0, statusBase = '', lastEnd = -1;
  function now() { return window.performance && performance.now ? performance.now() : Date.now(); }
  // Data arrived (or time moved on): the connection is alive, however slow.
  function noteData() {
    var n = now();
    if (starvedAt) { longestSilence = Math.max(longestSilence, n - Math.max(lastData, starvedAt)); starvedAt = 0; }   // a quiet spell that ended by itself: be more patient with this connection
    lastData = n;
    if (slowShown) { slowShown = false; setStatus(statusBase); }
  }
  // How long a connection may deliver nothing before it is replaced. It only decides how soon a dead connection is replaced (the stream method never changes
  // because of it), so it adapts: three times the longest quiet spell this connection has had and survived, but never less than the floor. The floor is longer
  // before the first data (the server may be waiting for a free converter slot or looking up the start point) than while the movie plays.
  function patience() {
    // before the first data of a converted stream the server may be looking up the start point (up to 30 s), waiting for a free converter slot (8 s) and starting ffmpeg
    var floor = gotMeta ? 10000 : (mode === 'remux' ? 45000 : 20000);
    if (!gotMeta) floor *= 1 + 0.5 * Math.min(attempts, 2);   // a reconnect restarts that wait, so each one is given longer
    return Math.min(gotMeta ? 60000 : 90000, Math.max(floor, 3 * longestSilence));
  }
  function starved() {
    return !lazyRemux && !!video.getAttribute('src') && !video.ended && !video.error && video.readyState < 3 && (!video.paused || video.readyState === 0);
  }
  setInterval(function () {
    if (retryTimer || offlineWaiting) return;
    var n = now();
    // the start-point lookup has its own answer within 30 s on the server; silence beyond that is the connection
    if (lookupAt && n - lookupAt > Math.max(40000, patience())) { try { if (startAbort) startAbort.abort(); } catch (_) { } return; }
    try { var b = video.buffered, e = b.length ? b.end(b.length - 1) : -1; if (e !== lastEnd) { lastEnd = e; noteData(); } } catch (_) { /* no buffer info */ }
    if (!starved()) { starvedAt = 0; return; }
    if (!starvedAt) starvedAt = n;
    var idle = n - Math.max(lastData, starvedAt);
    if (idle > 4000 && !slowShown) { slowShown = true; setStatus('Waiting for data: the connection is slow or was interrupted.'); }
    if (idle > patience()) recover('stalled');
  }, 1000);

  // Asks for the same stream again, after a pause that grows with each failed try. A try that got a second of the movie played starts the count over.
  function recover(why, waitMs) {
    if (lazyRemux || retryTimer || offlineWaiting) return;
    var at = resumePos();
    if (healthySeq === loadSeq) attempts = 0;
    // a source that cannot be reached directly is not a reason to wait: the application's own relay may reach it
    if (mode === 'direct' && !triedModes.proxy && directUsable()) { load('proxy', at, true); return; }
    player.classList.add('is-loading');
    if (navigator.onLine === false) { offlineWaiting = true; setStatus('Waiting for your connection to come back…'); return; }   // does not use up a try
    if (attempts >= MAX_ATTEMPTS) { failNetwork(); return; }
    attempts++;
    var delay = waitMs > 0 ? waitMs : Math.min(1000 * Math.pow(2, attempts - 1), 15000);
    setStatus('Connection interrupted. Reconnecting…');
    retryTimer = setTimeout(function () { retryTimer = null; load(mode, resumePos(), true, true); }, delay);
  }
  window.addEventListener('online', function () {
    if (lazyRemux) return;
    if (offlineWaiting) { offlineWaiting = false; load(mode, resumePos(), true, true); }
    else if (retryTimer) { clearTimeout(retryTimer); retryTimer = null; load(mode, resumePos(), true, true); }
  });

  // Is the source reachable and delivering, or is the browser really refusing what it gets? Asked only after the browser could not open a stream at all
  // (it reports a refused or busy server and an unplayable file the same way).
  function probeSource(url) {
    if (typeof fetch !== 'function' || typeof AbortController !== 'function') return Promise.resolve({ state: 'ok' });
    var ctl = new AbortController();
    var timer = setTimeout(function () { try { ctl.abort(); } catch (_) { } }, patience());
    function done(r) { clearTimeout(timer); try { ctl.abort(); } catch (_) { } return r; }
    return fetch(url, { credentials: 'same-origin', signal: ctl.signal, headers: mode === 'proxy' ? { Range: 'bytes=0-0' } : undefined })
      .then(function (r) {
        if (!r.ok) {
          var ra = parseInt(r.headers.get('Retry-After'), 10);
          return done({ state: (r.status >= 500 || r.status === 429 || r.status === 408) ? 'busy' : 'refused', wait: ra > 0 ? ra * 1000 : 0 });
        }
        if (!r.body || !r.body.getReader) return done({ state: 'ok' });
        return r.body.getReader().read().then(function (x) {
          var v = x.value;
          if (!v || !v.length) return done({ state: 'empty' });
          return done({ state: 'ok', mp4: v.length >= 8 && v[4] === 0x66 && v[5] === 0x74 && v[6] === 0x79 && v[7] === 0x70 });   // a real MP4 starts with an 'ftyp' box
        });
      })
      .catch(function () { clearTimeout(timer); return { state: 'down' }; });
  }

  function chainNext(at) {
    var next = null;
    if (mode === 'direct' && !triedModes.proxy && directUsable()) next = 'proxy';
    else if (mode !== 'remux' && cfg.remuxEnabled && !triedModes.remux) next = 'remux';
    if (!next) return false;
    load(next, at, true);
    return true;
  }
  // The browser cannot use what the server delivers, and nothing says which part: step down one part at a time, sound first (the cheapest), and only
  // for this page view.
  function ladder(at) {
    if (mode === 'remux') {
      if (fixSound('The sound could not be played as it is. Converting it…')) return;
      // A remux the viewer asked for (audio switch / English start) failed: go back to plain playback instead of showing an error.
      if (audioIndex !== null && prevMode) {
        var back = prevMode; prevMode = null; audioIndex = null;
        techToast('Could not switch the audio track for this file. Playing the original audio.');
        renderAudioMenu();
        load(back, at, true);
        return;
      }
      if (fixPicture('Converting the picture for this device…')) return;
    } else if (chainNext(at)) return;
    failFormat();
  }

  function onError() {
    if (lazyRemux || retryTimer) return;
    var e = video.error, code = e ? e.code : 0;
    if (code === 1) return;                               // MEDIA_ERR_ABORTED: this player replaced the source
    var at = resumePos();
    var a = decodedAudio(), f = decodedFrames(), meta = metaSeq === loadSeq;
    var repeat = attempts > 0 && healthySeq !== loadSeq;   // the same kind of failure again after a reconnect that played nothing
    var loadFailure = /^MEDIA_ELEMENT_ERROR/i.test((e && e.message) || '');   // the request itself failed: no data reached the decoder
    var played = playedSeconds();

    if (code === 2) return recover('network');
    // everything decoded fine, then the data ended or was damaged: delivery, whatever the error is called
    if (a > 0 && f > 0) return recover('damaged');
    // the sound decodes nothing while the picture does: the sound's codec
    if (meta && mode === 'remux' && audioCopyOn() && f > 0 && a === 0 && played >= PLAYED_ENOUGH)
      return void fixSound('The sound could not be played as it is. Converting it…', true);
    if (meta && a > 0 && f === 0 && played >= PLAYED_ENOUGH && expectsPicture()) return pictureFailed();
    // a decode error without proof: it may be a broken connection. One reconnect settles it; the same error again after that is the file.
    if (code === 3 && !repeat) return recover('decode?');

    if (!meta && mode !== 'direct') {
      // The browser could not open the stream at all. Ask the source itself why: a busy or unreachable server is not a bad format.
      var seq = loadSeq, src = video.getAttribute('src');
      probeSource(src).then(function (r) {
        if (seq !== loadSeq || retryTimer || lazyRemux) return;
        if (r.state === 'busy' || r.state === 'down') recover('unreachable', r.wait);
        else if (r.state === 'refused') ladder(resumePos());
        // The server made nothing (ffmpeg could not start, perhaps because the source was unreachable for a moment): about fifteen seconds of tries (1+2+4+8) before blaming the combination, and the next step (picture) needs another fifteen.
        else if (r.state === 'empty') { if (attempts >= 4 && healthySeq !== loadSeq) ladder(resumePos()); else recover('empty'); }
        // Reachable now, but the failure may have been the moment before (a server that came back between the browser's try and this one). Chrome words a failed
        // request (HTTP error, reset) differently from data it received and could not use ("MEDIA_ELEMENT_ERROR: Format error" against DEMUXER / PIPELINE / DECODER
        // errors): a converted stream that failed like that is delivery, not format. And the browser rejecting a real MP4 has to happen three tries in a row
        // before the file is blamed, because one lucky answer to our own check between two failed tries proves nothing.
        else if (mode === 'remux') { unopenedOk = r.mp4 ? unopenedOk + 1 : 0; if (loadFailure || !r.mp4 || unopenedOk < 3) recover('unopened?'); else ladder(resumePos()); }
        else if (!repeat) recover('unopened?');
        else ladder(resumePos());
      });
      return;
    }
    ladder(at);
  }

  function failBase(text, kind) {
    clearTimeout(retryTimer); retryTimer = null; offlineWaiting = false;
    lastFail = kind || '';
    settleJump();
    player.classList.remove('is-loading');
    el.msgTitle.textContent = 'Cannot play this video';
    el.msgText.textContent = text;
    el.msg.classList.add('is-on');
  }
  function fail(text) { failBase(text, 'format'); }
  function failNetwork() {
    failBase('The stream was interrupted and could not be restored. Check your connection, then press Retry. Your place is kept.', 'network');
    el.msgTitle.textContent = 'Connection lost';
  }
  function failFormat() {
    var e = video.error;
    var why = (e && e.code === 3) ? 'The video could not be decoded (damaged data or an unsupported codec).'
            : (e && e.code === 2) ? 'A network error interrupted the stream. The source server may be offline.'
            : 'This browser cannot play this file, or the source server is unreachable.';
    var hint = cfg.remuxEnabled ? '' :
      ' Containers such as MKV/AVI and codecs such as HEVC or AC3 often are not supported by browsers. Install FFmpeg on the server (or set Media:FfmpegPath) to enable compatibility mode.';
    // the codec / ffmpeg detail is for whoever runs the server; visitors get the plain sentence when it is hidden
    fail(ui.hideTech ? why : why + hint + (cfg.videoCodec ? ' (video: ' + cfg.videoCodec + (cfg.audioCodec ? ', audio: ' + cfg.audioCodec : '') + ', container: ' + String(cfg.extension || '').replace('.', '') + ')' : ''));
  }
  function hideMessage() { el.msg.classList.remove('is-on'); }

  video.addEventListener('error', function () { onError(); });

  // Some phones accept a file (HEVC, 10-bit) and play the sound but cannot decode the picture, without any error. That is seen in the evidence: the sound
  // decodes, the movie has played, and no picture frame was ever decoded (see checkDecode), or the file's header says there is no usable picture.
  function expectsPicture() { return !cfg.inspected || !!cfg.videoCodec; }
  function pictureFailed() {
    if (lazyRemux || retryTimer || !expectsPicture()) return;
    var at = resumePos();
    if (mode !== 'remux' && cfg.remuxEnabled && !triedModes.remux) {
      techToast('This device cannot show the picture of this file directly. Switching to compatibility mode…');
      load('remux', at, true);
    } else if (fixPicture('Converting the picture for this device…')) {
      /* re-encoding the picture only; the sound keeps its method */
    } else {
      fail(pinned && mode === 'remux' && pictureCopyOn() ? 'This device could not show the picture with the stream method you chose. Pick a slower method in the settings. The sound may still play.'
         : ui.hideTech ? 'This device cannot show the picture of this video. The sound may still play.'
                       : 'This device cannot decode the picture of this video (video: ' + (cfg.videoCodec || 'unknown') + '). The sound may still play.');
    }
  }
  // Runs while the movie plays. Decides nothing until PLAYED_ENOUGH seconds have really played (so the data has arrived and has been given to the decoders).
  function checkDecode() {
    if (lazyRemux || retryTimer || judgedSeq === loadSeq || video.paused || playingSeq !== loadSeq) return;
    var played = playedSeconds();
    if (played >= 1 && healthySeq !== loadSeq) { healthySeq = loadSeq; unopenedOk = 0; }      // a second of the movie played: this connection works
    if (played < PLAYED_ENOUGH) return;
    var a = decodedAudio(), f = decodedFrames();
    if (a === null || f === null) { judgedSeq = loadSeq; return; }        // this browser has no counters (Firefox): it reports real failures as errors
    if (a > 0 && f > 0) { judgedSeq = loadSeq; return; }                  // both decode: nothing to fix
    if (f > 0 && a === 0) {
      judgedSeq = loadSeq;
      fixSound('The sound did not start. Switching to a more compatible stream method…', true);   // only when the sound is being copied (fixSound checks)
    } else if (a > 0 && f === 0 && expectsPicture()) {
      judgedSeq = loadSeq; pictureFailed();
    }
  }
  video.addEventListener('timeupdate', function () { if (!video.error && video.readyState > 0 && pendingPos === null) lastPos = offset + video.currentTime; noteData(); checkDecode(); });
  video.addEventListener('progress', noteData);
  video.addEventListener('loadeddata', noteData);
  video.addEventListener('canplay', noteData);
  video.addEventListener('playing', function () {
    noteData();
    if (playingSeq !== loadSeq) { playingSeq = loadSeq; playedFrom = video.currentTime; }
  });
  video.addEventListener('loadedmetadata', function () {
    metaSeq = loadSeq; gotMeta = true; noteData();
    player.classList.remove('is-loading');
    if (!knownDuration && isFinite(video.duration)) knownDuration = video.duration;
    detectNativeAudio();
    preferEnglishNatively();
    renderAudioMenu();
    updateTime();
    // The header is read and the picture track is unusable here: a fact about this browser and file, not about speed.
    if (expectsPicture() && !video.videoWidth) pictureFailed();
  });
  video.addEventListener('waiting', function () { player.classList.add('is-loading'); });
  ['playing', 'canplay', 'seeked'].forEach(function (ev) { video.addEventListener(ev, function () { player.classList.remove('is-loading'); }); });
  el.retry.addEventListener('click', function () {
    prevMode = null;
    if (lastFail === 'network') { load(mode, resumePos(), true); return; }   // nothing is wrong with the file: the same method again
    triedModes = {}; load('direct', resumePos(), true);
  });

  // ------------------------------------------------------------ basic controls
  function togglePlay() {
    if (lazyRemux) { lazyRemux = false; load('remux', 0, true); return; }
    if (video.paused) startPlay(); else video.pause();
  }
  el.play.addEventListener('click', togglePlay);
  el.bigPlay.addEventListener('click', function (e) { e.stopPropagation(); togglePlay(); wake(); });
  function showPlaying(on) {
    var icon = on ? ICON.pause : ICON.play, label = on ? 'Pause' : 'Play';
    [el.play, el.bigPlay].forEach(function (b) { b.innerHTML = icon; b.setAttribute('aria-label', label); });
    player.classList.toggle('is-paused', !on);
    if (on) player.classList.add('has-played');
  }
  video.addEventListener('play', function () { showPlaying(true); });
  video.addEventListener('pause', function () { showPlaying(false); player.classList.remove('is-idle'); });

  function updateTime() {
    var d = duration(), p = position();
    el.cur.textContent = fmt(p);
    el.dur.textContent = fmt(d);
    if (!seeking && d > 0) el.seek.value = Math.round((p / d) * 1000);
    paintSeek(d > 0 ? clamp(p / d, 0, 1) : 0);
    el.seek.setAttribute('aria-valuetext', fmt(p) + ' of ' + fmt(d));
    try {
      var b = video.buffered;
      if (b.length && d > 0) {
        var end = offset + b.end(b.length - 1);
        el.seekWrap.style.setProperty('--b', clamp(end / d, 0, 1));
      }
    } catch (_) { /* buffered not available yet */ }
  }
  video.addEventListener('loadeddata', function () { if (awaitingNew) { settleJump(); updateTime(); } });
  video.addEventListener('timeupdate', updateTime);
  video.addEventListener('progress', updateTime);
  video.addEventListener('durationchange', updateTime);

  var seeking = false;
  // The played part of the bar and the thumb use the same fraction, so they always agree.
  function paintSeek(f) { el.seekWrap.style.setProperty('--p', f); }
  // A small time label follows the pointer over the bar (and the thumb while dragging).
  function showTip(f) {
    el.seekTip.textContent = fmt(f * duration());
    el.seekTip.style.left = 'calc(7px + (100% - 14px) * ' + f + ')';
    el.seekWrap.classList.add('is-tip');
  }
  var tipTimer = null;
  el.seekWrap.addEventListener('pointermove', function (e) {
    if (e.pointerType === 'touch') return;
    var r = el.seekWrap.getBoundingClientRect(); if (r.width <= 14) return;
    showTip(clamp((e.clientX - r.left - 7) / (r.width - 14), 0, 1));
  });
  el.seekWrap.addEventListener('pointerleave', function () { if (!seeking) el.seekWrap.classList.remove('is-tip'); });
  el.seek.addEventListener('input', function () {
    seeking = true;
    var f = el.seek.value / 1000;
    el.cur.textContent = fmt(f * duration()); paintSeek(f); showTip(f);
  });
  el.seek.addEventListener('change', function () {
    seeking = false;
    clearTimeout(tipTimer); tipTimer = setTimeout(function () { el.seekWrap.classList.remove('is-tip'); }, 600);
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
    el.mute.innerHTML = video.muted || video.volume === 0 ? ICON.mute : video.volume < 0.5 ? ICON.volLow : ICON.vol;
    el.mute.setAttribute('aria-label', video.muted || video.volume === 0 ? 'Unmute' : 'Mute');
    el.volume.value = video.muted ? 0 : video.volume;
  });

  function toggleFs() {
    if (document.fullscreenElement) document.exitFullscreen();
    else if (player.requestFullscreen) {
      var r = player.requestFullscreen();
      if (r && r.catch) r.catch(function () { });
    }
    else if (video.webkitEnterFullscreen) video.webkitEnterFullscreen(); // iOS Safari (its own full screen turns sideways by itself)
  }
  // On a phone or tablet, full screen turns the screen to landscape where the browser allows it (Android Chrome and similar; iOS Safari does not allow it).
  function landscape(on) {
    try {
      var so = screen.orientation;
      if (!so) return;
      if (on) { if (window.matchMedia('(pointer: coarse)').matches && so.lock) so.lock('landscape').catch(function () { }); }
      else if (so.unlock) so.unlock();
    } catch (_) { /* not supported */ }
  }
  el.fs.addEventListener('click', toggleFs);
  document.addEventListener('fullscreenchange', function () {
    var on = !!document.fullscreenElement;
    player.classList.toggle('is-full', on); landscape(on);
    el.fs.innerHTML = on ? ICON.fsExit : ICON.fs; el.fs.setAttribute('aria-label', on ? 'Exit fullscreen' : 'Fullscreen');
  });

  // ------------------------------------------------------------ gestures on the picture (touch and mouse)
  //   tap / click ............ play or pause
  //   double tap / click ..... left side: back 10 s · right side: forward 10 s (keep tapping to keep skipping) · middle: fullscreen
  //   press and hold ......... 2× speed while held, back to the normal speed on release
  var G = { HOLD_MS: 380, DOUBLE_MS: 280, CHAIN_MS: 600, MOVE: 14, SKIP: 10, HOLD_RATE: 2 };
  var gDown = null, gHoldTimer = null, gHolding = false, gTapTimer = null, gLastTap = null, gChain = null;
  var gSkipTimer = null, gSkipPending = 0, gSkipBase = 0, gPointerType = 'mouse';
  var flashEls = { back: $('flash-back'), fwd: $('flash-fwd') }, holdBadge = $('hold-badge');

  function zoneOf(e) {
    var r = video.getBoundingClientRect(), x = (e.clientX - r.left) / Math.max(1, r.width);
    return x < 0.35 ? 'back' : x > 0.65 ? 'fwd' : 'mid';
  }
  function flash(zone, text) {
    var f = flashEls[zone]; if (!f) return;
    f.querySelector('b').textContent = text;
    f.classList.remove('is-on'); void f.offsetWidth; f.classList.add('is-on');   // restart the animation
    clearTimeout(f._t); f._t = setTimeout(function () { f.classList.remove('is-on'); }, 750);
  }
  // Several quick skips are added up and applied once: a remux stream has to restart for every seek, so one jump is much cheaper than five.
  function skip(zone) {
    if (gSkipPending === 0) gSkipBase = position();
    gSkipPending += zone === 'fwd' ? G.SKIP : -G.SKIP;
    flash(zone, Math.abs(gSkipPending) + ' s');
    clearTimeout(gSkipTimer);
    gSkipTimer = setTimeout(function () { var t = gSkipBase + gSkipPending; gSkipPending = 0; seekTo(t); }, 350);
  }
  // One touch on the picture shows the controls, or hides them while the video plays (like on YouTube); a mouse click plays or pauses.
  function singleTap(wasIdle) {
    if (gPointerType !== 'touch') { togglePlay(); return; }
    if (!el.settings.hidden) { closeMenus(); return; }
    if (wasIdle || video.paused) { wake(); return; }
    clearTimeout(idleTimer); player.classList.add('is-idle');
  }
  function tap(zone, wasIdle) {
    var now = Date.now();
    if (zone !== 'mid' && gChain && gChain.zone === zone && now - gChain.t < G.CHAIN_MS) { gChain.t = now; skip(zone); return; }
    if (gLastTap && now - gLastTap.t < G.DOUBLE_MS && gLastTap.zone === zone) {
      clearTimeout(gTapTimer); gLastTap = null;
      if (zone === 'mid') toggleFs(); else { gChain = { zone: zone, t: now }; skip(zone); }
      return;
    }
    gLastTap = { t: now, zone: zone };
    clearTimeout(gTapTimer);
    gTapTimer = setTimeout(function () { gLastTap = null; singleTap(wasIdle); }, G.DOUBLE_MS);   // waits briefly to see whether a second tap follows
  }
  function endHold() {
    if (!gHolding) return;
    gHolding = false;
    video.playbackRate = video.defaultPlaybackRate || 1;      // setRate() keeps defaultPlaybackRate equal to the speed the viewer chose
    holdBadge.classList.remove('is-on');
  }
  video.addEventListener('pointerdown', function (e) {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    gPointerType = e.pointerType || 'mouse';
    gDown = { x: e.clientX, y: e.clientY, id: e.pointerId, zone: zoneOf(e), moved: false, wasIdle: player.classList.contains('is-idle') };
    try { video.setPointerCapture(e.pointerId); } catch (_) { }
    clearTimeout(gHoldTimer);
    gHoldTimer = setTimeout(function () {
      if (!gDown || gDown.moved || video.paused || video.ended) return;
      gHolding = true;
      video.playbackRate = G.HOLD_RATE;
      holdBadge.classList.add('is-on');
    }, G.HOLD_MS);
  });
  video.addEventListener('pointermove', function (e) {
    if (gDown && !gDown.moved && Math.hypot(e.clientX - gDown.x, e.clientY - gDown.y) > G.MOVE) { gDown.moved = true; clearTimeout(gHoldTimer); }
  });
  function release(cancelled) {
    if (!gDown) return;
    var d = gDown; gDown = null; clearTimeout(gHoldTimer);
    try { video.releasePointerCapture(d.id); } catch (_) { }
    if (gHolding) { endHold(); return; }                       // a hold is never also a tap
    if (cancelled || d.moved) return;
    tap(d.zone, d.wasIdle);
  }
  video.addEventListener('pointerup', function () { release(false); });
  video.addEventListener('pointercancel', function () { release(true); });
  video.addEventListener('contextmenu', function (e) { if (gPointerType === 'touch' || gHolding) e.preventDefault(); });   // a long press on a phone must not open the browser menu
  window.addEventListener('blur', function () { gDown = null; clearTimeout(gHoldTimer); endHold(); });

  // ------------------------------------------------------------ menus
  // Everything but play, volume, time and fullscreen lives in one settings panel (gear): a home list and one view per setting, with a back button.
  var views = ['set-home', 'menu-sub', 'menu-audio', 'menu-speed', 'view-method'];
  var settingsBtns = [].slice.call(document.querySelectorAll('.js-settings'));
  function showView(id) {
    views.forEach(function (v) { var n = $(v); if (n) n.classList.toggle('is-on', v === id); });
    var home = id === 'set-home';
    el.setBack.hidden = home;
    el.setTitle.textContent = home ? 'Settings' : ($(id).getAttribute('data-title') || 'Settings');
    el.settings.querySelector('.set-views').scrollTop = 0;
  }
  function settingsOpen() { return !el.settings.hidden; }
  function openSettings(view) {
    showView(view || 'set-home');
    el.settings.hidden = false; el.scrim.hidden = false;
    settingsBtns.forEach(function (b) { b.setAttribute('aria-expanded', 'true'); });
    player.classList.add('has-panel');
    clearTimeout(idleTimer); player.classList.remove('is-idle');
  }
  function closeMenus() {
    if (el.settings.hidden) return;
    el.settings.hidden = true; el.scrim.hidden = true;
    settingsBtns.forEach(function (b) { b.setAttribute('aria-expanded', 'false'); });
    player.classList.remove('has-panel');
    wake();
  }
  settingsBtns.forEach(function (b) {
    b.addEventListener('click', function (e) { e.stopPropagation(); if (settingsOpen()) closeMenus(); else openSettings('set-home'); });
  });
  // CC (desktop bar) opens the settings straight at the subtitles
  el.btnSub.addEventListener('click', function (e) {
    e.stopPropagation();
    if (settingsOpen() && $('menu-sub').classList.contains('is-on')) closeMenus(); else openSettings('menu-sub');
  });
  [].forEach.call(el.settings.querySelectorAll('.set-row[data-view]'), function (row) {
    row.addEventListener('click', function () { showView(row.getAttribute('data-view')); });
  });
  el.setBack.addEventListener('click', function () { showView('set-home'); });
  el.setClose.addEventListener('click', closeMenus);
  el.scrim.addEventListener('click', closeMenus);
  document.addEventListener('click', function (e) {
    if (!settingsOpen()) return;
    if (e.target.closest('.settings') || e.target.closest('.js-settings') || e.target.closest('.tuner') || e.target === el.btnSub || el.btnSub.contains(e.target)) return;
    closeMenus();
  });
  // The value shown on each row of the home list
  function setVal(node, text) { if (node) node.textContent = text; }
  function shortTrack(a, i) { return (a && (a.lang || a.title)) || ('Track ' + (i + 1)); }
  function refreshRowValues() {
    setVal(el.valSub, activeTrack ? activeTrack.label : 'Off');
    var cur = currentAudioIndex(), txt = '';
    if (nativeAudio) {
      for (var k = 0; k < video.audioTracks.length; k++) if (video.audioTracks[k].enabled) txt = video.audioTracks[k].label || video.audioTracks[k].language || ('Track ' + (k + 1));
    } else {
      for (var i = 0; i < audioTracks.length; i++) if (audioTracks[i].index === cur) txt = shortTrack(audioTracks[i], i);
      if (!txt && audioTracks.length === 1) txt = shortTrack(audioTracks[0], 0);
    }
    setVal(el.valAudio, txt);
  }

  function item(menu, label, on, handler, keepOpen) {
    var b = document.createElement('button');
    b.type = 'button'; b.textContent = label; b.title = label;
    if (on) b.classList.add('is-on');
    b.addEventListener('click', function () { handler(); if (!keepOpen) closeMenus(); });
    menu.appendChild(b);
    return b;
  }
  function heading(menu, text) { if (menu.classList.contains('set-view')) return; var h = document.createElement('h4'); h.textContent = text; menu.appendChild(h); }
  // kind: 'tech' (talks about the server, ffmpeg, the dashboard), 'hint' (explains how something works) or 'both'. A hidden note is replaced by `plain` when there is one.
  function note(menu, text, kind, plain) {
    var hidden = (kind === 'tech' && ui.hideTech) || (kind === 'hint' && ui.hideHints) || (kind === 'both' && (ui.hideTech || ui.hideHints));
    if (hidden) { if (!plain) return; text = plain; }
    var d = document.createElement('div'); d.className = 'note'; d.textContent = text; menu.appendChild(d);
  }

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
    setVal(el.valSpeed, fmtRate(r));
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
  var subSize = Number(store.get('sa.subsize')) || 1;

  (cfg.subtitles || []).forEach(function (s) {
    if (!s.supported) return;
    subTracks.push({ key: 's' + s.id, label: (s.lang || 'Subtitle') + ' — ' + s.file, kind: 'server', id: s.id, cues: null, offset: 0, scale: 1, state: 'idle' });
  });

  function timingKey(t) { return 'sa.subtiming.' + cfg.fileId + '.' + t.key; }
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
    if (!auto) { userTouched = true; store.set('sa.subs.on', key ? '1' : '0'); }   // remembered: subtitles you turned on stay on for the next movie
    if (!key) { activeTrack = null; renderSubs(true); renderSubMenu(); refreshTuner(); refreshSubButton(); return; }
    var t = trackByKey(key);
    if (!t) return;
    if (t.state === 'preparing') { toast('Preparing this subtitle from the video file (first time only). It turns on by itself when ready.'); pendingKey = key; return; }
    activeTrack = t; lastActiveKey = key;
    renderSubMenu(); refreshTuner(); refreshSubButton();
    if (t.cues) { renderSubs(true); kickSubs(); return; }
    if (t.kind !== 'server' && t.kind !== 'embedded') return;
    t.state = 'loading'; renderSubMenu();
    var source = t.vtt != null ? Promise.resolve(t.vtt)      // embedded track already kept in this browser
      : fetch(t.kind === 'server' ? '/media/subtitle/' + t.id : t.url).then(function (r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.arrayBuffer();
      }).then(decodeBytes);
    source.then(function (text) {
      t.cues = parseSubtitle(text);
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
  function ensureEmbTrack(e, n, state) {
    var key = 'e' + e.index, t = trackByKey(key);
    if (!t) {
      t = { key: key, label: embLabel(e, n), kind: 'embedded', url: '/media/embsub/' + cfg.fileId + '/' + e.index, lang: e.lang, title: e.title, cues: null, vtt: e.vtt != null ? e.vtt : null, offset: 0, scale: 1, state: state };
      loadTiming(t); subTracks.push(t);
    }
    return t;
  }
  function applyEmbedded(tracks) {
    embInfo = { preparing: false, failed: false, unavailable: false, unsupported: 0 };
    var n = 0;
    tracks.forEach(function (e) {
      n++;
      if (!e.supported) { embInfo.unsupported++; return; }
      var state = e.state === 'ready' ? 'idle' : e.state === 'preparing' ? 'preparing' : 'error';
      if (e.state === 'failed') embInfo.failed = true;
      if (e.state === 'unavailable') embInfo.unavailable = true;
      if (e.state === 'preparing') embInfo.preparing = true;
      var existed = !!trackByKey('e' + e.index);
      var t = ensureEmbTrack(e, n, state);
      if (existed) {
        if (e.vtt != null) { t.vtt = e.vtt; if (t.cues) t.cues = parseSubtitle(e.vtt); }   // the complete file replaces the stretch read so far
        if (t.cues && t.state !== 'loading') t.state = 'idle';
        else if (t.state === 'preparing' || t.state === 'error') t.state = state;
      }
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
    var saved = store.get('sa.subs.on');
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
  // The browser keeps extracted subtitles in IndexedDB, so the server only extracts a movie's embedded subtitles once per viewer and never stores them.
  var DB_NAME = 'streamapp', DB_STORE = 'embsubs', DB_CAP = 300, cacheKey = 'f' + cfg.fileId;
  function idb() {
    return new Promise(function (res) {
      try {
        var r = indexedDB.open(DB_NAME, 2);
        r.onupgradeneeded = function () {
          var st = r.result.objectStoreNames.contains(DB_STORE) ? r.transaction.objectStore(DB_STORE) : r.result.createObjectStore(DB_STORE, { keyPath: 'k' });
          if (!st.indexNames.contains('at')) st.createIndex('at', 'at');
        };
        r.onsuccess = function () { var db = r.result; db.onversionchange = function () { db.close(); }; res(db); };   // lets another tab upgrade the database
        r.onerror = r.onblocked = function () { res(null); };
      } catch (e) { res(null); }          // private window or storage blocked: everything still works, just without the local copy
    });
  }
  function idbGet(k) {
    return idb().then(function (db) {
      if (!db) return null;
      return new Promise(function (res) {
        try { var q = db.transaction(DB_STORE).objectStore(DB_STORE).get(k); q.onsuccess = function () { res(q.result || null); }; q.onerror = function () { res(null); }; }
        catch (e) { res(null); }
      });
    });
  }
  function idbPut(v) {
    return idb().then(function (db) {
      if (!db) return;
      return new Promise(function (res) {
        try { var tx = db.transaction(DB_STORE, 'readwrite'); tx.objectStore(DB_STORE).put(v); tx.oncomplete = tx.onerror = tx.onabort = function () { res(); }; }
        catch (e) { res(); }
      });
    });
  }
  // Keeps the browser's copy bounded: when more than DB_CAP movies are stored, the ones opened longest ago go first.
  function idbTrim() {
    return idb().then(function (db) {
      if (!db) return;
      return new Promise(function (res) {
        try {
          var tx = db.transaction(DB_STORE, 'readwrite'), st = tx.objectStore(DB_STORE), cq = st.count();
          cq.onsuccess = function () {
            var extra = cq.result - DB_CAP;
            if (extra <= 0) return;
            var cur = st.index('at').openCursor();
            cur.onsuccess = function () { var c = cur.result; if (c && extra-- > 0) { c.delete(); c.continue(); } };
          };
          tx.oncomplete = tx.onerror = tx.onabort = function () { res(); };
        } catch (e) { res(); }
      });
    });
  }
  function keepStorage() { try { if (navigator.storage && navigator.storage.persist) navigator.storage.persist(); } catch (e) { } }

  // Subtitles appear within seconds: the server reads just the stretch around the playhead straight out of the file (like a desktop player would),
  // while the complete extraction runs in the background. Time ranges already read are remembered, so only a jump to somewhere new asks again.
  var embCover = [], embBusy = false, embFull = false, embWindowOn = false, embNoText = false, embWinFails = 0, embTick = 0;
  var WIN_LEN = 600, WIN_BACK = 30, WIN_AHEAD_MIN = 90;
  function addCover(a, b) {
    embCover.push([a, b]); embCover.sort(function (x, y) { return x[0] - y[0]; });
    var out = [];
    embCover.forEach(function (r) { var l = out[out.length - 1]; if (l && r[0] <= l[1] + 1) l[1] = Math.max(l[1], r[1]); else out.push([r[0], r[1]]); });
    embCover = out;
  }
  function coverAhead(p) {
    for (var i = 0; i < embCover.length; i++) if (embCover[i][0] <= p && embCover[i][1] > p) return embCover[i][1] - p;
    return 0;
  }
  function addCues(t, list) {
    var seen = t.seen || (t.seen = {}), out = t.cues || [];
    list.forEach(function (c) {
      var k = c.start.toFixed(2) + '|' + c.end.toFixed(2) + '|' + c.text;
      if (!seen[k]) { seen[k] = 1; out.push(c); }
    });
    out.sort(function (a, b) { return a.start - b.start; });
    t.cues = out;
  }
  function applyWindow(d) {
    var tracks = d.tracks || [], from = +d.from || 0, n = 0, got = false;
    tracks.forEach(function (e) {
      n++;
      if (!e.supported) return;
      var t = ensureEmbTrack(e, n, 'idle');
      if (e.vtt == null || embFull || t.vtt != null) return;      // nothing read, or the complete subtitle is already here
      var cues = parseSubtitle(e.vtt).map(function (c) { return { start: c.start + from, end: c.end + from, text: c.text }; });
      addCues(t, cues);
      if (t.state === 'preparing' || t.state === 'error') t.state = 'idle';
      got = true;
    });
    if (!tracks.some(function (e) { return e.supported; })) embNoText = true;
    if (got || tracks.length) addCover(from, from + (+d.length || WIN_LEN));
    renderSubMenu();
    if (pendingKey) { var pt = trackByKey(pendingKey); if (pt && pt.state === 'idle') { var k = pendingKey; pendingKey = null; activate(k, true); } }
    autoSubtitles();
    if (activeTrack && activeTrack.kind === 'embedded') renderSubs(true);
  }
  function ensureWindow() {
    if (!embWindowOn || embFull || embBusy || embNoText || embWinFails >= 3) return;
    var p = Math.max(0, position());
    if (coverAhead(p) >= WIN_AHEAD_MIN) return;
    var from = Math.max(0, Math.floor(p - WIN_BACK));
    embBusy = true;
    fetch('/media/embsub/' + cfg.fileId + '/window?from=' + from + '&len=' + WIN_LEN, { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
      .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(function (d) { embWinFails = 0; applyWindow(d); })
      .catch(function () { embWinFails++; })
      .then(function () { embBusy = false; if (embWindowOn && !embFull && embWinFails < 3 && coverAhead(Math.max(0, position())) < WIN_AHEAD_MIN) setTimeout(ensureWindow, 500); });
  }
  video.addEventListener('timeupdate', function () { var now = Date.now(); if (now - embTick > 4000) { embTick = now; ensureWindow(); } });
  video.addEventListener('seeked', ensureWindow);

  function loadEmbedded() {
    if (embTimer) { clearTimeout(embTimer); embTimer = null; }
    idbGet(cacheKey).then(function (hit) {
      if (hit && hit.tracks && hit.fp === (cfg.fingerprint || '')) {
        embFull = true;
        var list = hit.tracks.map(function (t) { return { index: t.index, lang: t.lang, title: t.title, supported: true, state: 'ready', vtt: t.vtt }; });
        for (var i = 0; i < (hit.unsupported || 0); i++) list.push({ supported: false });
        applyEmbedded(list);
        hit.at = Date.now(); idbPut(hit);                // opened just now: moves to the back of the removal queue
        return;
      }
      embWindowOn = true;
      ensureWindow();
      pollServer();
    });
  }
  function pollServer() {
    fetch('/media/embsub/' + cfg.fileId, { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
      .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(function (d) {
        var tr = d.tracks || [];
        applyEmbedded(tr);
        var supported = tr.filter(function (t) { return t.supported; });
        if (embInfo.preparing && ++embPolls < 600) embTimer = setTimeout(pollServer, 3000);
        else if (supported.length && supported.every(function (t) { return t.state === 'ready'; })) harvest(tr, supported);
      })
      .catch(function () { /* the menu still offers file loading */ });
  }
  // Takes the finished subtitles from the server once and keeps them in this browser for every later visit.
  function harvest(tr, supported) {
    Promise.all(supported.map(function (t) {
      return fetch('/media/embsub/' + cfg.fileId + '/' + t.index, { credentials: 'same-origin' })
        .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.text(); })
        .then(function (vtt) { return { index: t.index, lang: t.lang, title: t.title, vtt: vtt }; });
    })).then(function (list) {
      embFull = true;
      list.forEach(function (x) {
        var t = trackByKey('e' + x.index);
        if (!t) return;
        t.vtt = x.vtt;
        if (t.cues) { t.cues = parseSubtitle(x.vtt); t.seen = null; if (activeTrack === t) renderSubs(true); }   // complete file replaces the stretch read so far
      });
      return idbPut({ k: cacheKey, fp: cfg.fingerprint || '', tracks: list, unsupported: tr.filter(function (t) { return !t.supported; }).length, at: Date.now() })
        .then(idbTrim).then(keepStorage);
    }).catch(function () { });
  }
  if (cfg.embeddedSubtitles || !cfg.inspected) loadEmbedded();
  autoSubtitles();

  // ---- subtitle menu
  function refreshSubButton() {
    el.btnSub.classList.toggle('is-on', !!activeTrack);
    el.btnSub.title = activeTrack ? 'Subtitles: ' + activeTrack.label + ' (C to turn off)' : 'Subtitles (C)';
    refreshRowValues();
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
    if (emb.preparing && !subTracks.some(function (t) { return t.cues; })) note(m, 'Reading the subtitles inside the video… they will show up in a few seconds.');
    if (emb.failed) note(m, 'The subtitles inside this video could not be read. You can still load a subtitle file.');
    if (emb.unavailable) note(m, 'Subtitles inside the video need ffmpeg on the server (Admin › Dashboard › Download FFmpeg).', 'tech');
    if (emb.unsupported) note(m, emb.unsupported + ' image-based subtitle track(s) (Blu-ray / DVD style) cannot be turned into text, so they cannot be shown here.', 'hint');
    if (!subTracks.length && !emb.preparing && !emb.unsupported && !emb.unavailable) note(m, 'No subtitles found for this movie. Load a .srt, .vtt or .ass file, or drop one onto the video.', 'hint', 'No subtitles found for this movie.');
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
  function setSize(v) { subSize = clamp(Math.round(v * 20) / 20, 0.5, 2.5); store.set('sa.subsize', String(subSize)); applySubSize(); }
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
    el.btnAudio.disabled = false;
    el.btnAudio.classList.toggle('is-dim', !many && !nativeAudio);
    refreshRowValues();

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
        note(m, 'This file has only one audio track, so there is nothing to switch to. A different file of this movie (see the file list under the player) may have other languages.', 'hint');
      } else if (!cfg.inspected) {
        note(m, 'The audio tracks of this file are unknown because the server could not read them' +
          (cfg.inspectionError ? ': ' + cfg.inspectionError : '') +
          '. An administrator can check Dashboard → Media tools (FFprobe must be found), then reopen this page.', 'tech', 'Audio track information is not available for this file.');
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
      note(m, 'Switching restarts playback at the current position; the server converts the sound to stereo AAC.', 'both');
    } else {
      audioTracks.forEach(function (a, i) { var b = document.createElement('div'); b.className = 'note'; b.textContent = '• ' + label(a, i); m.appendChild(b); });
      note(m, 'This file has several audio tracks, but this browser cannot switch between them and the server has no ffmpeg. Install FFmpeg (it ships with FFprobe) or set Media:FfmpegPath, then restart StreamApp.', 'tech', 'This file has several audio tracks, but switching between them is not available here.');
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

  // ------------------------------------------------------------ compatibility mode switch
  // On = the server converts the stream with ffmpeg (plays almost anything, supports audio switching, costs server CPU and makes seeking a little slower).
  // Off = the file is played as it is. The button shows what is really happening (an automatic fallback also turns it on) and a manual choice is remembered on this device.
  var COMPAT_KEY = 'streamapp.compat';
  function updateCompatButton() {
    var on = mode === 'remux';
    var title = !cfg.remuxEnabled ? 'Compatibility mode is not available: the server has no ffmpeg'
      : on ? 'Compatibility mode is ON: the server converts the video. Click to play the original file instead.'
           : 'Compatibility mode is OFF: playing the original file. Click to let the server convert the video.';
    if (ui.hideTech) title = 'Compatibility mode';
    renderSpeed();
    if (el.compatSwitch) {
      el.compatSwitch.checked = on;
      el.compatSwitch.disabled = !cfg.remuxEnabled;
      el.compatSwitch.title = title;
      if (el.compatState) el.compatState.textContent = on ? 'On' : 'Off';
      if (el.compatHint) el.compatHint.textContent = !cfg.remuxEnabled ? 'Needs ffmpeg on the server.'
        : on ? 'The server converts the video (audio switching and more formats; uses server CPU).'
             : 'Playing the original file.';
    }
  }
  function setCompat(on) {
    if (!cfg.remuxEnabled || (on === (mode === 'remux'))) return;
    var at = lazyRemux ? 0 : position();
    var playing = !video.paused || lazyRemux;
    lazyRemux = false;
    store.set(COMPAT_KEY, on ? '1' : '0');
    if (on) {
      prevMode = mode;
      triedModes = { remux: true };
      techToast('Compatibility mode on: the server converts the video.');
      load('remux', at, playing);
    } else {
      var hadChoice = audioIndex !== null && audioTracks.length > 1;
      audioIndex = null; prevMode = null; autoPicture = false;
      triedModes = {};
      techToast(hadChoice ? 'Compatibility mode off: playing the original file with its default audio track.' : 'Compatibility mode off: playing the original file.');
      load('direct', at, playing);
    }
    renderAudioMenu();
  }
  if (el.compatSwitch) el.compatSwitch.addEventListener('change', function () { setCompat(el.compatSwitch.checked); });

  // ------------------------------------------------------------ idle controls + keyboard
  var idleTimer;
  function wake() {
    player.classList.remove('is-idle');
    clearTimeout(idleTimer);
    if (!video.paused && el.tuner.hidden && el.settings.hidden) idleTimer = setTimeout(function () { player.classList.add('is-idle'); }, 3000);
  }
  ['mousemove', 'touchstart', 'keydown'].forEach(function (ev) { player.addEventListener(ev, wake, { passive: true }); });
  video.addEventListener('play', wake);

  player.addEventListener('keydown', function (e) {
    if (e.target.closest && (e.target.closest('.tuner') || e.target.closest('.settings'))) {
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
        else if (embInfo.preparing) toast('Subtitles from the video are still being prepared.');
        else hintToast('No subtitles for this movie. Load a subtitle file or drop one onto the video.');
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
    clearTimeout(retryTimer); retryTimer = null; offlineWaiting = false;
    lazyRemux = true; mode = 'remux'; offset = 0; updateCompatButton();
    video.pause(); video.removeAttribute('src'); video.load();
    audioIndex = cfg.preferredAudio; prevMode = 'direct'; triedModes.remux = true;
    player.classList.remove('is-loading');
    setStatus('Ready. Starts with the English audio track (compatibility mode converts the stream when you press play).');
    updateTime();
  }
  function startEnglishNow() {
    var t = position();
    audioIndex = cfg.preferredAudio; prevMode = mode === 'proxy' ? 'proxy' : 'direct'; triedModes.remux = true; lazyRemux = false;
    load('remux', t < 0.5 ? 0 : t, !video.paused);
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
        if (cfg.remuxEnabled && (mode === 'direct' || mode === 'proxy') && !lazyRemux && position() < 8 && !gSkipPending) {
          if (video.paused && position() < 0.5) startLazyEnglish();
          else startEnglishNow();           // autoplay already began with the file's default track: continue from here with the English one
        } else if (currentAudioIndex() !== cfg.preferredAudio) hintToast('English audio found. Use the 🎧 menu to switch to it.');
        renderAudioMenu();
      })
      .catch(function () { inspecting = false; renderAudioMenu(); });
  }

  // ------------------------------------------------------------ start
  var needEnglishSwitch = audioTracks.length > 1 && cfg.preferredAudio != null && cfg.preferredAudio !== cfg.defaultAudio;
  if (needEnglishSwitch && cfg.remuxEnabled && cfg.directUrl) {
    // The file's default track is not English: start (automatically) from a remux that carries the English track.
    audioIndex = cfg.preferredAudio;
    prevMode = 'direct';
    triedModes.remux = true;
    renderAudioMenu();
    load('remux', 0, true);
  } else if (cfg.remuxEnabled && store.get(COMPAT_KEY) === '1') {
    triedModes.remux = true;
    load('remux', 0, true);          // compatibility mode was left on for this device
  } else {
    load('direct', 0, true);
  }
  updateCompatButton();
  if (!cfg.inspected) inspectNow();
})();
