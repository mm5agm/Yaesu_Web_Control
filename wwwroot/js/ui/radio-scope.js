// radio-scope.js — controls for the RADIO's own spectrum scope, over CAT.
//
// Not to be confused with spectrum-panel.js, which draws YWC's own SDR
// spectrum from an RSP1 on the rear-panel IF output. This module draws nothing.
// It sends SS commands and reflects back whatever the radio says it is now
// showing on its front-panel screen.
//
// See docs/design/scope-control-via-cat.md. Frame format is hardware-confirmed
// on an FTdx101MP; the FTdx10 and FT-710 tables come from their CAT manuals.
//
// Design notes worth keeping:
//
//  * Every write is followed by a server-side read-back, and the returned state
//    is what repaints the buttons. The radio wins, always. That matters here
//    more than in most of the UI because the operator may be standing at the
//    rig changing the same settings by hand, and because a radio that quietly
//    refuses a value should look refused rather than accepted.
//
//  * The panel reads state lazily, on first expand, not at page load. Six SS
//    reads on a port shared with the ~10 Hz meter poll is not a cost worth
//    paying for a panel most users will never open.
//
//  * SPAN IS STORED PER MODE on the radio, which is why the highlighted span
//    button moves on its own when you change display mode. Measured on an
//    FTdx101MP over ten consecutive mode changes: W/F CURSOR (L) held 20 kHz
//    while 3DSS FIX held 1 MHz, and each mode brought its own span back every
//    time. This is the radio's behaviour, not a bug here and not something to
//    "correct" by re-sending the previous span — doing that would overwrite a
//    setting the operator deliberately chose on the front panel. Repainting
//    everything from the read-back after every write is what keeps the UI
//    honest about it.

const MODE_PLACEMENTS = 3;   // Center, Cursor, Fix

// Mirrors ScopeCommands.ModeValue in Services/CatCommands.cs. The two must
// agree; if you change the composition rule, change it in both places.
function composeMode(is3dss, placement, size) {
    const p = Math.max(0, Math.min(2, placement | 0));
    if (is3dss) return String(p);
    const index = 3 + p * MODE_PLACEMENTS + Math.max(0, Math.min(2, size | 0));
    return index < 10 ? String(index) : String.fromCharCode(65 + index - 10);
}

export class RadioScopeControl {
    constructor() {
        this.card = document.getElementById('radioScopeCard');
        if (!this.card) return;              // model has no CAT scope control

        this.body     = document.getElementById('radioScopeBody');
        this.toggle   = document.getElementById('radioScopeToggle');
        this.chevron  = document.getElementById('radioScopeChevron');
        this.status   = document.getElementById('radioScopeStatus');
        this.sizeGroup = document.getElementById('scopeSizeGroup');
        this.levelSlider = document.getElementById('scopeLevelSlider');
        this.levelLabel  = document.getElementById('scopeLevelLabel');

        this.band    = 'main';
        this.state   = null;
        this.loaded  = false;
        this.busy    = false;

        this._wireExpand();
        this._wireControls();

        // Restore the operator's expand/collapse choice, and load state if they
        // had it open. Same localStorage convention as the spectrum panels.
        if (localStorage.getItem('ywc.radioScopeOpen') === '1') this._expand();
    }

    // ── expand / collapse ────────────────────────────────────────────────────

    _wireExpand() {
        const flip = () => (this.body.style.display === 'none' ? this._expand() : this._collapse());
        this.toggle.addEventListener('click', flip);
        this.toggle.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); flip(); }
        });
    }

    _expand() {
        this.body.style.display = '';
        this.toggle.setAttribute('aria-expanded', 'true');
        this.chevron?.classList.replace('bi-chevron-down', 'bi-chevron-up');
        localStorage.setItem('ywc.radioScopeOpen', '1');
        if (!this.loaded) this.refresh();
    }

    _collapse() {
        this.body.style.display = 'none';
        this.toggle.setAttribute('aria-expanded', 'false');
        this.chevron?.classList.replace('bi-chevron-up', 'bi-chevron-down');
        localStorage.setItem('ywc.radioScopeOpen', '0');
    }

    // ── control wiring ───────────────────────────────────────────────────────

    _wireControls() {
        this.card.querySelectorAll('.scope-band-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                this.band = btn.dataset.band;
                this.card.querySelectorAll('.scope-band-btn')
                    .forEach(b => b.classList.toggle('active', b === btn));
                // MAIN and SUB hold genuinely independent settings on the '101,
                // so switching scope means re-reading, not re-labelling.
                this.loaded = false;
                this.refresh();
            });
        });

        this.card.querySelectorAll('.scope-span-btn').forEach(btn => {
            btn.addEventListener('click', () => this._send('span', btn.dataset.value));
        });

        // The three mode axes all resolve to one SS write, composed from the
        // current state plus whichever axis the operator just moved.
        this.card.querySelectorAll('.scope-type-btn').forEach(btn => {
            btn.addEventListener('click', () => this._sendMode({ is3dss: btn.dataset.value === '3dss' }));
        });
        this.card.querySelectorAll('.scope-place-btn').forEach(btn => {
            btn.addEventListener('click', () => this._sendMode({ placement: parseInt(btn.dataset.value, 10) }));
        });
        this.card.querySelectorAll('.scope-size-btn').forEach(btn => {
            btn.addEventListener('click', () => this._sendMode({ size: parseInt(btn.dataset.value, 10) }));
        });

        this.card.querySelectorAll('.scope-toggle-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const setting = btn.dataset.setting;
                const now = this.state?.[setting] === '1';
                this._send(setting, now ? '0' : '1');
            });
        });

        if (this.levelSlider) {
            // Fire on release, not on every pixel of drag — this is a serial
            // port, not a local slider.
            this.levelSlider.addEventListener('input', () => {
                this.levelLabel.textContent = this._formatLevel(this.levelSlider.value);
            });
            this.levelSlider.addEventListener('change', () => {
                this._send('level', this.levelSlider.value);
            });
        }
    }

    _sendMode(change) {
        const s = this.state || { is3dss: false, placement: 0, size: 0 };
        const is3dss    = change.is3dss    !== undefined ? change.is3dss    : !!s.is3dss;
        const placement = change.placement !== undefined ? change.placement : (s.placement | 0);
        const size      = change.size      !== undefined ? change.size      : (s.size | 0);
        this._send('mode', composeMode(is3dss, placement, size));
    }

    // ── transport ────────────────────────────────────────────────────────────

    async refresh() {
        await this._call(`/api/scope/${this.band}`, null);
    }

    async _send(setting, value) {
        await this._call(`/api/scope/${this.band}/${setting}`, { value: String(value) });
    }

    async _call(url, body) {
        if (this.busy) return;               // the port is serial; so are we
        this.busy = true;
        this._setStatus('…', 'bg-secondary');
        try {
            const res = await fetch(url, body === null ? {} : {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            const data = await res.json().catch(() => null);
            if (!res.ok) {
                // Show the server's own message: the useful failures here are
                // things like "the FT-710 has no scope HOLD", which are worth
                // reading rather than flattening into "error".
                this._setStatus(data?.error || `Error ${res.status}`, 'bg-danger');
                return;
            }
            this.loaded = true;
            this._apply(data);
        } catch (err) {
            this._setStatus('No response', 'bg-danger');
            console.error('[radio-scope]', err);
        } finally {
            this.busy = false;
        }
    }

    // ── repaint from the radio's answer ──────────────────────────────────────

    _apply(state) {
        this.state = state;

        this._mark('.scope-span-btn', b => b.dataset.value === state.span);
        this._mark('.scope-type-btn', b => (b.dataset.value === '3dss') === !!state.is3dss);
        this._mark('.scope-place-btn', b => parseInt(b.dataset.value, 10) === (state.placement | 0));
        this._mark('.scope-size-btn', b => parseInt(b.dataset.value, 10) === (state.size | 0));

        this.card.querySelectorAll('.scope-toggle-btn').forEach(btn => {
            btn.classList.toggle('active', state[btn.dataset.setting] === '1');
        });

        // 3DSS has no size variants — the buttons stay in place but go inert,
        // so the row does not reflow when the operator switches display type.
        const sizeDisabled = !!state.is3dss;
        this.sizeGroup?.querySelectorAll('.scope-size-btn')
            .forEach(b => { b.disabled = sizeDisabled; });
        this.sizeGroup?.classList.toggle('opacity-50', sizeDisabled);

        if (this.levelSlider && state.level) {
            const db = parseFloat(state.level);
            if (!Number.isNaN(db)) {
                this.levelSlider.value = db;
                this.levelLabel.textContent = this._formatLevel(db);
            }
        }

        this._setStatus(this._summary(state), 'bg-success');
    }

    _mark(selector, isActive) {
        this.card.querySelectorAll(selector)
            .forEach(b => b.classList.toggle('active', isActive(b)));
    }

    _summary(state) {
        const spans = ['1k', '2k', '5k', '10k', '20k', '50k', '100k', '200k', '500k', '1M'];
        const span  = spans[parseInt(state.span, 10)] || '?';
        const type  = state.is3dss ? '3DSS' : 'W/F';
        const place = ['Center', 'Cursor', 'Fix'][state.placement | 0] || '';
        const hold  = state.hold === '1' ? ' · HOLD' : '';
        return `${type} ${place} · ${span}${hold}`;
    }

    _formatLevel(db) {
        const n = parseFloat(db);
        if (Number.isNaN(n)) return '—';
        return `${n >= 0 ? '+' : ''}${n.toFixed(1)} dB`;
    }
}
