// radio-scope.js — controls for the RADIO's own spectrum scope, over CAT.
//
// Not to be confused with spectrum-panel.js, which draws YWC's own SDR
// spectrum from an RSP1 on the rear-panel IF output. This module draws nothing.
// It sends SS commands and reflects back whatever the radio says it is now
// showing on its front-panel screen.
//
// See docs/design/scope-control-via-cat.md. Frame format is hardware-confirmed
// on an FTdx101MP. FTdx10 is enabled from the same SS table; FT-710 stays
// gated until a write probe.
//
// Design notes worth keeping:
//
//  * Every write is followed by a server-side read-back, and the returned state
//    is what repaints the buttons. The radio wins, always. That matters here
//    more than in most of the UI because the operator may be standing at the
//    rig changing the same settings by hand, and because a radio that quietly
//    refuses a value should look refused rather than accepted.
//
//  * The standalone card reads state lazily, on first expand, not at page load.
//    The Radio Display Controls dialog does the same when opened, so the CAT
//    port is idle until the operator asks for the buttons.
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

// Inverse of composeMode, mirroring ScopeCommands.ParseMode in
// Services/CatCommands.cs. Needed because the radio announces its front-panel
// mode changes as the composed character, so unpicking it happens in the
// browser rather than costing a server round-trip to re-read what we were just
// told.
function parseMode(ch) {
    const c = String(ch || '0').toUpperCase();
    let index = 0;
    if (c >= '0' && c <= '9')      index = c.charCodeAt(0) - 48;
    else if (c >= 'A' && c <= 'B') index = c.charCodeAt(0) - 65 + 10;
    if (index < 3) return { is3dss: true, placement: index, size: 0 };
    const offset = index - 3;
    return { is3dss: false, placement: Math.floor(offset / 3), size: offset % 3 };
}

function notifyRadioScopeControls(fn) {
    const list = window.radioScopeControls;
    if (Array.isArray(list) && list.length) {
        list.forEach(fn);
        return;
    }
    if (window.radioScopeControl) fn(window.radioScopeControl);
}
window.notifyRadioScopeControls = notifyRadioScopeControls;

export class RadioScopeControl {
    constructor(root) {
        this.card = root instanceof Element
            ? root
            : document.getElementById(typeof root === 'string' ? root : 'radioScopeCard');
        if (!this.card) return;              // model has no CAT scope control

        this.body        = this.card.querySelector('.scope-body') || this.card;
        this.toggle      = this.card.querySelector('.scope-toggle');
        this.chevron     = this.card.querySelector('.scope-chevron');
        this.status      = this.card.querySelector('.scope-status');
        this.sizeGroup   = this.card.querySelector('.scope-size-group');
        this.nbColorGroup = this.card.querySelector('.scope-nbcolor-group');
        this.levelSlider = this.card.querySelector('.scope-level-slider');
        this.levelLabel  = this.card.querySelector('.scope-level-label');
        this.eager       = this.card.dataset.eagerLoad === '1';

        // Server-rendered from RadioStateService.ActiveVfo so the panel is aimed
        // at the band the radio is actually operating on from the very first
        // paint. setActiveBand() keeps it there as the operator moves bands.
        this.band    = this.card.dataset.initialBand === 'sub' ? 'sub' : 'main';
        this.state   = null;
        this.loaded  = false;
        this.busy    = false;

        if (this.toggle) this._wireExpand();
        this._wireMultiGroup();
        this._wireControls();
        this._initTooltips();

        if (this.eager) this.refresh();
        else if (this.toggle && localStorage.getItem('ywc.radioScopeOpen') === '1') this._expand();
        else if (this.card?.tagName === 'DIALOG' && this.card.open) this.refresh();
    }

    // Same Bootstrap tooltip pattern as Mic & Gain on the Remote Audio bar.
    // Native title= is not used: loadLabels() overwrites title from labels.json,
    // and a <dialog> top-layer does not show browser tooltips reliably.
    // Container is this root so tips stay above the Radio Display dialog.
    _initTooltips() {
        const start = () => {
            if (typeof bootstrap === 'undefined' || !bootstrap.Tooltip) return false;
            const container = this.card.tagName === 'DIALOG' ? this.card : document.body;
            this.card.querySelectorAll('.radio-scope-label[data-bs-toggle="tooltip"], .radio-scope-sublabel[data-bs-toggle="tooltip"], .radio-scope-multi-toggle[data-bs-toggle="tooltip"]').forEach(el => {
                bootstrap.Tooltip.getInstance(el)?.dispose();
                bootstrap.Tooltip.getOrCreateInstance(el, {
                    delay: { show: 200, hide: 50 },
                    trigger: 'hover focus',
                    placement: el.getAttribute('data-bs-placement') || 'top',
                    container,
                    fallbackPlacements: ['bottom', 'right', 'left']
                });
            });
            return true;
        };
        if (!start()) window.addEventListener('load', () => start(), { once: true });
    }

    _isOpen() {
        if (this.card?.tagName === 'DIALOG') return !!this.card.open;
        if (this.eager) return true;
        return this.body && this.body.style.display !== 'none';
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
        this.toggle?.setAttribute('aria-expanded', 'true');
        this.chevron?.classList.replace('bi-chevron-down', 'bi-chevron-up');
        localStorage.setItem('ywc.radioScopeOpen', '1');
        if (!this.loaded) this.refresh();
    }

    _collapse() {
        this.body.style.display = 'none';
        this.toggle?.setAttribute('aria-expanded', 'false');
        this.chevron?.classList.replace('bi-chevron-up', 'bi-chevron-down');
        localStorage.setItem('ywc.radioScopeOpen', '0');
    }

    // MULTI / AF-FFT / OSC sit behind a <details> because they only apply
    // after MULTI is showing on the TFT. Remember the last open/closed choice.
    _wireMultiGroup() {
        this.card.querySelectorAll('details.radio-scope-multi').forEach(el => {
            el.open = localStorage.getItem('ywc.radioScopeMultiOpen') === '1';
            el.addEventListener('toggle', () => {
                localStorage.setItem('ywc.radioScopeMultiOpen', el.open ? '1' : '0');
            });
        });
    }

    // ── control wiring ───────────────────────────────────────────────────────

    _wireControls() {
        this.card.querySelectorAll('.scope-band-btn').forEach(btn => {
            btn.addEventListener('click', () => this._requestBand(btn.dataset.band));
        });

        this.card.querySelectorAll('.scope-span-btn').forEach(btn => {
            btn.addEventListener('click', () => this._send('span', btn.dataset.value));
        });

        this.card.querySelectorAll('.scope-speed-btn').forEach(btn => {
            btn.addEventListener('click', () => this._send('speed', btn.dataset.value));
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

        this.card.querySelectorAll('.scope-peak-btn').forEach(btn => {
            btn.addEventListener('click', () => this._send('peak', btn.dataset.value));
        });

        this.card.querySelectorAll('.scope-color-btn').forEach(btn => {
            btn.addEventListener('click', () => this._sendColor({ color: btn.dataset.value }));
        });
        this.card.querySelectorAll('.scope-nbcolor-btn').forEach(btn => {
            btn.addEventListener('click', () => this._sendColor({ nbColor: btn.dataset.value }));
        });
        this.card.querySelectorAll('.scope-nbon-btn').forEach(btn => {
            btn.addEventListener('click', () => this._sendColor({ nbOn: btn.dataset.value }));
        });

        this.card.querySelectorAll('.scope-fftatt-btn').forEach(btn => {
            btn.addEventListener('click', () => this._sendAfFft({ fftAtt: btn.dataset.value }));
        });
        this.card.querySelectorAll('.scope-oscatt-btn').forEach(btn => {
            btn.addEventListener('click', () => this._sendAfFft({ oscAtt: btn.dataset.value }));
        });
        this.card.querySelectorAll('.scope-osctime-btn').forEach(btn => {
            btn.addEventListener('click', () => this._sendAfFft({ oscTime: btn.dataset.value }));
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
                if (this.levelLabel)
                    this.levelLabel.textContent = this._formatLevel(this.levelSlider.value);
            });
            this.levelSlider.addEventListener('change', () => {
                this._send('level', this.levelSlider.value);
            });
        }
    }

    // ── band selection ───────────────────────────────────────────────────────

    // The MAIN/SUB buttons move THE RADIO, not just these controls. The radio
    // displays the scope of whichever band it is operating on, so there is no
    // such thing as "look at SUB's scope while operating MAIN" — asking for the
    // SUB scope and asking for the SUB band are one request, and splitting them
    // is what made the first version of this row so confusing.
    //
    // So this sends VS via the same endpoint as clicking a VFO panel header,
    // then stops. The ActiveVfo broadcast that comes back re-aims the controls
    // through setActiveBand(), which keeps one path for "the band changed"
    // whether the change came from here, from the VFO header, or from the
    // operator's hand on the front panel.
    async _requestBand(band) {
        if (band !== 'main' && band !== 'sub') return;
        if (band === this.band) return;
        if (this.busy) return;
        this.busy = true;
        try {
            this._setStatus('…', 'bg-secondary');
            const res = await fetch(`/api/cat/active-vfo/${band === 'sub' ? 'B' : 'A'}`,
                                    { method: 'POST' });
            if (!res.ok) {
                const data = await res.json().catch(() => null);
                this._setStatus(data?.error || `Error ${res.status}`, 'bg-danger');
                return;
            }
        } catch (err) {
            this._setStatus('No response', 'bg-danger');
            console.error('[radio-scope]', err);
            return;
        } finally {
            this.busy = false;
        }

        // Reconcile once we are out of the way. Two things can have gone wrong,
        // and both were observed on the bench rather than imagined:
        //
        //  * The ActiveVfo broadcast beat the POST's own response back, so
        //    _selectBand ran while this method still held `busy` and its
        //    refresh() was swallowed by the guard in _call. Symptom: the right
        //    band highlighted, the badge stuck on the ellipsis. `loaded` is
        //    false in that case, which is exactly what we test for.
        //  * No broadcast arrived at all, and the panel is still labelled MAIN
        //    while the radio has moved to SUB — the precise failure this whole
        //    row exists to prevent.
        setTimeout(() => {
            if (this.band !== band) { this._selectBand(band); return; }
            if (!this.loaded && this._isOpen()) this.refresh();
        }, 400);
    }

    // Points the controls at a band and re-reads it. MAIN and SUB hold genuinely
    // independent settings on the '101, so switching scope means re-reading,
    // not re-labelling. Internal: reached from setActiveBand(), never straight
    // from a click — a click has to move the radio first.
    _selectBand(band) {
        if (band !== 'main' && band !== 'sub') return;
        this.band = band;
        this.card.querySelectorAll('.scope-band-btn')
            .forEach(b => b.classList.toggle('active', b.dataset.band === band));
        this.loaded = false;
        if (this._isOpen()) {
            this.refresh();
        } else {
            // Nothing to read while collapsed — the panel loads lazily on
            // expand, and this port is shared with the meter poll. But
            // the badge stays visible in the header when collapsed, so leaving
            // the previous band's summary sitting under the new band's name
            // would be an outright lie. Blank it; expanding reloads it.
            this.state = null;
            this._setStatus('—', 'bg-secondary');
        }
    }

    // Called from site.js when the radio's active band changes (the ActiveVfo
    // SignalR update, i.e. a VS command from any source — this panel, the VFO
    // headers, or the operator's hand on the front panel). The radio's scope
    // follows the operating band, so these controls follow it too; otherwise the
    // operator selects SUB at the rig and carries on adjusting MAIN's scope with
    // no indication that anything is wrong. That exact confusion is what this
    // panel's first bench test ran into.
    //
    // Every band change reaches _selectBand through here (bar the fallback timer
    // in _requestBand), which is what keeps the button highlight and the radio in
    // step no matter who moved the band.
    setActiveBand(activeVfo) {
        if (!this.card) return;                    // model has no CAT scope control
        const band = Number(activeVfo) === 1 ? 'sub' : 'main';
        // Single-receiver models render no band buttons; SS P1 is fixed at 0.
        if (!this.card.querySelector(`.scope-band-btn[data-band="${band}"]`)) return;
        if (band === this.band) return;
        this._selectBand(band);
    }

    // ── the operator changed something at the rig ────────────────────────────

    // Called from site.js for each unsolicited SS the radio sends when someone
    // works its front panel. Patches the one sub-command that changed and
    // repaints, rather than re-reading all six over a port shared with the
    // meter poll: the radio has just told us the value, so asking it
    // again would be both slower and no more truthful.
    //
    // { band: "main"|"sub", setting: "0".."8", field: 5 chars }
    applyRemote(msg) {
        if (!this.card || !msg) return;
        if (msg.band !== this.band) return;   // the band we are not showing
        if (!this.state) return;              // nothing loaded yet; expand reads it

        const v = String(msg.field ?? '');
        const s = { ...this.state };

        switch (String(msg.setting)) {
            case '5': s.span   = v[0]; break;
            case '2': s.marker = v[0]; break;
            case '1': s.peak   = v[0]; break;
            case '0': s.speed  = v[0]; break;
            case '8': s.hold   = v[0]; break;
            // LEVEL is the one sub-command using the whole five-character
            // field ("+05.0"), kept as the raw string so there is no float
            // round-trip to disagree with the server about.
            case '4': s.level  = v; break;
            case '6': {
                s.mode = v[0];
                const m = parseMode(v[0]);
                s.is3dss = m.is3dss; s.placement = m.placement; s.size = m.size;
                break;
            }
            case '7':
                s.fftAtt  = v[0];
                s.oscAtt  = v[1];
                s.oscTime = v[2];
                break;
            case '3':
                s.color   = (v[0] || '0').toUpperCase();
                s.nbColor = v[1];
                s.nbOn    = v[2];
                break;
            default: return;
        }

        this._apply(s);
    }

    _sendMode(change) {
        const s = this.state || { is3dss: false, placement: 0, size: 0 };
        const is3dss    = change.is3dss    !== undefined ? change.is3dss    : !!s.is3dss;
        const placement = change.placement !== undefined ? change.placement : (s.placement | 0);
        const size      = change.size      !== undefined ? change.size      : (s.size | 0);
        this._send('mode', composeMode(is3dss, placement, size));
    }

    _sendAfFft(change) {
        if (change.fftAtt !== undefined) {
            this._send('affft', String(change.fftAtt));
        } else if (change.oscAtt !== undefined) {
            this._send('affft', `a${change.oscAtt}`);
        } else if (change.oscTime !== undefined) {
            this._send('affft', `t${change.oscTime}`);
        }
    }

    _sendColor(change) {
        if (change.color !== undefined) {
            this._send('color', String(change.color).toUpperCase());
        } else if (change.nbColor !== undefined) {
            this._send('color', `n${change.nbColor}`);
        } else if (change.nbOn !== undefined) {
            this._send('color', `o${change.nbOn}`);
        }
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
        try {
            // Everything after busy=true belongs inside the try. The first
            // version set the status here, one line above it, and _setStatus did
            // not exist: the TypeError escaped this method entirely, so the
            // finally never ran, busy stayed true forever, and every subsequent
            // click was swallowed by the guard above without a sound. One
            // missing method turned into a panel where nothing at all responded.
            this._setStatus('…', 'bg-secondary');
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
        this._mark('.scope-speed-btn', b => b.dataset.value === state.speed);
        this._mark('.scope-fftatt-btn', b => b.dataset.value === state.fftAtt);
        this._mark('.scope-oscatt-btn', b => b.dataset.value === state.oscAtt);
        this._mark('.scope-osctime-btn', b => b.dataset.value === state.oscTime);
        this._mark('.scope-peak-btn', b => b.dataset.value === state.peak);
        this._mark('.scope-color-btn', b => (b.dataset.value || '').toUpperCase() === (state.color || '').toUpperCase());
        this._mark('.scope-nbcolor-btn', b => b.dataset.value === state.nbColor);
        this._mark('.scope-nbon-btn', b => b.dataset.value === state.nbOn);

        this.card.querySelectorAll('.scope-toggle-btn').forEach(btn => {
            btn.classList.toggle('active', state[btn.dataset.setting] === '1');
        });

        // 3DSS has no size variants — the buttons stay in place but go inert,
        // so the row does not reflow when the operator switches display type.
        const sizeDisabled = !!state.is3dss;
        this.sizeGroup?.querySelectorAll('.scope-size-btn')
            .forEach(b => { b.disabled = sizeDisabled; });
        this.sizeGroup?.classList.toggle('opacity-50', sizeDisabled);

        const nbOff = state.nbOn === '0';
        this.nbColorGroup?.querySelectorAll('.scope-nbcolor-btn')
            .forEach(b => { b.disabled = nbOff; });
        this.nbColorGroup?.classList.toggle('opacity-50', nbOff);

        if (this.levelSlider && state.level) {
            const db = parseFloat(state.level);
            if (!Number.isNaN(db)) {
                this.levelSlider.value = db;
                this.levelLabel.textContent = this._formatLevel(db);
            }
        }

        this._setStatus(this._summary(state), 'bg-success');
    }

    /// Repaints the badge in the card header. That badge is the panel's only
    /// feedback channel — the controls themselves look identical whether a write
    /// landed, was refused by the radio, or never left the browser — so it needs
    /// to say something after every single call.
    _setStatus(text, cls) {
        if (!this.status) return;
        this.status.textContent = text;
        this.status.className = `badge ${cls} small scope-status`;
    }

    _mark(selector, isActive) {
        this.card.querySelectorAll(selector)
            .forEach(b => b.classList.toggle('active', isActive(b)));
    }

    _summary(state) {
        const spans = ['1k', '2k', '5k', '10k', '20k', '50k', '100k', '200k', '500k', '1M'];
        const speeds = ['SLOW1', 'SLOW2', 'FAST1', 'FAST2', 'FAST3', 'STOP'];
        const span  = spans[parseInt(state.span, 10)] || '?';
        const type  = state.is3dss ? '3DSS' : 'W/F';
        const place = ['Center', 'Cursor', 'Fix'][state.placement | 0] || '';
        const speed = speeds[parseInt(state.speed, 10)] || '';
        const hold  = state.hold === '1' ? ' · HOLD' : '';
        const spd   = speed ? ` · ${speed}` : '';
        return `${type} ${place} · ${span}${spd}${hold}`;
    }

    _formatLevel(db) {
        const n = parseFloat(db);
        if (Number.isNaN(n)) return '—';
        return `${n >= 0 ? '+' : ''}${n.toFixed(1)} dB`;
    }
}
