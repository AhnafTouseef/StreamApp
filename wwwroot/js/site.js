/* MovieHub site script: header, row scrolling, home hero carousel. Progressive enhancement only. */
(function () {
  'use strict';

  // ---- header: solid background after scrolling, mobile menu
  var header = document.getElementById('site-header');
  function onScroll() { if (header) header.classList.toggle('is-solid', window.scrollY > 24); }
  onScroll();
  window.addEventListener('scroll', onScroll, { passive: true });

  var toggle = document.getElementById('nav-toggle');
  var nav = document.getElementById('site-nav');
  if (toggle && nav) {
    toggle.addEventListener('click', function () {
      var open = nav.classList.toggle('is-open');
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
    });
  }

  // ---- rows: arrow buttons scroll by roughly one screenful
  document.querySelectorAll('.row').forEach(function (row) {
    var track = row.querySelector('.row-track');
    row.querySelectorAll('.row-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        track.scrollBy({ left: Number(btn.dataset.dir) * track.clientWidth * 0.85, behavior: 'smooth' });
      });
    });
  });

  // ---- "Watch" chip inside a card goes straight to the player without opening the details page
  document.addEventListener('click', function (e) {
    var go = e.target.closest && e.target.closest('.card-go');
    if (!go) return;
    e.preventDefault();
    e.stopPropagation();
    window.location.href = go.dataset.href;
  }, true);

  // ---- home hero
  var hero = document.getElementById('hero');
  var dataEl = document.getElementById('hero-data');
  if (!hero || !dataEl) return;

  var items;
  try { items = JSON.parse(dataEl.textContent || '[]'); } catch (_) { items = []; }
  if (!items.length) return;

  var copy = hero.querySelector('.hero-copy');
  var layers = hero.querySelectorAll('.hero-bg .layer');
  var dotsBox = hero.querySelector('.hero-dots');
  var els = {
    title: hero.querySelector('[data-f="title"]'),
    year: hero.querySelector('[data-f="year"]'),
    rating: hero.querySelector('[data-f="rating"]'),
    genres: hero.querySelector('[data-f="genres"]'),
    overview: hero.querySelector('[data-f="overview"]'),
    watch: hero.querySelector('[data-f="watch"]'),
    more: hero.querySelector('[data-f="more"]')
  };
  var current = 0, topLayer = 0, timer = null;
  var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function esc(s) { return String(s == null ? '' : s); }

  function render(item) {
    if (els.title) els.title.textContent = esc(item.title);
    if (els.year) { els.year.textContent = esc(item.year); els.year.hidden = !item.year; }
    if (els.rating) { els.rating.textContent = item.rating ? '★ ' + item.rating : ''; els.rating.hidden = !item.rating; }
    if (els.genres) {
      els.genres.innerHTML = '';
      (item.genres || []).forEach(function (g) {
        var s = document.createElement('span'); s.className = 'chip'; s.textContent = g; els.genres.appendChild(s);
      });
    }
    if (els.overview) els.overview.textContent = esc(item.overview);
    if (els.watch) els.watch.href = '/watch/' + item.id;
    if (els.more) els.more.href = '/movie/' + item.id;

    // cross-fade the backdrop: write to the hidden layer, then swap
    var next = 1 - topLayer;
    if (layers[next] && item.backdrop) {
      layers[next].style.backgroundImage = 'url("' + item.backdrop.replace(/"/g, '%22') + '")';
      layers[next].classList.add('is-on');
      layers[topLayer].classList.remove('is-on');
      topLayer = next;
    }
    if (dotsBox) Array.prototype.forEach.call(dotsBox.children, function (d, i) { d.classList.toggle('is-on', i === current); });
  }

  function show(index, animate) {
    current = (index + items.length) % items.length;
    if (!animate || reduce) { render(items[current]); return; }
    copy.classList.add('is-swapping');
    setTimeout(function () { render(items[current]); copy.classList.remove('is-swapping'); }, 280);
  }

  function restart() {
    clearInterval(timer);
    if (items.length > 1 && !reduce) timer = setInterval(function () { show(current + 1, true); }, 8000);
  }

  if (dotsBox) {
    dotsBox.innerHTML = '';
    items.forEach(function (it, i) {
      var b = document.createElement('button');
      b.type = 'button';
      b.setAttribute('aria-label', 'Show ' + it.title);
      b.addEventListener('click', function () { show(i, true); restart(); });
      dotsBox.appendChild(b);
    });
  }
  hero.addEventListener('mouseenter', function () { clearInterval(timer); });
  hero.addEventListener('mouseleave', restart);

  // Selecting a card (in any row) makes that movie the featured banner. Modified clicks keep the normal link behaviour.
  document.addEventListener('click', function (e) {
    var card = e.target.closest && e.target.closest('.card[data-feature]');
    if (!card || e.metaKey || e.ctrlKey || e.shiftKey || e.button !== 0) return;
    e.preventDefault();
    var item = {
      id: Number(card.dataset.feature), title: card.dataset.title, year: card.dataset.year,
      rating: card.dataset.rating, genres: card.dataset.genres ? card.dataset.genres.split(', ') : [],
      overview: card.dataset.overview, backdrop: card.dataset.backdrop
    };
    var existing = items.findIndex(function (x) { return x.id === item.id; });
    if (existing === -1) { items.unshift(item); existing = 0; if (dotsBox) { rebuildDots(); } }
    hero.classList.add('is-expanding');
    window.scrollTo({ top: 0, behavior: reduce ? 'auto' : 'smooth' });
    show(existing, true);
    restart();
    setTimeout(function () { hero.classList.remove('is-expanding'); }, 600);
  });

  function rebuildDots() {
    if (items.length > 8) items.length = 8; // keep the indicator compact
    dotsBox.innerHTML = '';
    items.forEach(function (it, i) {
      var b = document.createElement('button');
      b.type = 'button';
      b.setAttribute('aria-label', 'Show ' + it.title);
      b.addEventListener('click', function () { show(i, true); restart(); });
      dotsBox.appendChild(b);
    });
  }

  show(0, false);
  restart();
})();
