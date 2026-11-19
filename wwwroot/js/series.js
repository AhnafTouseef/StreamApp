/* Season / episode picker for series pages. Lives entirely outside the video player: it only switches which episode buttons are visible,
   and (on the watch page) moves on to the next episode when the video ends. */
(function () {
  'use strict';
  var picker = document.querySelector('.series-picker');
  if (!picker) return;

  var tabs = picker.querySelector('.season-tabs');
  var buttons = Array.prototype.slice.call(picker.querySelectorAll('.season-btn'));
  var panels = Array.prototype.slice.call(picker.querySelectorAll('[data-season-panel]'));

  function show(n, scroll) {
    buttons.forEach(function (b) {
      var on = b.getAttribute('data-season') === String(n);
      b.classList.toggle('is-active', on);
      b.setAttribute('aria-selected', on ? 'true' : 'false');
      if (on && scroll && tabs && b.scrollIntoView) { try { b.scrollIntoView({ block: 'nearest', inline: 'center', behavior: 'smooth' }); } catch (_) { b.scrollIntoView(); } }
    });
    panels.forEach(function (p) { p.hidden = p.getAttribute('data-season-panel') !== String(n); });
  }

  buttons.forEach(function (b) { b.addEventListener('click', function () { show(b.getAttribute('data-season'), true); }); });
  var active = picker.querySelector('.season-btn.is-active');
  if (active && tabs) { try { tabs.scrollLeft = Math.max(0, active.offsetLeft - 16); } catch (_) { /* ignore */ } }
  var curEp = picker.querySelector('.ep-btn.is-active');
  if (curEp && curEp.scrollIntoView) { try { curEp.scrollIntoView({ block: 'nearest', inline: 'center' }); } catch (_) { /* ignore */ } }

  // ---- watch page only: "play next episode automatically"
  var video = document.getElementById('video');
  var nextUrl = picker.getAttribute('data-next');
  if (!video) return;

  var KEY = 'sa.series.autonext';
  function pref() { try { return window.localStorage.getItem(KEY) !== '0'; } catch (_) { return true; } }
  var toggle = document.getElementById('autonext');
  if (toggle) {
    toggle.checked = pref();
    toggle.addEventListener('change', function () { try { window.localStorage.setItem(KEY, toggle.checked ? '1' : '0'); } catch (_) { /* ignore */ } });
  }
  if (nextUrl) {
    video.addEventListener('ended', function () { if (pref()) window.location.href = nextUrl; });
  }

  // Arrived through "next episode": start it (best effort; browsers may still require a tap).
  if (/[?&]next=1\b/.test(window.location.search)) {
    var tries = 0;
    var t = setInterval(function () {
      tries++;
      var big = document.getElementById('big-play');
      if (!video.paused || tries > 12) { clearInterval(t); return; }
      if (video.readyState >= 2 && big) { clearInterval(t); big.click(); }
    }, 500);
  }
})();
