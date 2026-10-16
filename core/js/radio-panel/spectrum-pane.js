// spectrum-pane.js - one receiver's spectrum, waterfall and overlays, on its
// own: the pop-out spectrum window shows one of these per receiver.
//
// The radio panel's board draws the same picture with the same SpectrumView;
// this is that picture without the board around it. Like SpectrumView it is
// fed plain numbers - bins already ordered low to high and centred on the
// dial - so it knows nothing about SDRs, the hub or which radio is attached.
//
// Needs the radio panel's stylesheet (injectRadioPanelStyle) and a theme on
// an ancestor (applyTheme): every colour is a --rp-* property.

import { SpectrumView } from './spectrum-view.js';
import { axisLabels, passbandPercent, spanText } from './panel-math.js';

function h(tag, cls, text) {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (text != null) el.textContent = text;
    return el;
}

export class SpectrumPane {
    /**
     * @param {HTMLElement} container  the pane fills it
     * @param {object} [opts]
     * @param {string} [opts.empty='No SDR']  shown until the first frame
     */
    constructor(container, opts = {}) {
        this.el = h('div', 'rp-pane');
        this.bar = h('div', 'rp-pane-bar');
        this._label = h('span', 'rp-pane-label', '');
        this.tools = h('span', 'rp-pane-tools');
        this.bar.append(this._label, this.tools);
        const spec = h('div', 'rp-spectrum');
        this._pb = h('div', 'rp-passband');
        this._spots = h('div');
        this._empty = h('div', 'rp-spec-empty', opts.empty ?? 'No SDR');
        spec.append(this._pb, h('div', 'rp-cursor'), this._spots, this._empty);
        this._axis = h('div', 'rp-axis');
        const wf = h('div', 'rp-waterfall');
        this.el.append(this.bar, spec, this._axis, wf);
        container.append(this.el);
        this._view = new SpectrumView(spec, wf);
        this._st = { hz: 0, mode: '', widthHz: null, spanHz: 0, spots: [], showSpots: true, name: '', status: null, passband: true };
    }

    /** The board's drawing settings: SpectrumView's, plus passband on or off. */
    setSettings(s) {
        this._view.setSettings(s);
        this._st.passband = s?.passband !== false;
        this._draw();
    }

    setTheme(theme) { this._view.setTheme(theme); }

    /** What the label says before the frequency and span, e.g. 'MAIN · SDR A'. */
    setName(name) { this._st.name = name ?? ''; this._drawLabel(); }
    setFrequency(hz) { this._st.hz = Number(hz) || 0; this._draw(); }
    setPassband(mode, widthHz) { this._st.mode = mode ?? ''; this._st.widthHz = widthHz ?? null; this._draw(); }
    setSpan(hz) {
        const s = Number(hz) || 0;
        if (s === this._st.spanHz) return;
        this._st.spanHz = s;
        this._draw();
    }
    setSpots(list) { this._st.spots = list ?? []; this._drawSpots(); }
    setShowSpots(show) { this._st.showSpots = !!show; this._drawSpots(); }

    /** A message across the spectrum (e.g. 'SDR disconnected'), or null for none. */
    setStatus(text) {
        this._st.status = text || null;
        if (text) { this._empty.textContent = text; this._empty.style.display = ''; this._view.clear(); }
    }

    push(bins) {
        if (this._st.status) return;
        this._empty.style.display = 'none';
        this._view.push(bins);
    }

    clear() { this._view.clear(); }

    _drawLabel() {
        const s = this._st;
        const p3 = n => String(n).padStart(3, '0');
        const hz = Math.round(s.hz);
        const f = hz > 0 ? `${Math.floor(hz / 1e6)}.${p3(Math.floor(hz / 1000) % 1000)}.${p3(hz % 1000)}` : '';
        this._label.textContent = [s.name, f, s.spanHz ? 'SPAN ' + spanText(s.spanHz) : ''].filter(Boolean).join(' · ');
    }

    _draw() {
        const s = this._st;
        const pct = s.passband && passbandPercent(s.mode, s.widthHz, s.spanHz);
        if (pct) { this._pb.style.display = 'block'; this._pb.style.left = pct.left + '%'; this._pb.style.width = pct.width + '%'; }
        else this._pb.style.display = 'none';
        this._axis.textContent = '';
        if (s.spanHz > 0 && s.hz > 0) for (const t of axisLabels(s.hz, s.spanHz, 9)) this._axis.append(h('span', null, t));
        this._drawLabel();
        this._drawSpots();
    }

    _drawSpots() {
        const s = this._st;
        this._spots.textContent = '';
        if (!s.showSpots || !(s.spanHz > 0) || !(s.hz > 0)) return;
        let row = 0;
        for (const sp of s.spots) {
            const pos = 50 + (sp.hz - s.hz) / s.spanHz * 100;
            if (pos < 2 || pos > 98) continue;
            const el = h('div', 'rp-spot', sp.call);
            el.style.left = pos + '%';
            el.style.top = (30 + (row++ % 4) * 28) + 'px';
            this._spots.append(el);
        }
    }
}
