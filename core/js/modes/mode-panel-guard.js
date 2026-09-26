// Closes a mode-specific panel once the radio has settled in a mode the panel
// has no use for - the RTTY tuner after a move to CW, the CW reader after a
// move to RTTY - so it does not sit on screen holding the audio tap.
//
// Radio-agnostic: the consumer says which modes each panel belongs to, since
// the mode names are the radio's own (Yaesu CW-U / RTTY-L, Icom CW-R / RTTY-R).
//
// A mode has to hold for settleMs before anything closes. Band changes,
// memory recall and start-up can all report another mode for a moment, and a
// panel that vanished on one of those would be worse than one left open.
// Nothing is ever opened: a panel the operator did not ask for is worse than
// one they have to close.

export const MODE_SETTLE_MS = 2000;

export class ModePanelGuard {
    /**
     * @param {object} opts
     * @param {string}   opts.name      spoken name, e.g. "RTTY tuner"
     * @param {(mode: string) => boolean} opts.belongs  true for modes the panel is used in
     * @param {() => boolean} opts.isOpen
     * @param {() => void}    opts.close
     * @param {(message: string) => void} [opts.announce]  screen-reader message
     * @param {number} [opts.settleMs]
     */
    constructor({ name, belongs, isOpen, close, announce, settleMs = MODE_SETTLE_MS }) {
        this._name     = name;
        this._belongs  = belongs;
        this._isOpen   = isOpen;
        this._close    = close;
        this._announce = announce ?? (() => {});
        this._settleMs = settleMs;
        this._timer    = null;
        this._mode     = null;
    }

    /** Feed every mode report for the receiver the panel listens to. */
    setMode(mode) {
        if (typeof mode !== 'string' || !mode) return;
        this._mode = mode;
        if (this._belongs(mode)) { this._cancel(); return; }
        if (this._timer) return;   // already counting from the first foreign mode
        this._timer = setTimeout(() => {
            this._timer = null;
            if (this._belongs(this._mode) || !this._isOpen()) return;
            this._closePanel(this._mode);
        }, this._settleMs);
    }

    _closePanel(mode) {
        // Closing a dialog that holds keyboard focus drops the focus on
        // <body>, and a screen-reader user is left at the top of the page with
        // no idea why. Put it back on whatever opened the panel, if that is
        // known, and say what happened either way.
        const active = document.activeElement;
        const hadFocus = !!(active && this._panelHas?.(active));
        this._close();
        if (hadFocus) this._returnFocus?.focus?.();
        this._announce(`${this._name} closed, mode is now ${mode}`);
    }

    /**
     * Optional: where focus goes if it was inside the panel when it closed.
     * @param {(el: Element) => boolean} contains  whether an element is in the panel
     * @param {HTMLElement|null} target
     */
    setFocusReturn(contains, target) {
        this._panelHas = contains;
        this._returnFocus = target;
    }

    _cancel() {
        if (this._timer) { clearTimeout(this._timer); this._timer = null; }
    }
}
