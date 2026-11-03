// Keyboard shortcut shell - the radio-agnostic half of a page's shortcuts.
//
// An app supplies a help table (rows grouped by `group`) and a handleKey(e)
// that does the radio work. This module owns everything that is the same in
// both: the "typing into a field" guard, physical-key matching, the help
// dialog built from that table (so the table cannot drift from what the keys
// do), the live region that announces each action, and the capture-phase
// listener that keeps help-dialog keys from reaching the radio.
//
// Nothing here knows a radio, a VFO or a mode.

/** @typedef {{ keys: string, action: string, group: string, when?: string }} ShortcutHelpRow */

/**
 * Physical key match. Alt/Option on macOS changes e.key (Option+Z is "Ω")
 * while e.code stays "KeyZ", so letter chords with Alt must match by code.
 * @param {KeyboardEvent} e
 * @param {string} code  e.g. 'KeyZ'
 */
export function isCode(e, code) {
    return e.code === code;
}

/**
 * j/i/arrow tune ladder: 100 Hz, Shift 1 kHz, Alt 10 kHz. Shift+Alt returns
 * 0 because that chord is the DX-spot hop, not a step.
 * @param {{ shiftKey?: boolean, altKey?: boolean }} mods
 */
export function resolveTuneStepHz(mods = {}) {
    if (mods.shiftKey && mods.altKey) return 0;
    if (mods.altKey) return 10_000;
    if (mods.shiftKey) return 1_000;
    return 100;
}

/** [ / ] fine tune: 1 Hz, or 50 Hz with Shift. */
export function resolveBracketTuneStepHz(mods = {}) {
    return mods.shiftKey ? 50 : 1;
}

/** Ctrl/Cmd chords belong to the browser (find, zoom, tabs) - never claim one. */
export function isBrowserChord(e) {
    return !!(e.ctrlKey || e.metaKey);
}

/**
 * True while the operator is typing somewhere a letter is text, not a
 * command: text inputs, textareas, selects, contenteditable, and any open
 * dialog the app names as a text-entry surface (a frequency keypad, say,
 * where focus sits on buttons but digits are being entered).
 * @param {string[]} textEntryDialogIds
 */
export function isTypingIntoEditable(textEntryDialogIds = []) {
    const active = document.activeElement;
    if (active) {
        if (active.isContentEditable) return true;
        if (active.tagName === 'TEXTAREA' || active.tagName === 'SELECT') return true;
        if (active.tagName === 'INPUT') {
            const type = (active.getAttribute('type') || 'text').toLowerCase();
            return !['range', 'checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'color', 'hidden'].includes(type);
        }
    }
    return textEntryDialogIds.some(id => document.getElementById(id)?.open);
}

/** Help chord: ? (Shift+Slash on UK and US layouts, or the glyph) or a bare h. */
export function isHelpChord(e) {
    if (e.altKey || e.ctrlKey || e.metaKey) return false;
    if (e.key === '?' || (isCode(e, 'Slash') && e.shiftKey)) return true;
    return isCode(e, 'KeyH') && !e.shiftKey;
}

export function escapeHtml(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => (
        { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
    ));
}

/**
 * A polite live region for spoken confirmation of each shortcut. Created on
 * first use if the page has none with that id.
 * @param {string} id
 * @returns {(message: string) => void}
 */
export function createAnnouncer(id = 'kbShortcutAnnounce') {
    return (message) => {
        if (!message) return;
        let el = document.getElementById(id);
        if (!el) {
            el = document.createElement('span');
            el.id = id;
            el.className = 'visually-hidden';
            el.setAttribute('role', 'status');
            el.setAttribute('aria-live', 'polite');
            document.body.appendChild(el);
        }
        // Clear then set on the next frame, so the same message twice in a
        // row is still read out.
        el.textContent = '';
        requestAnimationFrame(() => { el.textContent = message; });
    };
}

const CELL = 'padding:4px 8px;border-bottom:1px solid #333;';
const HEAD = 'text-align:left;padding:4px 8px;border-bottom:1px solid #444;';
const KEYS = 'font-family:ui-monospace,Consolas,monospace;white-space:nowrap;';

function groupTableHtml(name, rows) {
    let html = `<h3 style="font-size:0.95rem;margin:1rem 0 0.4rem;color:#9cf;">${escapeHtml(name)}</h3>`
        + '<table style="width:100%;border-collapse:collapse;font-size:0.85rem;">'
        + `<thead><tr><th scope="col" style="${HEAD}width:40%;">Key</th><th scope="col" style="${HEAD}">Action</th></tr></thead><tbody>`;
    for (const row of rows) {
        const when = row.when ? ` <span style="color:#888;font-size:0.8em;">(${escapeHtml(row.when)})</span>` : '';
        html += `<tr><td style="${CELL}${KEYS}">${escapeHtml(row.keys)}</td>`
            + `<td style="${CELL}">${escapeHtml(row.action)}${when}</td></tr>`;
    }
    return html + '</tbody></table>';
}

/**
 * The "?" dialog, built from the app's table.
 * @param {{
 *   rows: ShortcutHelpRow[] | (() => ShortcutHelpRow[]),
 *   intro?: string,                        plain text shown above the tables
 *   afterGroup?: Record<string, () => string>,   extra HTML after a named group
 *   onOpen?: (dialog: HTMLDialogElement) => void,
 * }} opts
 */
export function createShortcutHelp(opts) {
    let dialog = null;
    let previousFocus = null;

    const body = () => dialog?.querySelector('.kb-help-body') || null;

    function build() {
        if (dialog) return dialog;
        const dlg = document.createElement('dialog');
        dlg.id = 'keyboardShortcutsDialog';
        dlg.setAttribute('aria-labelledby', 'keyboardShortcutsTitle');
        dlg.style.cssText = [
            'border:1px solid #555', 'border-radius:8px', 'background:#1e1e2e', 'color:#eee',
            'padding:0', 'max-width:min(780px, 95vw)', 'width:780px', 'max-height:90vh',
            'z-index:10050', 'box-shadow:0 12px 40px rgba(0,0,0,0.65)'
        ].join(';');
        dlg.innerHTML =
            '<div style="display:flex;align-items:center;justify-content:space-between;padding:10px 14px;border-bottom:1px solid #444;position:sticky;top:0;background:#1e1e2e;z-index:1;">'
            + '<strong id="keyboardShortcutsTitle">Keyboard shortcuts</strong>'
            + '<button type="button" class="btn btn-sm btn-outline-light" id="keyboardShortcutsClose" aria-label="Close keyboard shortcuts">&#10005;</button>'
            + '</div>'
            + '<div class="kb-help-body" tabindex="-1" style="padding:4px 14px 16px;overflow:auto;max-height:calc(90vh - 52px);outline:none;"></div>';
        document.body.appendChild(dlg);
        dlg.querySelector('#keyboardShortcutsClose').addEventListener('click', () => close());
        dlg.addEventListener('cancel', (e) => {
            // Native Esc: take our own close path so focus goes back.
            e.preventDefault();
            close();
        });
        dialog = dlg;
        return dlg;
    }

    // Rebuilt on every open, so a row whose `when` depends on the page (a
    // configured TX key, say) is current.
    function render() {
        const rows = typeof opts.rows === 'function' ? opts.rows() : opts.rows;
        const groups = [];
        for (const row of rows) {
            let g = groups.find(x => x.name === row.group);
            if (!g) { g = { name: row.group, rows: [] }; groups.push(g); }
            g.rows.push(row);
        }
        let html = '<p style="font-size:0.8rem;color:#aaa;margin:0.6rem 0 0;">'
            + escapeHtml(opts.intro ?? 'Shortcuts are ignored while typing in a text field. While this dialog is open, the arrow keys scroll it. Browser chords such as Ctrl/Cmd+F are never captured.')
            + '</p>';
        for (const g of groups) {
            html += groupTableHtml(g.name, g.rows);
            const extra = opts.afterGroup?.[g.name];
            if (extra) html += extra();
        }
        body().innerHTML = html;
    }

    function open() {
        build();
        render();
        opts.onOpen?.(dialog);
        previousFocus = document.activeElement;
        if (!dialog.open) dialog.showModal();
        (body() || dialog.querySelector('#keyboardShortcutsClose'))?.focus?.();
    }

    function close() {
        if (!dialog?.open) return;
        dialog.close();
        const prev = previousFocus;
        previousFocus = null;
        try { prev?.focus?.({ preventScroll: true }); } catch { /* element gone */ }
    }

    const isOpen = () => !!dialog?.open;

    function scroll(key) {
        const b = body();
        if (!b) return;
        const page = Math.max(80, Math.floor(b.clientHeight * 0.85));
        if (key === 'ArrowDown') b.scrollBy({ top: 48 });
        else if (key === 'ArrowUp') b.scrollBy({ top: -48 });
        else if (key === 'PageDown') b.scrollBy({ top: page });
        else if (key === 'PageUp') b.scrollBy({ top: -page });
        else if (key === 'Home') b.scrollTo({ top: 0 });
        else if (key === 'End') b.scrollTo({ top: b.scrollHeight });
    }

    return { open, close, isOpen, scroll, get dialog() { return dialog; } };
}

const SCROLL_KEYS = ['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End'];
const MODIFIER_KEYS = ['Shift', 'Alt', 'Control', 'Meta', 'Tab'];

/**
 * Wire the document. One capture-phase listener, so while help is open its
 * keys are consumed before any other handler on the page can tune or
 * shift something the operator cannot see.
 * @param {{
 *   help: ReturnType<typeof createShortcutHelp>,
 *   handleKey: (e: KeyboardEvent) => boolean,   true if it acted
 *   textEntryDialogIds?: string[],
 *   whileHelpOpen?: (e: KeyboardEvent) => boolean,   keys still live under help
 * }} opts
 */
export function installShortcuts(opts) {
    const { help, handleKey } = opts;
    const typing = () => isTypingIntoEditable(opts.textEntryDialogIds || []);

    document.addEventListener('keydown', (e) => {
        if (e.defaultPrevented || isBrowserChord(e)) return;

        if (help.isOpen()) {
            if (e.key === 'Escape' || isHelpChord(e)) {
                e.preventDefault();
                e.stopPropagation();
                help.close();
                return;
            }
            if (SCROLL_KEYS.includes(e.key)) {
                e.preventDefault();
                e.stopPropagation();
                help.scroll(e.key);
                return;
            }
            if (opts.whileHelpOpen?.(e)) {
                e.preventDefault();
                e.stopPropagation();
                return;
            }
            // Swallow everything else so reading help cannot change the radio.
            if (!MODIFIER_KEYS.includes(e.key)) {
                e.preventDefault();
                e.stopPropagation();
            }
            return;
        }

        if (e.key === 'Escape' || typing()) return;

        if (isHelpChord(e)) {
            e.preventDefault();
            help.open();
            return;
        }

        if (handleKey(e)) e.preventDefault();
    }, true);

    document.querySelectorAll('[data-open-shortcuts]').forEach(btn => {
        btn.addEventListener('click', () => help.open());
    });
}
