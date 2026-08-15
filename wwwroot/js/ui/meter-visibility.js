// meter-visibility.js — de-emphasise the transmit-only gauges while receiving.
//
// EXPERIMENT (feat/meter-visibility-experiment). Two candidate behaviours are
// implemented side by side so they can be compared against a real rig mid-QSO;
// one of them is meant to be deleted before this ever ships.
//
//   off   current behaviour — every gauge drawn at full strength always
//   dim    transmit-only gauges greyed in place, geometry unchanged
//   hide   transmit-only gauges removed from layout, row reflows
//
// Which gauges count as transmit-only is decided in the markup, not here: a
// cell carries data-meter-scope="tx" if it means nothing while receiving.
// S-meters are obviously excluded. So are Temp and VDD, which are NOT
// transmit-only despite sitting among the transmit gauges — MeterPollingService
// polls temperature outside the isTransmitting branch, and a hot PA or a sagging
// supply is worth seeing on receive, which is exactly when you would look.
//
// SWR/Power/ALC/Compression are already pinned to zero during receive by
// MeterPollingService, so nothing here is hiding live data — it is choosing
// whether to draw a gauge that is deliberately reading nothing.

const MODES = ['off', 'dim', 'hide'];
const KEY = 'ywc.meterIdleMode';

const LABEL = {
    off:  'Meters: all',
    dim:  'Meters: dim',
    hide: 'Meters: hide'
};

const TITLE = {
    off:  'Transmit-only gauges always shown (current behaviour)',
    dim:  'Transmit-only gauges greyed while receiving — nothing moves',
    hide: 'Transmit-only gauges hidden while receiving — the row reflows'
};

export class MeterVisibility {
    /**
     * @param {string} rowId    id of the gauges row container
     * @param {string} btnId    id of the toolbar cycle button (optional)
     */
    constructor(rowId, btnId) {
        this._row = document.getElementById(rowId);
        this._btn = btnId ? document.getElementById(btnId) : null;
        this._transmitting = false;

        this._mode = 'off';
        try {
            const stored = localStorage.getItem(KEY);
            if (MODES.includes(stored)) this._mode = stored;
        } catch { /* private browsing — fall back to the default */ }

        if (this._btn) {
            this._btn.addEventListener('click', () => {
                this.setMode(MODES[(MODES.indexOf(this._mode) + 1) % MODES.length]);
            });
        }
        this._apply();
    }

    /** Called from the IsTransmitting handler in site.js. */
    setTransmitting(transmitting) {
        const next = !!transmitting;
        if (next === this._transmitting) return;
        this._transmitting = next;
        this._apply();
    }

    setMode(mode) {
        if (!MODES.includes(mode)) return;
        this._mode = mode;
        try { localStorage.setItem(KEY, mode); } catch { /* ignore */ }
        this._apply();
    }

    get mode() { return this._mode; }

    _apply() {
        if (!this._row) return;

        // Receive-idle styling only bites while receiving; on transmit every
        // gauge is live and the row always looks exactly as it does today.
        const idle = !this._transmitting && this._mode !== 'off';
        this._row.classList.toggle('meters-idle-dim',  idle && this._mode === 'dim');
        this._row.classList.toggle('meters-idle-hide', idle && this._mode === 'hide');

        if (this._btn) {
            this._btn.textContent = LABEL[this._mode];
            this._btn.title = TITLE[this._mode];
            this._btn.classList.toggle('active', this._mode !== 'off');
        }
    }
}
