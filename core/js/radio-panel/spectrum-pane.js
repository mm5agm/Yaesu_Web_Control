// spectrum-pane.js - one receiver's spectrum, waterfall and overlays, on its
// own: the pop-out spectrum window shows one of these per receiver.
//
// The radio panel's board draws the same picture with the same SpectrumView;
// this is that picture without the board around it. Like SpectrumView it is
// fed plain numbers - bins already ordered low to high and centred on the
// dial, or on a point setViewOffset names - so it knows nothing about SDRs,
// the hub or which radio is attached.
//
// Needs the radio panel's stylesheet (injectRadioPanelStyle) and a theme on
// an ancestor (applyTheme): every colour is a --rp-* property.
//
// Given onTune it is tunable as the board's panes are: a click on the
// spectrum or waterfall tunes there, and a hairline and readout follow the
// pointer naming the frequency a click would send.

import { SpectrumView } from './spectrum-view.js';
import { axisLabels, dialPercent, formatFrequency, passbandCentrePercent, passbandPercent, spanText, spectrumHzAt } from './panel-math.js';

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
     * @param {string} [opts.empty='Waiting for data']  shown until the first frame
     * @param {(hz:number) => void} [opts.onTune]  a click on the spectrum or waterfall
     * @param {(hz:number) => number} [opts.tuneTo]  where the dial lands for a click
     * @param {(dir:number) => void} [opts.onStep]  a mouse wheel notch, +1 up or -1 down
     *        at hz, when the app tunes somewhere else (Yaesu RTTY); the readout shows it
     * @param {boolean} [opts.grip=false]  a bar between the spectrum and the waterfall
     *        that drags their split (setSplit); off, the split is the stylesheet's
     * @param {(frac:number) => void} [opts.onSplit]  the split after a drag, to keep
     * @param {() => void} [opts.onFit]  a double-click on the grip; the pane has fitted
     *        the trace (fit), and this is told so the range can be kept
     * @param {boolean} [opts.dbScale=false]  label the trace in dB above the noise floor
     * @param {boolean} [opts.waterfallFirst=false]  the waterfall above the spectrum (setOrder)
     * @param {(hz:number) => void} [opts.onJump]  arrows at the left and right edges of the
     *        part on top: a click asks for the dial one span down or up, at hz
     * @param {{hz:number, label:string}[]} [opts.spans]  with onSpan, the span in the
     *        label is a list of these, styled as the label
     * @param {(hz:number) => void} [opts.onSpan]  a span picked on that list
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
        this._empty = h('div', 'rp-spec-empty', opts.empty ?? 'Waiting for data');
        this._pbMid = h('div', 'rp-pbcentre');
        this._cursor = h('div', 'rp-cursor');
        spec.append(this._pb, this._pbMid, this._cursor, this._spots, this._empty);
        this._axis = h('div', 'rp-axis');
        const wf = h('div', 'rp-waterfall');
        this.el.append(this.bar, spec, this._axis, wf);
        container.append(this.el);
        this._specEl = spec;
        this._wfEl = wf;
        if (opts.grip) this._grip(opts.onSplit, opts.onFit);
        this._view = new SpectrumView(spec, wf, { dbScale: opts.dbScale });
        this._st = { hz: 0, view: 0, mode: '', widthHz: null, spanHz: 0, spots: [], showSpots: true, name: '', status: null, passband: true };
        if (opts.spans?.length && opts.onSpan) this._spanList(opts.spans, opts.onSpan);
        if (opts.onJump) this._jumps(opts.onJump);
        if (opts.waterfallFirst) this.setOrder(true);
        if (opts.onTune) this._tunable(spec, wf, opts.onTune, opts.tuneTo);
        // The wheel steps the dial a notch at a time, as on the board.
        if (opts.onStep) {
            for (const el of [spec, wf]) {
                el.title = opts.onTune ? 'Click to tune there; mouse wheel steps the dial' : 'Mouse wheel steps the dial';
                el.addEventListener('wheel', e => {
                    if (!e.deltaY || e.target.closest('select')) return;
                    e.preventDefault();
                    opts.onStep(e.deltaY < 0 ? 1 : -1);
                }, { passive: false });
            }
        }
    }

    // The board's rules (radio-panel.js): within 8 px of a DX spot is the
    // spot's own frequency, and the readout names exactly what a click there
    // would send. A click's clientX is whole CSS pixels while a pointer's is
    // fractional, so the click takes the pointer's last x when it is within a
    // pixel of it, or the readout and the radio disagree at wide spans.
    _tunable(spec, wf, onTune, tuneTo) {
        const at = (el, clientX) => {
            const s = this._st, rc = el.getBoundingClientRect();
            if (!rc.width) return null;
            return spectrumHzAt((clientX - rc.left) / rc.width, s.hz > 0 ? s.hz + s.view : 0, s.spanHz,
                s.showSpots ? s.spots : [], 8 / rc.width * s.spanHz);
        };
        const read = h('div', 'rp-xread');
        const lines = [spec, wf].map(el => { const x = h('div', 'rp-xhair'); el.append(x); return x; });
        spec.append(read);
        const hide = () => { read.style.display = 'none'; for (const x of lines) x.style.display = 'none'; };
        hide();
        let lastX = null, over = null;
        const show = (el, clientX) => {
            const a = at(el, clientX);
            if (!a) return hide();
            const fr = formatFrequency(a.hz);
            const to = tuneTo?.(a.hz);
            const dial = Number.isFinite(to) && Math.round(to) !== a.hz ? formatFrequency(Math.round(to)) : null;
            for (const x of lines) { x.style.left = a.frac * 100 + '%'; x.style.display = 'block'; }
            read.textContent = fr.big + fr.small + (a.spot ? ' ' + a.spot.call : '')
                + (dial ? ' → dial ' + dial.big + dial.small : '');
            read.style.display = 'block';
            read.classList.toggle('left', a.frac > 0.75);
            read.style.left = a.frac * 100 + '%';
        };
        // When the dial moves (the wheel, or the radio's knob) the trace slides
        // under a still pointer: name what is under it now, not what was.
        this._reread = () => { if (over && lastX != null) show(over, lastX); };
        for (const el of [spec, wf]) {
            el.classList.add('rp-tunable');
            el.title = 'Click to tune there';
            el.addEventListener('pointermove', e => {
                lastX = e.clientX;
                over = el;
                show(el, e.clientX);
            });
            el.addEventListener('pointerleave', () => { lastX = null; over = null; hide(); });
            el.addEventListener('click', e => {
                const a = at(el, lastX != null && Math.abs(lastX - e.clientX) < 1 ? lastX : e.clientX);
                if (a) onTune(a.hz);
            });
        }
    }

    // A pair of arrows, one span down and one span up. They sit on the part
    // on top (_placeJumps), and a click on them is theirs, not a tune.
    _jumps(onJump) {
        this._jumpEls = [-1, 1].map(dir => {
            const b = h('button', 'rp-jump ' + (dir < 0 ? 'rp-jump-dn' : 'rp-jump-up'), dir < 0 ? '◀' : '▶');
            b.type = 'button';
            b.title = dir < 0 ? 'One span down' : 'One span up';
            b.setAttribute('aria-label', b.title);
            for (const ev of ['pointerdown', 'pointermove', 'dblclick']) b.addEventListener(ev, e => e.stopPropagation());
            b.addEventListener('click', e => {
                e.stopPropagation();
                const s = this._st;
                if (s.hz > 0 && s.spanHz > 0) onJump(s.hz + dir * s.spanHz);
            });
            return b;
        });
        this._placeJumps();
    }
    _placeJumps() {
        if (!this._jumpEls) return;
        const c = this.el.classList;
        const wfTop = c.contains('no-spec') || (this._wfFirst && !c.contains('no-wf'));
        (wfTop ? this._wfEl : this._specEl).append(...this._jumpEls);
    }

    // The spectrum's share of the height the two share, 0.15..0.85. The grip
    // sits under the axis, so the axis stays with the trace it labels.
    _grip(onSplit, onFit) {
        const grip = h('div', 'rp-pane-grip');
        grip.title = 'Drag to share the height between the spectrum and the waterfall'
            + (onFit ? '; double-click to fit the trace to the signals' : '');
        this._gripEl = grip;
        this._wfEl.before(grip);
        if (onFit) grip.addEventListener('dblclick', () => { if (this.fit() != null) onFit(); });
        grip.addEventListener('pointerdown', e => {
            e.preventDefault();
            grip.setPointerCapture(e.pointerId);
            const first = this._wfFirst ? this._wfEl : this._specEl;
            const last = this._wfFirst ? this._specEl : this._wfEl;
            const top = first.getBoundingClientRect().top;
            const bottom = last.getBoundingClientRect().bottom;
            const move = m => {
                if (!(bottom > top)) return;
                const f = (m.clientY - top) / (bottom - top);
                this.setSplit(this._wfFirst ? 1 - f : f);
            };
            const up = () => {
                grip.removeEventListener('pointermove', move);
                grip.removeEventListener('pointerup', up);
                grip.removeEventListener('pointercancel', up);
                onSplit?.(this._split);
            };
            grip.addEventListener('pointermove', move);
            grip.addEventListener('pointerup', up);
            grip.addEventListener('pointercancel', up);
        });
    }

    /** The spectrum's share of the height, 0.15..0.85; the waterfall has the rest. */
    setSplit(frac) {
        const f = Math.max(0.15, Math.min(0.85, Number(frac) || 0.5));
        this._split = f;
        this._specEl.style.flex = `${f} 1 0`;
        this._wfEl.style.flex = `${1 - f} 1 0`;
    }

    /**
     * The waterfall above the spectrum, or (false) below it as usual. The axis
     * stays against the spectrum and the grip between the two.
     */
    setOrder(waterfallFirst) {
        this._wfFirst = !!waterfallFirst;
        const parts = this._wfFirst
            ? [this._wfEl, this._gripEl, this._axis, this._specEl]
            : [this._specEl, this._axis, this._gripEl, this._wfEl];
        this.el.append(...parts.filter(x => x instanceof HTMLElement));
        this._placeJumps();
    }

    /** SpectrumView's levels: { auto, ref } in, { auto, ref, floor, lo, hi } out. */
    setLevels(l) { return this._view.setLevels(l); }
    get levels() { return this._view.levels; }
    /** Fit the range to the recent peaks; the range in force, or null with no data. */
    fit() { return this._view.fit(); }
    get settings() { return this._view.settings; }

    /** The board's drawing settings: SpectrumView's, plus passband on or off. */
    setSettings(s) {
        this._view.setSettings(s);
        this._st.passband = s?.passband !== false;
        this._draw();
    }

    setTheme(theme) { this._view.setTheme(theme); }
    /** The trace's width in CSS pixels (SpectrumView.setTraceWidth). */
    setTraceWidth(px) { this._view.setTraceWidth(px); }

    /** What the label says before the frequency and span, e.g. 'MAIN · SDR A'. */
    setName(name) { this._st.name = name ?? ''; this._drawLabel(); }
    setFrequency(hz) { this._st.hz = Number(hz) || 0; this._draw(); }
    /**
     * How far the bins' centre sits from the dial, in Hz: 0 when they are
     * centred on the dial, as they are unless the feed says otherwise. The
     * axis, spots and clicks then read from that centre, and the dial's line
     * moves off the middle by as much.
     */
    setViewOffset(hz) {
        const v = Number(hz) || 0;
        if (v === this._st.view) return;
        this._st.view = v;
        this._draw();
    }
    /** dataCentreHz: where the radio centres its DATA filter from the dial; see passbandHz. */
    setPassband(mode, widthHz, dataCentreHz) {
        this._st.mode = mode ?? ''; this._st.widthHz = widthHz ?? null; this._st.dataCentreHz = dataCentreHz ?? 0;
        this._draw();
    }
    setSpan(hz) {
        const s = Number(hz) || 0;
        if (s === this._st.spanHz) return;
        this._st.spanHz = s;
        this._draw();
        this._showSpan();
    }

    // The span in the label as a list: the bar keeps the label's look, and
    // the list is the label's last part, so 'SPAN 10 kHz ▾' reads as before.
    _spanList(spans, onSpan) {
        this.bar.classList.add('has-span');
        const wrap = h('span', 'rp-pane-spanwrap');
        const sel = h('select', 'rp-pane-span');
        sel.title = 'Span';
        for (const s of spans) sel.append(new Option('SPAN ' + s.label, String(s.hz)));
        sel.addEventListener('change', () => onSpan(Number(sel.value)));
        wrap.append(sel);
        this._label.after(wrap);
        this._spanSel = sel;
        this._showSpan();
    }

    // A frame's span is whole bins: 20 kHz arrives as 19989 Hz, so a list
    // span within 2% is the one shown. A span not on the list shows as itself.
    _showSpan() {
        const sel = this._spanSel;
        if (!sel || document.activeElement === sel) return;
        const n = this._st.spanHz;
        sel.querySelector('.rp-span-other')?.remove();
        if (!n) { sel.selectedIndex = -1; return; }
        const o = [...sel.options].find(x => Math.abs(Number(x.value) - n) <= Number(x.value) * 0.02);
        if (o) { sel.value = o.value; return; }
        const other = new Option('SPAN ' + spanText(n), String(n));
        other.className = 'rp-span-other';
        other.disabled = true;
        sel.append(other);
        sel.value = other.value;
    }
    setSpots(list) { this._st.spots = list ?? []; this._drawSpots(); this._reread?.(); }
    setShowSpots(show) { this._st.showSpots = !!show; this._drawSpots(); this._reread?.(); }

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

    /** Show or hide the waterfall; hidden, the trace takes its room. */
    setWaterfall(show) { this.el.classList.toggle('no-wf', !show); this._placeJumps(); }

    /**
     * Which parts to draw: 'both', 'spectrum', 'waterfall' or 'none'. The
     * other part keeps being fed, so it is ready when it comes back.
     */
    setParts(parts) {
        const spec = parts === 'both' || parts === 'spectrum';
        const wf = parts === 'both' || parts === 'waterfall';
        this.el.hidden = !spec && !wf;
        this.el.classList.toggle('no-wf', !wf);
        this.el.classList.toggle('no-spec', !spec);
        this._placeJumps();
    }

    _drawLabel() {
        const s = this._st;
        const p3 = n => String(n).padStart(3, '0');
        const hz = Math.round(s.hz);
        const f = hz > 0 ? `${Math.floor(hz / 1e6)}.${p3(Math.floor(hz / 1000) % 1000)}.${p3(hz % 1000)}` : '';
        const span = s.spanHz && !this._spanSel ? 'SPAN ' + spanText(s.spanHz) : '';
        this._label.textContent = [s.name, f, span].filter(Boolean).join(' · ') + (this._spanSel ? ' ·' : '');
    }

    _draw() {
        const s = this._st;
        const pct = s.passband && passbandPercent(s.mode, s.widthHz, s.spanHz, s.dataCentreHz, s.view);
        if (pct) { this._pb.style.display = 'block'; this._pb.style.left = pct.left + '%'; this._pb.style.width = pct.width + '%'; }
        else this._pb.style.display = 'none';
        const mid = s.passband ? passbandCentrePercent(s.mode, s.spanHz, s.dataCentreHz, s.view) : null;
        this._pbMid.style.display = mid == null ? 'none' : 'block';
        if (mid != null) this._pbMid.style.left = mid + '%';
        const dial = dialPercent(s.view, s.spanHz);
        this._cursor.style.display = dial == null ? 'none' : '';
        if (dial != null) this._cursor.style.left = dial + '%';
        this._axis.textContent = '';
        if (s.spanHz > 0 && s.hz > 0) for (const t of axisLabels(s.hz + s.view, s.spanHz, 9)) this._axis.append(h('span', null, t));
        this._drawLabel();
        this._drawSpots();
        this._reread?.();
    }

    _drawSpots() {
        const s = this._st;
        this._spots.textContent = '';
        if (!s.showSpots || !(s.spanHz > 0) || !(s.hz > 0)) return;
        let row = 0;
        for (const sp of s.spots) {
            const pos = 50 + (sp.hz - s.hz - s.view) / s.spanHz * 100;
            if (pos < 2 || pos > 98) continue;
            const el = h('div', 'rp-spot', sp.call);
            el.style.left = pos + '%';
            el.style.top = (30 + (row++ % 4) * 28) + 'px';
            this._spots.append(el);
        }
    }
}
