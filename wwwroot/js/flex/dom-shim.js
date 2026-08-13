/**
 * Multi-document getElementById for Flex workspace (main + popout windows).
 * FlexLayout portals tab DOM into the popout document while JS still runs in the main window.
 */
const _origGetElementById = Document.prototype.getElementById;
const _origQuerySelector = Document.prototype.querySelector;

/** @type {Set<Window>} */
const popoutWindows = new Set();

export function registerFlexPopoutWindow(win) {
    if (win && win !== window) popoutWindows.add(win);
}

export function unregisterFlexPopoutWindow(win) {
    if (win) popoutWindows.delete(win);
}

function collectDocuments() {
    const docs = [document];
    for (const w of [...popoutWindows]) {
        try {
            if (w.closed) {
                popoutWindows.delete(w);
                continue;
            }
            if (w.document) docs.push(w.document);
        } catch { /* cross-origin guard */ }
    }
    return docs;
}

export function installDomShim() {
    if (window.__ywcFlexDomShim) return;
    window.__ywcFlexDomShim = true;

    window.ywcGetElementById = function ywcGetElementById(id) {
        for (const doc of collectDocuments()) {
            const el = _origGetElementById.call(doc, id);
            if (el) return el;
        }
        return null;
    };

    Document.prototype.getElementById = function patchedGetElementById(id) {
        if (this === document) {
            return window.ywcGetElementById(id);
        }
        return _origGetElementById.call(this, id);
    };

    // Keep querySelector('#id') working for a few site.js call sites that use it
    // against document for known singleton IDs after a popout remount.
    Document.prototype.querySelector = function patchedQuerySelector(sel) {
        if (this === document && typeof sel === 'string' && /^#[\w-]+$/.test(sel)) {
            const byId = window.ywcGetElementById(sel.slice(1));
            if (byId) return byId;
        }
        return _origQuerySelector.call(this, sel);
    };
}

export function dispatchPanelResize() {
    window.dispatchEvent(new Event('resize'));
    window.dispatchEvent(new Event('ywc-flex-panel-resize'));
    for (const doc of collectDocuments()) {
        try {
            doc.defaultView?.dispatchEvent(new Event('resize'));
        } catch { /* ignore */ }
    }
    try { window.ywcMetersFit?.refit(); } catch { /* ignore */ }
    try { window.ywcVfoFit?.refit(); } catch { /* ignore */ }
    try { window.ywcWidgetFit?.refit(); } catch { /* ignore */ }
}
