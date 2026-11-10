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

  // ---- rows / sliders: arrows (header and edge), page dots, mouse drag, snap scrolling
  document.querySelectorAll('.row').forEach(function (row) {
    var track = row.querySelector('.row-track');
    if (!track) return;
    var dots = row.querySelector('.row-dots');
    var arrows = row.querySelectorAll('.row-btn, .row-edge');

    function page() { return Math.max(1, track.clientWidth * 0.9); }
    arrows.forEach(function (btn) {
      btn.addEventListener('click', function () {
        track.scrollBy({ left: Number(btn.dataset.dir) * page(), behavior: 'smooth' });
      });
    });

    function pages() { return Math.max(1, Math.round(track.scrollWidth / track.clientWidth)); }
    function sync() {
      var max = track.scrollWidth - track.clientWidth;
      var atStart = track.scrollLeft <= 2, atEnd = track.scrollLeft >= max - 2;
      row.classList.toggle('at-start', atStart);
      row.classList.toggle('at-end', atEnd || max <= 2);
      arrows.forEach(function (b) {
        var back = Number(b.dataset.dir) < 0;
        b.disabled = back ? atStart : (atEnd || max <= 2);
      });
      if (!dots) return;
      var n = pages();
      if (dots.children.length !== n) {
        dots.innerHTML = '';
        if (n > 1 && n <= 12) for (var i = 0; i < n; i++) dots.appendChild(document.createElement('span'));
      }
      var cur = max <= 2 ? 0 : Math.round((track.scrollLeft / max) * (n - 1));
      Array.prototype.forEach.call(dots.children, function (d, i) { d.classList.toggle('is-on', i === cur); });
    }
    track.addEventListener('scroll', function () { window.requestAnimationFrame(sync); }, { passive: true });
    window.addEventListener('resize', sync);
    sync();

    // Mouse drag to slide (touch already scrolls natively). A drag never counts as a click on a card.
    var down = false, startX = 0, startLeft = 0, moved = false;
    track.addEventListener('pointerdown', function (e) {
      if (e.pointerType !== 'mouse' || e.button !== 0) return;
      down = true; moved = false; startX = e.clientX; startLeft = track.scrollLeft;
    });
    window.addEventListener('pointermove', function (e) {
      if (!down) return;
      var dx = e.clientX - startX;
      if (!moved && Math.abs(dx) > 6) { moved = true; track.classList.add('is-dragging'); }
      if (moved) track.scrollLeft = startLeft - dx;
    });
    window.addEventListener('pointerup', function () {
      if (!down) return;
      down = false;
      track.classList.remove('is-dragging');
    });
    track.addEventListener('click', function (e) { if (moved) { e.preventDefault(); e.stopPropagation(); moved = false; } }, true);
    track.addEventListener('dragstart', function (e) { e.preventDefault(); }); // poster images must not start a native image drag
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
    if (!card || !card.dataset.feature || e.metaKey || e.ctrlKey || e.shiftKey || e.button !== 0) return;
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

// ---- header search: matching titles drop down while typing.
// Its own function on purpose: the script above returns early on pages without a hero, which must not stop the search box from working.
(function () {
  'use strict';
    var form = document.querySelector('.header-search');
    var input = form && form.querySelector('input[name="q"]');
    if (!input || !window.fetch) return;

    var list = document.createElement('ul');
    list.className = 'suggest'; list.id = 'suggest-list'; list.setAttribute('role', 'listbox'); list.hidden = true;
    form.appendChild(list);
    input.setAttribute('role', 'combobox');
    input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-controls', 'suggest-list');
    input.setAttribute('aria-expanded', 'false');

    var timer = 0, ctl = null, shown = '', active = -1, seq = 0;

    function esc(t) { return String(t).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    function mark(title, typed) {
      var i = title.toLowerCase().indexOf(typed.toLowerCase());
      if (i < 0 || !typed) return esc(title);
      return esc(title.slice(0, i)) + '<b>' + esc(title.slice(i, i + typed.length)) + '</b>' + esc(title.slice(i + typed.length));
    }
    function close() { list.hidden = true; input.setAttribute('aria-expanded', 'false'); active = -1; input.removeAttribute('aria-activedescendant'); }
    function setActive(n) {
      var items = list.querySelectorAll('li[role=option]');
      if (!items.length) return;
      active = (n + items.length) % items.length;
      items.forEach(function (li, i) { li.classList.toggle('is-active', i === active); li.setAttribute('aria-selected', i === active ? 'true' : 'false'); });
      input.setAttribute('aria-activedescendant', items[active].id);
      items[active].scrollIntoView({ block: 'nearest' });
    }
    function render(rows, typed) {
      var html = rows.map(function (r, i) {
        var meta = [r.year, r.category].filter(Boolean).join(' · ');
        var img = r.poster ? '<img src="' + esc(r.poster) + '" alt="" loading="lazy" width="36" height="54">' : '<span class="suggest-noimg"></span>';
        return '<li role="option" id="suggest-' + i + '" aria-selected="false"><a href="/movie/' + r.id + '" tabindex="-1">' + img +
          '<span class="suggest-text"><span class="suggest-title">' + mark(r.title, typed) + '</span>' + (meta ? '<span class="suggest-meta">' + esc(meta) + '</span>' : '') + '</span></a></li>';
      }).join('');
      html += '<li class="suggest-all" role="presentation"><a href="/search?q=' + encodeURIComponent(typed) + '" tabindex="-1">See all results for “' + esc(typed) + '”</a></li>';
      list.innerHTML = html; list.hidden = false; active = -1;
      input.setAttribute('aria-expanded', 'true'); input.removeAttribute('aria-activedescendant');
    }
    function ask() {
      var typed = input.value.trim();
      if (typed.length < 2) { shown = ''; close(); return; }
      if (typed === shown && !list.hidden) return;
      if (ctl) ctl.abort();
      ctl = typeof AbortController === 'function' ? new AbortController() : null;   // very old browsers: no cancelling, stale answers are ignored below
      var mine = ++seq;
      fetch('/api/suggest?q=' + encodeURIComponent(typed), { signal: ctl ? ctl.signal : undefined, headers: { 'Accept': 'application/json' } })
        .then(function (r) { return r.ok ? r.json() : []; })
        .then(function (rows) {
          if (mine !== seq || input.value.trim() !== typed) return;   // an older answer arriving late
          shown = typed;
          if (!rows.length) { close(); return; }
          render(rows, typed);
        })
        .catch(function () { /* aborted or offline: the normal search still works */ });
    }

    input.addEventListener('input', function () { clearTimeout(timer); timer = setTimeout(ask, 120); });
    input.addEventListener('focus', function () { if (input.value.trim().length >= 2) ask(); });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        if (list.hidden) { ask(); return; }
        e.preventDefault(); setActive(active + (e.key === 'ArrowDown' ? 1 : -1));
      } else if (e.key === 'Escape') { if (!list.hidden) { e.preventDefault(); close(); } }   // first Escape only closes the list (a search field would also clear itself)
      else if (e.key === 'Enter' && !list.hidden && active >= 0) {
        var a = list.querySelectorAll('li[role=option] a')[active];
        if (a) { e.preventDefault(); window.location.href = a.getAttribute('href'); }
      }
    });
    list.addEventListener('mousedown', function (e) { e.preventDefault(); });   // keep focus in the box so a click is not lost to blur
    list.addEventListener('mousemove', function (e) {
      var li = e.target.closest && e.target.closest('li[role=option]');
      if (!li) return;
      var items = Array.prototype.slice.call(list.querySelectorAll('li[role=option]'));
      var n = items.indexOf(li); if (n !== active) setActive(n);
    });
    document.addEventListener('click', function (e) { if (!form.contains(e.target)) close(); });
    input.addEventListener('blur', function () { setTimeout(close, 120); });
    form.addEventListener('submit', function () { clearTimeout(timer); if (ctl) ctl.abort(); close(); });
})();
