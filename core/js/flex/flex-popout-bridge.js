/**
 * Pop-out bridge for the Flex UI page.
 *
 * Popping a FlexLayout tab out moves the panel's DOM into the pop-out
 * window's document. The page's code was written for one document, and
 * reaches its elements and events through this window's `document` and
 * `window`. Left alone, a popped-out panel then:
 *
 *   - is never found by document.getElementById / querySelector here, so
 *     every update or redraw that looks its element up is skipped (the
 *     spectrum, S-meter and filter display stay blank);
 *   - never reaches the page-wide listeners -- a delegated click handler, a
 *     keyboard shortcut, or the document mouseup that ends a slider or
 *     splitter drag, so a drag started in the pop-out never ends.
 *
 * Rather than teach every panel about other windows, this treats each open
 * pop-out as part of the page:
 *
 *   - getElementById / querySelector here fall back to the pop-outs when this
 *     document has no match, and querySelectorAll adds the pop-outs' matches;
 *   - the input listeners (mouse, pointer, touch, key, click, wheel, form
 *     events) registered on this document or window are also registered on
 *     every pop-out's, including ones added later, and removed from them too;
 *   - document.activeElement is the focused element of whichever window has
 *     the focus, so "is the operator typing?" checks still hold in a pop-out.
 *
 * With no pop-out open every call goes straight to the browser's own, so
 * nothing changes for a page that never pops anything out.
 *
 * Classic script, loaded first in _FlexLayout.cshtml so it is in place before
 * any other script registers a listener. flex-workspace.js calls
 * ywcFlexPopouts.add(win) when a panel arrives in a pop-out and
 * ywcFlexPopouts.remove(win) when that pop-out closes.
 */
(function () {
    'use strict';
    if (window.ywcFlexPopouts) return;

    const MIRRORED = new Set([
        'click', 'dblclick', 'auxclick', 'contextmenu',
        'mousedown', 'mouseup', 'mousemove', 'mouseover', 'mouseout',
        'pointerdown', 'pointerup', 'pointermove', 'pointercancel',
        'touchstart', 'touchmove', 'touchend', 'touchcancel',
        'wheel', 'keydown', 'keyup', 'input', 'change', 'focusin', 'focusout',
    ]);

    const popouts = new Set();
    // Listeners registered on this window's document / window, by target.
    const records = { document: [], window: [] };

    const nativeAdd = EventTarget.prototype.addEventListener;
    const nativeRemove = EventTarget.prototype.removeEventListener;
    const nativeGetById = Document.prototype.getElementById;
    const nativeQuery = Document.prototype.querySelector;
    const nativeQueryAll = Document.prototype.querySelectorAll;

    function targetsIn(win) {
        return { document: win.document, window: win };
    }

    function capture(options) {
        return typeof options === 'boolean' ? options : !!options?.capture;
    }

    function liveDocs() {
        const docs = [];
        for (const win of popouts) {
            if (win.closed) { popouts.delete(win); continue; }
            docs.push(win.document);
        }
        return docs;
    }

    function mirror(kind, self) {
        self.addEventListener = function addEventListener(type, listener, options) {
            nativeAdd.call(this, type, listener, options);
            if (!listener || !MIRRORED.has(type)) return;
            const list = records[kind];
            const cap = capture(options);
            if (list.some((r) => r.type === type && r.listener === listener && r.capture === cap)) return;
            list.push({ type, listener, options, capture: cap });
            for (const win of popouts) {
                if (!win.closed) nativeAdd.call(targetsIn(win)[kind], type, listener, options);
            }
        };
        self.removeEventListener = function removeEventListener(type, listener, options) {
            nativeRemove.call(this, type, listener, options);
            if (!listener || !MIRRORED.has(type)) return;
            const cap = capture(options);
            records[kind] = records[kind].filter((r) => !(r.type === type && r.listener === listener && r.capture === cap));
            for (const win of popouts) {
                if (!win.closed) nativeRemove.call(targetsIn(win)[kind], type, listener, options);
            }
        };
    }
    mirror('document', document);
    mirror('window', window);

    document.getElementById = function getElementById(id) {
        const el = nativeGetById.call(this, id);
        if (el || !popouts.size) return el;
        for (const doc of liveDocs()) {
            const found = nativeGetById.call(doc, id);
            if (found) return found;
        }
        return null;
    };

    document.querySelector = function querySelector(selector) {
        const el = nativeQuery.call(this, selector);
        if (el || !popouts.size) return el;
        for (const doc of liveDocs()) {
            const found = nativeQuery.call(doc, selector);
            if (found) return found;
        }
        return null;
    };

    document.querySelectorAll = function querySelectorAll(selector) {
        const here = nativeQueryAll.call(this, selector);
        if (!popouts.size) return here;
        const docs = liveDocs();
        if (!docs.length) return here;
        // An array, not a NodeList, once a pop-out is open: forEach, length,
        // indexing, for..of and Array.from all behave the same.
        const all = Array.from(here);
        for (const doc of docs) all.push(...nativeQueryAll.call(doc, selector));
        all.item = (i) => all[i] ?? null;
        return all;
    };

    // document.activeElement answers for whichever window has the focus.
    // Every typing guard on the page (the TX key, the letter shortcuts) asks
    // it whether the operator is in a text box; once the bridge carries key
    // events to a pop-out, a guard reading only this document would let the
    // TX key through while someone types in a popped-out text field.
    const nativeActive = Object.getOwnPropertyDescriptor(Document.prototype, 'activeElement').get;
    let lastFocusDoc = document;
    nativeAdd.call(document, 'focusin', () => { lastFocusDoc = document; }, true);
    Object.defineProperty(document, 'activeElement', {
        configurable: true,
        get() {
            const here = nativeActive.call(document);
            if (!popouts.size || document.hasFocus()) return here;
            const docs = liveDocs();
            for (const doc of docs) if (doc.hasFocus()) return nativeActive.call(doc);
            // No window reports focus (a minimised or headless one): the last
            // that took it.
            return docs.includes(lastFocusDoc) ? nativeActive.call(lastFocusDoc) : here;
        },
    });

    window.ywcFlexPopouts = {
        /** Treat this pop-out window as part of the page. Idempotent. */
        add(win) {
            if (!win || win === window || popouts.has(win)) return;
            popouts.add(win);
            nativeAdd.call(win.document, 'focusin', () => { lastFocusDoc = win.document; }, true);
            const t = targetsIn(win);
            for (const kind of ['document', 'window']) {
                for (const r of records[kind]) nativeAdd.call(t[kind], r.type, r.listener, r.options);
            }
        },
        /** Stop treating it as part of the page (it is closing). */
        remove(win) {
            if (!popouts.delete(win) || win.closed) return;
            const t = targetsIn(win);
            for (const kind of ['document', 'window']) {
                for (const r of records[kind]) {
                    try { nativeRemove.call(t[kind], r.type, r.listener, r.options); } catch { /* ignore */ }
                }
            }
        },
        has(win) { return popouts.has(win); },
        get size() { return popouts.size; },
    };
})();
