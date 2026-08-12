/**
 * Multi-document getElementById for Dock workspace (main + popout windows).
 */
const _origGetElementById = Document.prototype.getElementById;

function collectDocuments() {
    const docs = [document];
    const popouts = window.ywcDock?.api?.getPopouts?.() ?? [];
    for (const p of popouts) {
        try {
            if (p?.window?.document) docs.push(p.window.document);
        } catch { /* cross-origin guard */ }
    }
    return docs;
}

export function installDomShim() {
    if (window.__ywcDockDomShim) return;
    window.__ywcDockDomShim = true;

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
}

export function dispatchPanelResize() {
    window.dispatchEvent(new Event('resize'));
    for (const doc of collectDocuments()) {
        try {
            doc.defaultView?.dispatchEvent(new Event('resize'));
        } catch { /* ignore */ }
    }
    try { window.ywcMetersFit?.refit(); } catch { /* ignore */ }
}
