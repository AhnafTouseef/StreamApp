/* Season / episode picker for series pages. Lives entirely outside the video player: it only switches which episode buttons are visible,
   and (on the watch page) moves on to the next episode when the video ends. */
(function () {
  'use strict';
  var picker = document.querySelector('.series-picker');
  if (!picker) return;

  var tabs = picker.querySelector('.season-tabs');
  var buttons = Array.prototype.slice.call(picker.querySelectorAll('.season-btn'));
  var panels = Array.prototype.slice.call(picker.querySelectorAll('[data-season-panel]'));

  // Scrolls only the strip / list itself, never the page (scrollIntoView would also move the page down to the button).
  function centerIn(container, el, axis, smooth) {
    if (!container || !el) return;
    var c = container.getBoundingClientRect(), r = el.getBoundingClientRect();
    if (axis === 'x') {
      var left = container.scrollLeft + (r.left - c.left) - (c.width - r.width) / 2;
      if (smooth && container.scrollTo) container.scrollTo({ left: left, behavior: 'smooth' }); else container.scrollLeft = left;
    } else if (container.scrollHeight > container.clientHeight) {
      container.scrollTop += (r.top - c.top) - (c.height - r.height) / 2;
    }
  }

  function show(n, scroll) {
    buttons.forEach(function (b) {
      var on = b.getAttribute('data-season') === String(n);
      b.classList.toggle('is-active', on);
      b.setAttribute('aria-selected', on ? 'true' : 'false');
      if (on && scroll) centerIn(tabs, b, 'x', true);
    });
    panels.forEach(function (p) { p.hidden = p.getAttribute('data-season-panel') !== String(n); });
  }

  buttons.forEach(function (b) { b.addEventListener('click', function () { show(b.getAttribute('data-season'), true); }); });
  var active = picker.querySelector('.season-btn.is-active');
  if (active) centerIn(tabs, active, 'x', false);
  var curEp = picker.querySelector('.ep-btn.is-active');
  if (curEp) centerIn(curEp.closest('.ep-grid'), curEp, 'y', false);   // inside the episode list only; the page does not move

  // ---- watch page only: "play next episode automatically"
  var video = document.getElementById('video');
  var nextUrl = picker.getAttribute('data-next');
  if (!video) return;

  var KEY = 'sa.series.autonext';
  function pref() { try { return window.localStorage.getItem(KEY) !== '0'; } catch (_) { return true; } }
  var toggle = document.getElementById('autonext');
  if (toggle) {
    toggle.checked = pref();
    toggle.addEventListener('change', function () {
      try { window.localStorage.setItem(KEY, toggle.checked ? '1' : '0'); } catch (_) { /* ignore */ }
      var pl = document.getElementById('player');            // the switch must not keep the keyboard: Space / arrows go back to the player
      if (pl) { try { pl.focus({ preventScroll: true }); } catch (_) { /* ignore */ } }
    });
  }
  if (nextUrl) {
    // 'sa-ended' comes from the player and means the episode really reached its end (a stream that merely ran out of data earlier is reconnected by the player instead).
    video.addEventListener('sa-ended', function () { if (pref()) window.location.href = nextUrl; });
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
