// pip-dock.js - move an element into an always-on-top window (Document
// Picture-in-Picture) and back. The RTTY Contest view uses it for "On top"
// on the RTTY tuner, so the tuning scope stays over the logging program.
//
// The element itself moves, not a copy: its listeners, its canvas and the
// code that holds references to its children all keep working, because the
// script that drives it still runs in the main page. The page's stylesheets
// are copied into the PiP window so it looks the same there.
//
// Limits (Chrome and Edge only; one PiP window per browser; script can set
// its size but not its place): see docs/design/rtty-contest-gui-plan.md.
// Closing the PiP window, or the main page going away, puts the element back.

/** Whether this browser can open an always-on-top window. */
export function pipSupported(win = globalThis.window) {
    return !!win && 'documentPictureInPicture' in win;
}

// Every stylesheet of the page, as rules where they can be read and as a
// link where they cannot (another origin).
function copyStyles(from, to) {
    for (const sheet of [...from.styleSheets]) {
        try {
            const st = to.createElement('style');
            st.textContent = [...sheet.cssRules].map(r => r.cssText).join('\n');
            to.head.append(st);
        } catch {
            if (!sheet.href) continue;
            const link = to.createElement('link');
            link.rel = 'stylesheet';
            link.href = sheet.href;
            to.head.append(link);
        }
    }
}

/**
 * Open the PiP window and move el into it. Call from a click: the browser
 * refuses one without a user gesture.
 *
 * @param {HTMLElement} el
 * @param {object} [opts]
 * @param {number} [opts.width], [opts.height]  the window's page area; default el's size
 * @param {string} [opts.bodyClass]  classes for the PiP body, e.g. for a theme
 * @param {string} [opts.background]  the PiP body's background
 * @param {string} [opts.className]  classes on el while it is in the PiP window,
 *        so the host's styles can make it fill the window there
 * @param {() => void} [opts.onDock]  el is back in the page
 * @returns {Promise<{ window: Window, dock: () => void }>}
 */
export async function pipDock(el, opts = {}) {
    const rc = el.getBoundingClientRect();
    const pip = await window.documentPictureInPicture.requestWindow({
        width: Math.round(opts.width ?? rc.width),
        height: Math.round(opts.height ?? rc.height),
    });
    copyStyles(document, pip.document);
    pip.document.title = document.title;
    const html = pip.document.documentElement;
    for (const a of [...document.documentElement.attributes]) if (a.name !== 'style') html.setAttribute(a.name, a.value);
    const body = pip.document.body;
    body.style.margin = '0';
    // The page may not scroll (the RTTY Contest view does not), but the PiP
    // window is as small as the operator makes it: what does not fit has to
    // scroll rather than be cut off.
    html.style.overflow = body.style.overflow = 'auto';
    html.style.height = body.style.height = '100%';
    body.style.background = opts.background ?? getComputedStyle(document.body).backgroundColor;
    if (opts.bodyClass) body.className = opts.bodyClass;

    // A marker where el was, so it goes back to the same place.
    const mark = document.createComment('pip-dock');
    el.before(mark);
    const added = (opts.className ?? '').split(/\s+/).filter(c => c && !el.classList.contains(c));
    el.classList.add(...added);
    body.append(el);

    let docked = false;
    const dock = () => {
        if (docked) return;
        docked = true;
        el.classList.remove(...added);
        mark.replaceWith(el);
        try { pip.close(); } catch { /* already closing */ }
        opts.onDock?.();
    };
    pip.addEventListener('pagehide', dock, { once: true });
    window.addEventListener('pagehide', dock, { once: true });
    return { window: pip, dock };
}
