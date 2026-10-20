// Pre-paint theme application. Deliberately duplicated from src/lib/theme.ts: this has to
// run before the first paint, and a module import cannot — without it every load flashes
// the light theme before React mounts. theme.test.ts fails if the two ever disagree.
//
// A file of its own, loaded by a plain blocking <script src> in <head>, rather than an
// inline script (FU-091): the Content-Security-Policy allows scripts from this origin only,
// and an inline one would be refused, flashing the light theme on every load.
(function () {
  var fallback = 'light';
  var accentFallback = 'teal';
  var densityFallback = 'regular';
  try {
    var t = localStorage.getItem('coffer.theme');
    var ok = t === 'light' || t === 'light-hc' || t === 'dark' || t === 'dark-hc';
    document.documentElement.setAttribute('data-theme', ok ? t : fallback);
    var a = localStorage.getItem('coffer.accent');
    var aok = a === 'teal' || a === 'indigo' || a === 'rust';
    document.documentElement.setAttribute('data-accent', aok ? a : accentFallback);
    var d = localStorage.getItem('coffer.density');
    var dok = d === 'compressed' || d === 'regular' || d === 'relaxed';
    document.documentElement.setAttribute('data-density', dok ? d : densityFallback);
  } catch (e) {
    // Blocked storage (private window, embedded webview) throws on read.
    document.documentElement.setAttribute('data-theme', fallback);
    document.documentElement.setAttribute('data-accent', accentFallback);
    document.documentElement.setAttribute('data-density', densityFallback);
  }
})();
