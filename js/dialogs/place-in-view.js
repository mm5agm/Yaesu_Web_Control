// ── Keep a modeless dialog where the operator can see it ────────────────────
//
// Shared by Icom Web Control and Yaesu Web Control. Nothing radio-specific.
// Edit it here, in the core, never in a wwwroot copy.
//
// A dialog the operator has dragged is pinned position: fixed at the place
// they left it, and that place is remembered in CSS pixels. Change the
// browser's zoom and the same number is somewhere else on the screen: a
// dialog left on the right of the window at 50% zoom is past its right-hand
// edge at 100%. The dialog then opens where nobody can see it, the next press
// of its button closes it again, and the button seems to do nothing at all.
// A smaller window, or a place saved on a bigger monitor, does the same.
//
// And a dialog that has never been dragged opens wherever its markup sits in
// the page, which on a long page is a long way down - below the bottom of the
// window, where the page cannot scroll.
//
// So when a watched dialog opens: one off the window is pulled back into it,
// and, if asked, one that has never been placed is pinned across the top.
// Only the visible style changes; whatever the page has saved is left alone,
// so the next drag saves a place that is on the screen.

/** How much of a dialog must still be on the window to count as visible. */
export const GRIP = 40;

/**
 * Where a fixed dialog should go so the operator can see it, or null where it
 * is already fine. Pure, so it can be tested.
 *
 * @param {{left:number,top:number,right:number,width:number,height:number}} r  its box now
 * @param {{width:number,height:number}} view  the window's page area
 * @returns {{left?:number,top?:number}|null}
 */
export function pullIntoView(r, view) {
    const out = {};
    if (r.top < 0 || r.top > view.height - GRIP)
        out.top = Math.max(0, Math.min(80, view.height - r.height));
    if (r.right < GRIP || r.left > view.width - GRIP)
        out.left = Math.max(0, (view.width - r.width) / 2);
    return (out.left === undefined && out.top === undefined) ? null : out;
}

/**
 * Where a dialog that has never been placed should go: centred across the
 * top of the window. Pure, so it can be tested.
 *
 * @param {number} width  the dialog's width
 * @param {{width:number,height:number}} view
 * @returns {{left:number,top:number}}
 */
export function firstPlace(width, view) {
    return {
        left: Math.max(0, (view.width - width) / 2),
        top:  Math.min(80, Math.max(0, view.height / 10)),
    };
}

/**
 * Put an open dialog where it can be seen.
 *
 * @param {HTMLDialogElement} dialog
 * @param {object} [opts]
 * @param {boolean} [opts.placeFirst=false]  pin a dialog that is still in the
 *        page flow across the top of the window, instead of leaving it where
 *        its markup sits
 */
export function keepInView(dialog, { placeFirst = false } = {}) {
    if (!dialog?.open) return;
    const view = { width: window.innerWidth, height: window.innerHeight };
    if (getComputedStyle(dialog).position !== 'fixed') {
        if (!placeFirst) return;
        dialog.style.position  = 'fixed';
        dialog.style.margin    = '0';
        dialog.style.transform = 'none';
        dialog.style.left = '0px';
        dialog.style.top  = '0px';
        const p = firstPlace(dialog.getBoundingClientRect().width, view);
        dialog.style.left = `${p.left}px`;
        dialog.style.top  = `${p.top}px`;
        return;
    }
    const p = pullIntoView(dialog.getBoundingClientRect(), view);
    if (!p) return;
    if (p.left !== undefined) dialog.style.left = `${p.left}px`;
    if (p.top  !== undefined) dialog.style.top  = `${p.top}px`;
}

/**
 * Call keepInView every time the dialog opens, however it is opened.
 *
 * @param {HTMLDialogElement} dialog
 * @param {object} [opts]  as for keepInView
 */
export function watchInView(dialog, opts) {
    if (!dialog) return;
    new MutationObserver(() => {
        if (dialog.open) keepInView(dialog, opts);
    }).observe(dialog, { attributes: true, attributeFilter: ['open'] });
    if (dialog.open) keepInView(dialog, opts);
}
