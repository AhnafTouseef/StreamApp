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

    document.querySelectorAll('[data-open="' + d.id + '"]').forEach(function (b) { b.addEventListener('click', open); });
    d.querySelectorAll('[data-close]').forEach(function (b) { b.addEventListener('click', function () { d.close(); }); });
    // click on the dimmed area (outside the form box) closes it
    d.addEventListener('mousedown', function (e) { if (e.target === d) d.close(); });
    // leaving an edit / error view goes back to the clean list URL
    d.addEventListener('close', function () { if (autoOpened && back) window.location.href = back; });
  });
})();
