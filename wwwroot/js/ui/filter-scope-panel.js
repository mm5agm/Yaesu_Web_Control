// filter-scope-panel.js — Filter Function Display canvas renderer
// Shows DSP filter passband shape, roofing filter outline, notch, contour, and APF markers.
// Pure computation from CAT state — no actual signal content.

// IF Width code → Hz per radio model (mirrors ifWidthOptions in Index.cshtml)
const IF_WIDTH_TABLES = {
    // FTdx101MP/D: code 0 = mode-dependent default (3 kHz in SSB w/3 kHz roofing);
    // codes 1-21 run narrow→wide. From Table 3 of the CAT manual.
    'FTdx101MP': { '0':3000,'1':300,'2':400,'3':600,'4':850,'5':1100,'6':1200,'7':1500,
                   '8':1650,'9':1800,'10':1950,'11':2100,'12':2200,'13':2300,'14':2400,
                   '15':2500,'16':2600,'17':2700,'18':2800,'19':2900,'20':3000,'21':3200 },
    'FTdx101D':  { '0':3000,'1':300,'2':400,'3':600,'4':850,'5':1100,'6':1200,'7':1500,
                   '8':1650,'9':1800,'10':1950,'11':2100,'12':2200,'13':2300,'14':2400,
                   '15':2500,'16':2600,'17':2700,'18':2800,'19':2900,'20':3000,'21':3200 },
    // FTdx10: code 0 = 3 kHz wide default; codes 1-22 run narrow→wide
    'FTdx10':    { '0':3000,'1':300,'2':400,'3':600,'4':850,'5':1100,'6':1200,'7':1500,
                   '8':1650,'9':1800,'10':1950,'11':2100,'12':2250,'13':2400,'14':2450,'15':2500,
                   '16':2600,'17':2700,'18':2800,'19':2900,'20':3200,'21':3500,'22':4000 },
    'FT-710':    { '0':300,'2':600,'3':850,'5':1200,'7':1650,'9':1950,'12':2400,'16':2700,
                   '19':3000,'20':3200,'21':3500,'22':4000 },
    'FTDX3000':  { '1':200,'2':400,'3':600,'4':850,'6':1350,'7':1500,'9':1800,'12':2200,
                   '14':2400,'16':2600,'18':2800,'20':3000,'22':3400,'25':4000 },
};

// Roofing filter code → Hz (FTdx101MP/D)
const ROOFING_HZ = { '6':12000,'7':3000,'8':1200,'9':600,'A':300,'a':300 };

// FTdx10 roofing filter read codes 6/7/9/A (500 Hz standard, not 600 Hz)
const ROOFING_HZ_FTDX10 = { '6':12000,'7':3000,'9':500,'A':300,'a':300 };

// FTDX3000 roofing filter set codes (P2): 0=Auto (no fixed width), 1=15k,
// 2=6k, 3=3k, 4=600, 5=300. Keyed to match the dropdown values in Index.cshtml
// and the normalised state code from the backend. Auto (0) has no single width
// so it falls through to null and no roofing outline is drawn.
const ROOFING_HZ_3000 = { '1':15000,'2':6000,'3':3000,'4':600,'5':300 };

export class FilterScopePanel {
    constructor(canvasId, radioModel, initialState = {}) {
        this._canvasId  = canvasId;
        this._model     = radioModel || 'FTdx101MP';
        this._widthTable = IF_WIDTH_TABLES[this._model] || IF_WIDTH_TABLES['FTdx101MP'];
        this._resizeObserver = null;

        this._state = {
            ifWidthCode:      '8',
            ifShiftHz:        0,
            roofingCode:      '',
            manualNotchOn:    false,
            manualNotchFreqHz: 800,
            contourOn:        false,
            contourFreqHz:    800,
            apfOn:            false,
            apfFreqHz:        0,
            mode:             'USB',
            ...initialState
        };

        this._init();
    }

    setState(updates) {
        Object.assign(this._state, updates);
        this._render();
    }

    _init() {
        const canvas = document.getElementById(this._canvasId);
        if (!canvas) return;
        this._sizeCanvas(canvas);
        this._resizeObserver = new ResizeObserver(() => {
            this._sizeCanvas(canvas);
        });
        this._resizeObserver.observe(canvas.parentElement ?? canvas);
        this._startAnimation();
    }

    _startAnimation() {
        let frameCount = 0;
        const loop = () => {
            this._animFrame = requestAnimationFrame(loop);
            if (++frameCount % 3 === 0) this._render();  // ~20 fps
        };
        this._animFrame = requestAnimationFrame(loop);
    }

    /**
     * Cancel the animation loop and disconnect the resize observer. Called by
     * the ServerShutdown overlay in site.js so a dead browser tab doesn't keep
     * burning CPU at 20 fps after the server has stopped. Idempotent — safe
     * to call multiple times.
     */
    stop() {
        if (this._animFrame) {
            try { cancelAnimationFrame(this._animFrame); } catch { /* ignore */ }
            this._animFrame = null;
        }
        if (this._resizeObserver) {
            try { this._resizeObserver.disconnect(); } catch { /* ignore */ }
            this._resizeObserver = null;
        }
    }

    _sizeCanvas(canvas) {
        const w = 160;
        canvas.width        = w;
        canvas.height       = 80;
        canvas.style.width  = w + 'px';
        canvas.style.height = '80px';
    }

    // Returns the display bounds in Hz based on the current mode AND
    // the current passband. The static per-mode default is the lower bound
    // — if the passband extends past it (e.g. CW with a wide IF Width
    // where the passband centred on +700 Hz spans negative Hz on the
    // lower side), the bounds expand to include the whole passband with
    // a 200 Hz margin on each side. Reported by Jacek SP3L on #34: at
    // 3 kHz and 12 kHz CW the trapezium's left slope vanished off the
    // canvas edge because the previous fixed [0, rangeHz] axis couldn't
    // represent negative audio Hz.
    _displayBounds() {
        // Axis tracks the current passband with margin on each side, so the
        // trapezium fills most of the canvas at every IF Width. This makes
        // the contour / notch / APF markers proportionally bigger and easier
        // to read at narrow filters (e.g. 300 Hz CW) where the trapezium
        // previously occupied only ~10% of the canvas. The labels adapt to
        // whatever range we're showing.
        const margin = 300;
        const ifWidthHz = this._ifWidthHz();
        const { lo: pbLo, hi: pbHi } = this._passbandEdges(ifWidthHz);
        return { lo: pbLo - margin, hi: pbHi + margin };
    }

    /**
     * Returns the current audio passband edges, in Hz, as {lo, hi}. Exposed
     * so site.js can use the same calculation for the contour slider's
     * dynamic min/max without duplicating the passband formula.
     */
    getPassband() {
        return this._passbandEdges(this._ifWidthHz());
    }

    _hzToX(hz, W, loHz, hiHz) {
        return Math.round(((hz - loHz) / (hiHz - loHz)) * W);
    }

    _ifWidthHz() {
        // Prefer the mode-aware lookup so the passband matches what the radio
        // is actually doing in the current mode (CW code 8 = 400 Hz, SSB
        // code 8 = 1650 Hz on the FTdx101 etc.). Falls back to the static
        // SSB table if the mode-aware module is unavailable.
        let hz = null;
        if (window.IfWidth) {
            hz = window.IfWidth.ifWidthHzFor(this._model, this._state.mode, parseInt(this._state.ifWidthCode));
        }
        if (hz == null) hz = this._widthTable[String(this._state.ifWidthCode)] || 3000;
        const roofHz = this._roofingHz();
        return roofHz !== null ? Math.min(hz, roofHz) : hz;
    }

    _roofingHz() {
        if (this._model === 'FTDX3000') {
            return ROOFING_HZ_3000[String(this._state.roofingCode)] || null;
        }
        if (this._model === 'FTdx10') {
            return ROOFING_HZ_FTDX10[String(this._state.roofingCode)] || null;
        }
        return ROOFING_HZ[String(this._state.roofingCode)] || null;
    }

    // Returns { lo, hi } passband edges in audio Hz
    _passbandEdges(ifWidthHz) {
        const mode = (this._state.mode || '').toUpperCase();
        const shift = this._state.ifShiftHz || 0;
        if (mode.startsWith('CW')) {
            const centre = 700 + shift;
            return { lo: centre - ifWidthHz / 2, hi: centre + ifWidthHz / 2 };
        } else if (mode === 'AM' || mode === 'AM-N') {
            return { lo: 0, hi: ifWidthHz / 2 };
        } else {
            // SSB/Data: audio lower cutoff is ~300 Hz; IF Width extends upward from there
            return { lo: 300 + shift, hi: 300 + shift + ifWidthHz };
        }
    }

    _render() {
        const canvas = document.getElementById(this._canvasId);
        if (!canvas) return;
        const ctx    = canvas.getContext('2d');
        const W      = canvas.width;
        const H      = canvas.height;
        const { lo: rangeLo, hi: rangeHi } = this._displayBounds();
        const rangeHz = rangeHi - rangeLo;
        const axisH  = 14;   // pixels reserved for frequency axis at bottom
        const scopeH = H - axisH;

        // --- Background ---
        ctx.fillStyle = '#1e2a38';
        ctx.fillRect(0, 0, W, H);

        const x = hz => this._hzToX(hz, W, rangeLo, rangeHi);

        // --- Passband (trapezoid with sloped sides) ---
        const ifWidthHz = this._ifWidthHz();
        const { lo: pbLo, hi: pbHi } = this._passbandEdges(ifWidthHz);
        const pxLo    = x(pbLo);
        const pxHi    = x(pbHi);
        const pbTop   = Math.round(scopeH * 0.05);
        const pbBot   = scopeH;
        const slopeW  = Math.max(6, Math.round((pxHi - pxLo) * 0.08));

        // Trapezoid path: wider at bottom, narrower at top (filter roll-off shape)
        const trapPath = () => {
            ctx.beginPath();
            ctx.moveTo(pxLo,           pbBot);
            ctx.lineTo(pxHi,           pbBot);
            ctx.lineTo(pxHi - slopeW,  pbTop);
            ctx.lineTo(pxLo + slopeW,  pbTop);
            ctx.closePath();
        };

        // Subtle fill inside the trapezoid
        trapPath();
        ctx.fillStyle = 'rgba(74,138,191,0.10)';
        ctx.fill();

        // Clip to trapezoid, then draw animated signal bars inside it
        ctx.save();
        trapPath();
        ctx.clip();

        const barW    = 2;
        const maxBarH = Math.floor((pbBot - pbTop) * 0.85);
        const barBase = pbBot - 1;
        for (let bx = pxLo; bx <= pxHi; bx += barW) {
            const nh = Math.random();
            const bh = Math.max(2, Math.round(nh * maxBarH));
            ctx.fillStyle = `rgba(80,210,80,${(0.4 + nh * 0.5).toFixed(2)})`;
            ctx.fillRect(bx, barBase - bh, barW - 1, bh);
        }

        ctx.restore();

        // Red trapezoid border — all sides
        trapPath();
        ctx.strokeStyle = '#e83535';
        ctx.lineWidth   = 1.5;
        ctx.stroke();

        // --- Manual notch ---
        if (this._state.manualNotchOn) {
            const nFreq = this._state.manualNotchFreqHz || 800;
            const nPx   = x(nFreq);
            const notchW = Math.max(2, Math.round(W * 0.008));
            ctx.fillStyle = 'rgba(0,0,0,0.75)';
            ctx.fillRect(nPx - notchW, pbTop, notchW * 2, pbBot - pbTop);
            ctx.strokeStyle = 'rgba(180,180,180,0.5)';
            ctx.lineWidth   = 1;
            ctx.beginPath();
            ctx.moveTo(nPx + 0.5, pbTop);
            ctx.lineTo(nPx + 0.5, pbBot);
            ctx.stroke();
        }

        // --- Contour marker (downward arrow on top edge, like FTdx101MP display) ---
        if (this._state.contourOn) {
            const cPx = x(this._state.contourFreqHz || 800);
            const aW  = 5;   // half-width of arrowhead base
            const aH  = 7;   // height of arrowhead
            ctx.fillStyle   = '#ffffff';
            ctx.strokeStyle = '#aaaaaa';
            ctx.lineWidth   = 0.5;
            ctx.beginPath();
            ctx.moveTo(cPx,      pbTop + aH);  // tip — pointing down into passband
            ctx.lineTo(cPx - aW, pbTop - 1);   // base left — sits above top edge
            ctx.lineTo(cPx + aW, pbTop - 1);   // base right
            ctx.closePath();
            ctx.fill();
            ctx.stroke();
        }

        // --- APF marker ---
        if (this._state.apfOn) {
            const mode    = (this._state.mode || '').toUpperCase();
            const cwCentre = 700 + (this._state.ifShiftHz || 0);
            const apfPx   = x(cwCentre + (this._state.apfFreqHz || 0));
            const peakHalf = Math.max(3, Math.round(W * 0.015));
            ctx.fillStyle = 'rgba(0,229,204,0.7)';
            ctx.beginPath();
            ctx.moveTo(apfPx, pbTop + 4);
            ctx.lineTo(apfPx - peakHalf, pbBot - 4);
            ctx.lineTo(apfPx + peakHalf, pbBot - 4);
            ctx.closePath();
            ctx.fill();
            ctx.strokeStyle = '#00e5cc';
            ctx.lineWidth   = 1;
            ctx.stroke();
        }

        // --- IF shift arrow at top ---
        const shift = this._state.ifShiftHz || 0;
        if (Math.abs(shift) > 50) {
            const arrowX = x(1500 + shift);
            const dir    = shift > 0 ? 1 : -1;
            const aSize  = 5;
            ctx.fillStyle = 'rgba(200,220,255,0.8)';
            ctx.beginPath();
            ctx.moveTo(arrowX + dir * aSize, 4);
            ctx.lineTo(arrowX - dir * aSize, 4 - aSize);
            ctx.lineTo(arrowX - dir * aSize, 4 + aSize);
            ctx.closePath();
            ctx.fill();
        }

        // --- Grid lines ---
        ctx.strokeStyle = 'rgba(100,120,140,0.3)';
        ctx.lineWidth   = 0.5;
        const step = rangeHz <= 4000 ? 500 : rangeHz <= 7000 ? 1000 : 2000;
        // First grid line at the lowest multiple of step strictly INSIDE
        // (rangeLo, rangeHi). Ceil handles negative rangeLo correctly.
        const firstGridHz = Math.ceil((rangeLo + 1) / step) * step;
        for (let hz = firstGridHz; hz < rangeHi; hz += step) {
            const gx = x(hz) + 0.5;
            ctx.beginPath();
            ctx.moveTo(gx, 0);
            ctx.lineTo(gx, scopeH);
            ctx.stroke();
        }

        // --- Frequency axis ---
        ctx.fillStyle = '#8899aa';
        ctx.font      = '9px sans-serif';
        ctx.textBaseline = 'bottom';
        const firstLabelHz = Math.ceil(rangeLo / step) * step;
        const lastLabelHz  = Math.floor(rangeHi / step) * step;
        for (let hz = firstLabelHz; hz <= rangeHi; hz += step) {
            const lx  = x(hz);
            const absHz = Math.abs(hz);
            const lbl = absHz >= 1000 ? (hz / 1000) + 'k'
                      : hz === 0      ? '0'
                      :                  hz + '';
            // Left-align the leftmost label and right-align the rightmost
            // so neither gets clipped at the canvas edges (was "-1k"
            // rendering as just "k" when centred on the left edge).
            if (hz === firstLabelHz)     ctx.textAlign = 'left';
            else if (hz === lastLabelHz) ctx.textAlign = 'right';
            else                          ctx.textAlign = 'center';
            ctx.fillText(lbl, lx, H - 1);
        }

        // --- Roofing filter label (top-right corner) ---
        //
        // The trapezium shape is the DSP filter (IF Width), not the roofing.
        // When the roofing filter is WIDER than the DSP filter (e.g. 12k or
        // 3k roofing with a 2.7 kHz DSP setting in SSB), the trapezium looks
        // identical for those roofing choices because the DSP is the actual
        // limit. Without a label, operators can't tell whether they're on
        // 12k or 3k roofing from the display.
        //
        // A small text label removes the ambiguity:
        //   • Tells the operator which roofing is selected at a glance
        //   • Doesn't disrupt the existing trapezium UX
        //   • Only drawn when a roofing filter is actually set
        const roofHz = this._roofingHz();
        if (roofHz !== null) {
            const roofLabel = roofHz >= 1000
                ? 'Roof ' + (roofHz / 1000).toString().replace(/\.0$/, '') + 'k'
                : 'Roof ' + roofHz;
            ctx.fillStyle    = '#aab8c4';
            ctx.font         = '9px sans-serif';
            ctx.textAlign    = 'right';
            ctx.textBaseline = 'top';
            ctx.fillText(roofLabel, W - 2, 2);
        }
    }
}
