// ── Bring the clicked modeless dialog to the front ────────────────────────
//
// Shared by Icom Web Control and Yaesu Web Control. Nothing radio-specific:
// it watches <dialog> elements and nothing else.
//
// A <dialog> opened with show() rather than showModal() is not in the
// browser's top layer, so when two of them overlap, the one later in the
// document wins whichever the operator is actually using. This keeps a
// stacking order instead: a dialog comes to the front when it opens and
// whenever it is pressed anywhere (header, body, a button inside it).
//
// The order is written as inline z-index values in a fixed band, renumbered
// from the bottom every time, so the numbers never climb. The band sits just
// under Bootstrap's modal backdrop (1050), so a Bootstrap message box still
// covers every dialog, and well over anything a page gives its own z-index
// (gauge overlays, spectrum controls, focused button-group buttons). The
// host's CSS should give modeless dialogs the band's base value too, so they
// are already above the page before anything has been clicked:
//
//   dialog[open]:not(:modal) { z-index: 1040; }
//
// A dialog laid out in the page flow (position static or relative, as Radio
// Display's docked scope is) is left alone: z-index on it would do nothing
// useful, and it isn't floating over anything.
(function () {
    const BAND_BASE = 1040;
    const BAND_TOP = 1049;

    // Front-most last. Holds only dialogs that are open and floating.
    let order = [];

    function isFloating(dlg) {
        if (!dlg.open) return false;
        try { if (dlg.matches(':modal')) return false; } catch { /* old browser: treat as modeless */ }
        const pos = getComputedStyle(dlg).position;
        return pos === 'fixed' || pos === 'absolute';
    }

    function restack() {
        order = order.filter(isFloating);
        order.forEach((dlg, i) => {
            dlg.style.zIndex = String(Math.min(BAND_BASE + i, BAND_TOP));
        });
    }

    function bringToFront(dlg) {
        if (!isFloating(dlg)) return;
        if (order[order.length - 1] === dlg) return;
        order = order.filter(d => d !== dlg);
        order.push(dlg);
        restack();
    }

    // Capture phase, so a handler inside the dialog that stops propagation
    // cannot keep it at the back. Never preventDefault here: that would take
    // focus away from the input or button that was pressed.
    document.addEventListener('pointerdown', e => {
        const dlg = e.target instanceof Element ? e.target.closest('dialog') : null;
        if (dlg) bringToFront(dlg);
    }, true);

    // A dialog opened while others are up belongs on top of them.
    new MutationObserver(records => {
        for (const r of records) {
            const dlg = r.target;
            if (dlg.tagName !== 'DIALOG') continue;   // <details> has an open attribute too
            if (dlg.open) bringToFront(dlg);
            else {
                dlg.style.zIndex = '';
                order = order.filter(d => d !== dlg);
            }
        }
    }).observe(document.documentElement, { subtree: true, attributes: true, attributeFilter: ['open'] });
})();
