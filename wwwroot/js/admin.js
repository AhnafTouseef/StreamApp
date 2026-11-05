/* MovieHub admin: popup forms (<dialog class="modal">). */
(function () {
  'use strict';
  document.querySelectorAll('dialog.modal').forEach(function (d) {
    var back = d.getAttribute('data-return');
    var autoOpened = d.hasAttribute('data-autoopen');

    function focusFirst() {
      var f = d.querySelector('input:not([type=hidden]), select, textarea');
      if (f) f.focus();
    }
    function open() {
      if (typeof d.showModal !== 'function') { d.setAttribute('open', ''); return; }
      if (d.hasAttribute('open')) d.removeAttribute('open'); // server-rendered non-modal copy (no flash); upgrade to a real modal. removeAttribute fires no close event
      d.showModal(); focusFirst();
    }
    if (autoOpened) open();

    // Buttons can pre-select a field: data-preset="Src.CategoryId=3" (applied to a freshly reset form).
    function preset(btn) {
      var form = d.querySelector('form'); if (!form) return;
      if (!autoOpened) form.reset();
      (btn.getAttribute('data-preset') || '').split(';').forEach(function (pair) {
        var i = pair.indexOf('='); if (i < 1) return;
        var el = form.elements[pair.slice(0, i)]; if (el) el.value = pair.slice(i + 1);
      });
    }
    document.querySelectorAll('[data-open="' + d.id + '"]').forEach(function (b) { b.addEventListener('click', function () { preset(b); open(); }); });
    d.querySelectorAll('[data-close]').forEach(function (b) { b.addEventListener('click', function () { d.close(); }); });
    // click on the dimmed area (outside the form box) closes it
    d.addEventListener('mousedown', function (e) { if (e.target === d) d.close(); });
    // leaving an edit / error view goes back to the clean list URL
    d.addEventListener('close', function () { if (autoOpened && back) window.location.href = back; });
  });

  // "⋯" kebabs: only one open at a time; click elsewhere or press Esc to close
  var kebabs = document.querySelectorAll('details.kebab');
  document.addEventListener('click', function (e) {
    kebabs.forEach(function (m) { if (m.open && !m.contains(e.target)) m.open = false; });
  });
  kebabs.forEach(function (m) {
    m.addEventListener('toggle', function () { if (m.open) kebabs.forEach(function (o) { if (o !== m) o.open = false; }); });
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') kebabs.forEach(function (m) { m.open = false; }); });
})();
/* Movies list: Edit opens the entry in a popup (iframe), so the list, its filters and its page stay exactly as they are. */
(function () {
  'use strict';
  var dlg = document.getElementById('edit-dialog');
  var frame = document.getElementById('edit-frame');
  if (!dlg || !frame || typeof dlg.showModal !== 'function') return;   // without it the plain links still open the full page
  var titleEl = document.getElementById('edit-title');
  var loads = 0;

  function openEdit(href) {
    loads = 0; titleEl.textContent = 'Edit entry';
    frame.src = href + (href.indexOf('?') < 0 ? '?embed=1' : '&embed=1');
    dlg.showModal();
  }

  // Re-read the current list URL (filters and page included) and swap in the fresh rows, count and pager.
  function refreshList() {
    return fetch(window.location.href, { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
      .then(function (r) { return r.text(); })
      .then(function (html) {
        var doc = new DOMParser().parseFromString(html, 'text/html');
        ['#bulk', '.filters .result-count', '.pager'].forEach(function (sel) {
          var oldEl = document.querySelector(sel), fresh = doc.querySelector(sel);
          if (oldEl && fresh) oldEl.replaceWith(fresh); else if (oldEl && !fresh) oldEl.remove();
        });
        if (!document.querySelector('.pager') && doc.querySelector('.pager')) document.querySelector('main, .admin-main').appendChild(doc.querySelector('.pager'));
      })
      .catch(function () { /* the list simply stays as it was */ });
  }

  function closeEdit() { if (dlg.open) dlg.close(); }

  document.addEventListener('click', function (e) {
    var a = e.target.closest && e.target.closest('a[data-edit]');
    if (!a || e.metaKey || e.ctrlKey || e.shiftKey || e.button !== 0) return;
    e.preventDefault();
    openEdit(a.getAttribute('href'));
  });

  frame.addEventListener('load', function () {
    loads++;
    try { var h = frame.contentDocument.querySelector('h1'); if (h) titleEl.textContent = h.textContent; } catch (_) { /* cross-origin: keep the default title */ }
  });

  dlg.querySelectorAll('[data-close]').forEach(function (b) { b.addEventListener('click', closeEdit); });
  dlg.addEventListener('mousedown', function (e) { if (e.target === dlg) closeEdit(); });
  dlg.addEventListener('close', function () {
    var changed = loads > 1;       // the first load is the form itself; any later load means something was submitted
    frame.src = 'about:blank';
    if (changed) refreshList();
  });
  window.addEventListener('message', function (e) {
    if (e.origin === window.location.origin && e.data && e.data.moviehub === 'edit-done') closeEdit();
  });
})();
