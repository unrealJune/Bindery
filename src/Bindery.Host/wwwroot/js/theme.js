/*
    Theme selection.

    Loaded render-blocking in <head> so the chosen theme is applied before first paint; an
    inline script would be the usual way to do this, but the CSP has no `unsafe-inline` and
    loosening it for a colour preference would be a bad trade.

    All this does is set `data-theme`, which flips `color-scheme` on the root. Every themed
    value in bindery.css is a `light-dark()` pair, so with JavaScript off — or before this
    file has run — the OS preference already produces a correct page. There is no flash to
    prevent beyond honouring an explicit override.
*/
(function () {
    var root = document.documentElement;

    root.classList.add("js");

    try {
        var stored = localStorage.getItem("bindery-theme");
        if (stored === "light" || stored === "dark") {
            root.setAttribute("data-theme", stored);
        }
    } catch (error) {
        /* Private mode, or storage denied. Auto is a perfectly good answer. */
    }
})();
