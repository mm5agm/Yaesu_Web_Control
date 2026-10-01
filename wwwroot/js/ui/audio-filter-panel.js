// Audio Filter panel: LCUT FREQ / LCUT SLOPE / HCUT FREQ / HCUT SLOPE for one
// VFO's current mode class, backed by /api/cat/audiofilter/{vfo}. The radio
// holds all the state; the panel is a thin read/write proxy, so the main
// page's dialog and the /AudioFilter pop-out window can both show it.
//
// YWC-local rather than core: the slider-to-code mapping below is the Yaesu
// EX menu's (100 Hz / 700 Hz base, 50 Hz steps, slope 0 = 6 dB/oct,
// 1 = 18 dB/oct).
//
// Element ids are audioFilter<Part><VFO>, from Pages/Shared/_AudioFilterPartial.

export class AudioFilterPanel {
    /**
     * @param {'A'|'B'} vfo
     * @param {object} [opts]
     * @param {HTMLElement} [opts.anchor] the main page's Audio Filter button;
     *        on first open, with no saved position, the dialog appears under it
     */
    constructor(vfo, { anchor = null } = {}) {
        this._vfo    = vfo === 'B' ? 'B' : 'A';
        this._anchor = anchor;
        this._dialogId = 'audioFilterDialog' + this._vfo;
        // The slider's last non-OFF Hz, so toggling OFF -> ON returns to the
        // same value rather than the slider's minimum.
        this._lastLcutHz = 300;
        this._lastHcutHz = 3000;
        this._busy = false;
    }

    get vfo() { return this._vfo; }

    get dialog() { return document.getElementById(this._dialogId); }

    _el(part) { return document.getElementById(`audioFilter${part}${this._vfo}`); }

    init() {
        const el = p => this._el(p);
        el('LcutFreqSlider').addEventListener('input',  e => el('LcutFreqLabel').textContent = e.target.value + ' Hz');
        el('LcutFreqSlider').addEventListener('change', e => this._write('lcutFreq', hzToFreqCode(parseInt(e.target.value), true)));
        el('LcutFreqOff').addEventListener('change', e => {
            this._write('lcutFreq', e.target.checked ? '00' : hzToFreqCode(this._lastLcutHz, true));
        });
        el('LcutSlopeBtn').addEventListener('click', () => {
            const cur = el('LcutSlopeBtn').dataset.slopeCode || '0';
            this._write('lcutSlope', cur === '0' ? '1' : '0');
        });
        el('HcutFreqSlider').addEventListener('input',  e => el('HcutFreqLabel').textContent = e.target.value + ' Hz');
        el('HcutFreqSlider').addEventListener('change', e => this._write('hcutFreq', hzToFreqCode(parseInt(e.target.value), false)));
        el('HcutFreqOff').addEventListener('change', e => {
            this._write('hcutFreq', e.target.checked ? '00' : hzToFreqCode(this._lastHcutHz, false));
        });
        el('HcutSlopeBtn').addEventListener('click', () => {
            const cur = el('HcutSlopeBtn').dataset.slopeCode || '0';
            this._write('hcutSlope', cur === '0' ? '1' : '0');
        });
        return this;
    }

    /** Show the dialog (main page) and read the radio. */
    async open() {
        const d = this.dialog;
        if (d && typeof d.show === 'function' && !d.open) {
            let saved = false;
            try { saved = !!localStorage.getItem('dlgPos_' + this._dialogId); } catch { /* ignore */ }
            if (!saved && this._anchor) {
                const r = this._anchor.getBoundingClientRect();
                d.style.left = Math.round(r.left) + 'px';
                d.style.top  = Math.round(r.bottom + 6) + 'px';
                d.style.transform = 'none';
            }
            d.show();
        }
        await this.refresh();
    }

    /** A VFO's mode changed: re-read, if it is this panel's and it is showing. */
    onModeChanged(changedVfo) {
        if (changedVfo !== this._vfo) return;
        if (!this.dialog?.open) return;
        this.refresh();
    }

    async refresh() {
        if (this._busy) return;
        this._busy = true;
        const el = p => this._el(p);
        try {
            const resp = await fetch(`/api/cat/audiofilter/${this._vfo.toLowerCase()}`);
            if (!resp.ok) {
                el('Status').textContent = 'Failed to read audio filter.';
                return;
            }
            const data = await resp.json();
            el('ModeLabel').textContent = data.modeClass ? `(${data.modeClass})` : '';

            const other = this._vfo === 'A' ? 'B' : 'A';
            el('Status').textContent = data.otherVfoShares
                ? `VFO ${other} is also in ${data.modeClass} — these settings affect both VFOs.`
                : '';

            const anySupported = (data.lcutFreq && data.lcutFreq.supported)
                              || (data.lcutSlope && data.lcutSlope.supported)
                              || (data.hcutFreq && data.hcutFreq.supported)
                              || (data.hcutSlope && data.hcutSlope.supported);
            el('Unsupported').style.display = anySupported ? 'none' : '';
            el('Controls').style.display    = anySupported ? '' : 'none';
            if (!anySupported) return;

            this._renderFreq('Lcut', data.lcutFreq);
            this._renderSlope('Lcut', data.lcutSlope);
            this._renderFreq('Hcut', data.hcutFreq);
            this._renderSlope('Hcut', data.hcutSlope);
        } catch (e) {
            console.error(`audioFilter[${this._vfo}].refresh error:`, e);
        } finally {
            this._busy = false;
        }
    }

    _renderFreq(prefix, v) {
        const slider = this._el(prefix + 'FreqSlider');
        const off    = this._el(prefix + 'FreqOff');
        const label  = this._el(prefix + 'FreqLabel');
        if (!v || !v.supported) {
            slider.disabled = true; off.disabled = true; label.textContent = '—';
            return;
        }
        off.disabled = false;
        if (v.label === 'OFF') {
            off.checked = true; slider.disabled = true;
            label.textContent = 'OFF';
        } else {
            off.checked = false; slider.disabled = false;
            if (typeof v.hz === 'number') {
                slider.value = v.hz;
                if (prefix === 'Lcut') this._lastLcutHz = v.hz; else this._lastHcutHz = v.hz;
            }
            label.textContent = v.label || (slider.value + ' Hz');
        }
    }

    _renderSlope(prefix, v) {
        const btn = this._el(prefix + 'SlopeBtn');
        if (!v || !v.supported) {
            btn.disabled = true; btn.textContent = '—';
            btn.classList.remove('btn-warning'); btn.classList.add('btn-outline-light');
            return;
        }
        btn.disabled = false;
        btn.dataset.slopeCode = v.code || '0';
        btn.textContent = v.label || (v.code === '1' ? '18 dB/oct' : '6 dB/oct');
        btn.classList.toggle('btn-warning',       v.code === '1');
        btn.classList.toggle('btn-outline-light', v.code !== '1');
    }

    async _write(setting, code) {
        try {
            const resp = await fetch(`/api/cat/audiofilter/${this._vfo.toLowerCase()}/${setting}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code }),
            });
            if (!resp.ok) return;
            const v = (await resp.json()).result;
            if (setting === 'lcutFreq')       this._renderFreq('Lcut', v);
            else if (setting === 'hcutFreq')  this._renderFreq('Hcut', v);
            else if (setting === 'lcutSlope') this._renderSlope('Lcut', v);
            else if (setting === 'hcutSlope') this._renderSlope('Hcut', v);
        } catch (e) { console.error(`audioFilter[${this._vfo}].write error:`, e); }
    }
}

/** Slider Hz to the EX menu's two-digit code: 01 is the base, one per 50 Hz. */
export function hzToFreqCode(hz, isLcut) {
    const base = isLcut ? 100 : 700;
    const n = Math.round((hz - base) / 50) + 1;
    return String(n).padStart(2, '0');
}
