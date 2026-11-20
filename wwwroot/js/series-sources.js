/* Available Sources of a series: Copy URL, Download and Download All (whole series or one season/group). No playback happens here. */
(function () {
  'use strict';
  var root = document.getElementById('series-sources');
  if (!root) return;

  function copy(text) {
    if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
    return new Promise(function (resolve, reject) {
      var ta = document.createElement('textarea');
      ta.value = text; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.opacity = '0';
      document.body.appendChild(ta); ta.select();
      try { document.execCommand('copy') ? resolve() : reject(new Error('copy failed')); } catch (e) { reject(e); } finally { document.body.removeChild(ta); }
    });
  }

  function say(text) { var s = root.querySelector('[data-download-status]'); if (s) s.textContent = text || ''; }

  // Browsers allow a page to start several downloads one after another; they are spaced out so none is dropped.
  var busy = false;
  function downloadAll(urls, label) {
    if (busy) return;
    if (urls.length === 0) return;
    if (urls.length > 3 && !window.confirm('Start ' + urls.length + ' downloads' + (label ? ' (' + label + ')' : '') + '? Your browser may ask to allow multiple downloads.')) return;
    busy = true;
    var i = 0;
    (function next() {
      if (i >= urls.length) { busy = false; say('Started ' + urls.length + ' download' + (urls.length === 1 ? '' : 's') + '.'); return; }
      say('Starting download ' + (i + 1) + ' of ' + urls.length + '…');
      var a = document.createElement('a');
      a.href = urls[i++]; a.setAttribute('download', ''); a.rel = 'noopener'; a.style.display = 'none';
      document.body.appendChild(a); a.click(); document.body.removeChild(a);
      setTimeout(next, 900);
    })();
  }

  function urlsIn(node) {
    return Array.prototype.map.call(node.querySelectorAll('[data-file-download]'), function (a) { return a.getAttribute('href'); }).filter(Boolean);
  }

  root.addEventListener('click', function (e) {
    var copyBtn = e.target.closest('.copy-source');
    if (copyBtn) {
      var original = copyBtn.textContent;
      copy(copyBtn.getAttribute('data-copy-url') || '').then(function () { copyBtn.textContent = 'Copied'; }, function () { copyBtn.textContent = 'Copy failed'; })
        .then(function () { setTimeout(function () { copyBtn.textContent = original; }, 1500); });
      return;
    }
    var all = e.target.closest('[data-download-all]');
    if (all) { downloadAll(urlsIn(root), 'whole series'); return; }
    var grp = e.target.closest('[data-download-group]');
    if (grp) {
      var g = grp.closest('[data-source-group]');
      var title = g && g.querySelector('h3') ? g.querySelector('h3').firstChild.textContent.trim() : '';
      downloadAll(g ? urlsIn(g) : [], title);
    }
  });
})();
