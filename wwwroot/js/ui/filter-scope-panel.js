// filter-scope-panel.js — Filter Function Display canvas renderer
// Shows DSP filter passband shape, roofing filter outline, notch, contour, and APF markers.
// Passband geometry is computed from CAT state. Green bars inside the passband are
// real RX spectrum: from the browser's own analyser while Remote Audio is
// playing, otherwise from the host's analysis of the radio's RX audio
// (FilterSpectrum over SignalR, see site.js). With neither attached the
// trapezium is empty. See setSpectrumProvider for why (#161).

// How the frequency axis is chosen. Both are kept on purpose -- Colin
// asked for the radio's look on 2026-09-19 and may want the other back:
//   'radio' -- a fixed audio span, as on the radio's own filter display:
//              the passband is drawn where it sits, so a 600 Hz filter is
//              a narrow slot a third of the way across and a 3 kHz one
//              nearly fills the box. Same picture as the front panel.
//   'zoom'  -- the axis follows the passband with a margin each side, so
//              the trapezium always fills most of the canvas and the
//              contour / notch / APF markers stay readable at 50 Hz.
const AXIS_MODE = 'radio';

// The fixed span for AXIS_MODE 'radio', in audio Hz. Read off the radio:
// at 2.9 kHz IF WIDTH the trapezium all but fills the box, at 600 Hz it is
// a slot at ~1.5 kHz a third of the way across. Widened when a passband
// runs past it (AM at 9 kHz is 0..4500 Hz; a wide CW filter about a low
// pitch reaches below zero) so nothing is ever drawn off the edge.
const RADIO_SPAN_HZ = 4000;

// Canvas size in CSS pixels. 240 x 120 from 2026-09-19 (was 160 x 80):
// with real signal in it the box earns the room, and the column has it.
const CANVAS_W = 240;
const CANVAS_H = 120;

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

// Top of the audio passband in the radio's RTTY modes: wide RTTY filters stop
// here however wide they are set (FTdx101MP, 2026-09-24, #178).
const RTTY_AUDIO_TOP_HZ = 2750;

// Audio limits of the passband in DATA-L / DATA-U: a filter wider than twice
// the DATA SHIFT loses everything below the bottom, and nothing passes above
// the top however wide it is set (FTdx101MP, 2026-09-24, #172).
const DATA_AUDIO_BOTTOM_HZ = 160;
const DATA_AUDIO_TOP_HZ    = 2980;

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
            // CW sidetone pitch, Hz. The CW passband is centred on the pitch,
            // not on a fixed 700 Hz -- an operator running 600 Hz was being
            // drawn a trapezium 100 Hz off, and the contour slider bounds and
            // APF marker derived from it were off by the same amount. Seeded
            // and kept current from the radio's own KP setting.
            cwPitchHz:        700,
            // The radio's RTTY MARK FREQUENCY and SHIFT (extended menu), which
            // place the RTTY-L / RTTY-U filter. Yaesu's defaults until the
            // host reads the real ones from GET /api/cat/rtty.
            rttyMarkHz:       2125,
            rttyShiftHz:      170,
            // The radio's DATA SHIFT (SSB) menu, Hz: where DATA-L / DATA-U
            // centre their filter in place of SSB's 1500. The host reads it
            // from GET /api/cat/datashift; 1500 is the radio's default.
            dataShiftHz:      1500,
            ...initialState
        };

        /** Optional () => { data, sampleRate, fftSize } | null from remote audio RX. */
        this._spectrumProvider = null;
        /** Same shape, fed by the host over SignalR; used when the above is not attached. */
        this._hostSpectrumProvider = null;

        this._init();
    }

    setState(updates) {
        Object.assign(this._state, updates);
        this._render();
    }

    /**
     * Attach or clear a live RX spectrum source (Remote Audio's RX analyser).
     * With no provider attached -- which is every page with Remote Audio not
     * running, i.e. most of them -- the trapezium is drawn empty. It used to
     * fill with Math.random() bars, which looked exactly like received signal
     * and was described as signal in the manual; Bruce VK2RT compared it with
     * the radio's own scope on #161 and quite reasonably asked why they
     * disagreed. They disagreed because ours was noise. The shape, the
     * markers and the axis are the real content of this panel.
     * @param {(() => ({ data: Uint8Array, sampleRate: number, fftSize: number } | null)) | null} provider
     */
    setSpectrumProvider(provider) {
        this._spectrumProvider = typeof provider === 'function' ? provider : null;
        this._render();
    }

    /**
     * The fallback source: the host's own FFT of the radio's RX audio,
     * pushed over SignalR whenever the Radio RX device is configured, with
     * no Remote Audio session needed. The browser-side provider above wins
     * while it is attached because it is the same audio with less latency.
     * @param {(() => ({ data: Uint8Array, sampleRate: number, fftSize: number } | null)) | null} provider
     */
    setHostSpectrumProvider(provider) {
        this._hostSpectrumProvider = typeof provider === 'function' ? provider : null;
        this._render();
    }

    _activeSpectrumProvider() {
        return this._spectrumProvider ?? this._hostSpectrumProvider;
    }

    _init() {
        const canvas = document.getElementById(this._canvasId);
        if (!canvas) return;
        this._sizeCanvas(canvas);
        this._resizeObserver = new ResizeObserver(() => {
            this._sizeCanvas(canvas);
            // Assigning canvas.width clears the canvas, and the loop below
            // only repaints when live audio is attached -- so repaint here
            // or a resize leaves the panel blank until the next setState.
            this._render();
        });
        this._resizeObserver.observe(canvas.parentElement ?? canvas);
        this._startAnimation();
    }

    _startAnimation() {
        let frameCount = 0;
        const loop = () => {
            this._animFrame = requestAnimationFrame(loop);
            // Only the bars move, and only when something is feeding them.
            // Everything else here repaints from setState. Without this the
            // panel redrew 20 times a second, on every open tab, for ever,
            // to show a new set of random numbers.
            if (!this._activeSpectrumProvider()) return;
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
        canvas.width        = CANVAS_W;
        canvas.height       = CANVAS_H;
        canvas.style.width  = CANVAS_W + 'px';
        canvas.style.height = CANVAS_H + 'px';
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
        const margin = 300;
        const ifWidthHz = this._ifWidthHz();
        const { lo: pbLo, hi: pbHi } = this._passbandEdges(ifWidthHz);

        if (AXIS_MODE === 'radio') {
            // Fixed span, as on the radio (see AXIS_MODE). Only stretched
            // when the passband would otherwise run off an edge -- and never
            // in AM / FM, where no outline is drawn and the radio's own box
            // simply fills with audio.
            if (this._isCarrierCentred(this._state.mode)) {
                return { lo: 0, hi: RADIO_SPAN_HZ };
            }
            return {
                lo: Math.min(0, pbLo - margin),
                hi: Math.max(RADIO_SPAN_HZ, pbHi + margin),
            };
        }

        // 'zoom': axis tracks the current passband with margin on each
        // side, so the trapezium fills most of the canvas at every IF
        // Width. This makes the contour / notch / APF markers
        // proportionally bigger and easier to read at narrow filters
        // (e.g. 300 Hz CW) where the trapezium previously occupied only
        // ~10% of the canvas. The labels adapt to whatever range we're
        // showing.
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

    /**
     * The filter settings this panel is drawing from, for anyone else who
     * needs the same numbers — the SDR spectrum panel uses them to work out
     * how far the radio has slid its LO (see sdr/if-out-offset.js). The width
     * is the DSP width alone, NOT clamped to the roofing filter: it is the SH
     * setting that moves the LO, whatever the roofing filter is doing.
     * @returns {{mode: string, ifWidthCode: number, ifWidthHz: number|null, ifShiftHz: number, cwPitchHz: number, dataShiftHz: number}}
     */
    getFilterState() {
        return {
            mode:        this._state.mode || '',
            ifWidthCode: parseInt(this._state.ifWidthCode) || 0,
            ifWidthHz:   this._dspWidthHz(),
            ifShiftHz:   this._state.ifShiftHz || 0,
            cwPitchHz:   this._cwPitchHz(),
            dataShiftHz: this._dataCentreHz(),
        };
    }

    _hzToX(hz, W, loHz, hiHz) {
        return Math.round(((hz - loHz) / (hiHz - loHz)) * W);
    }

    _ifWidthHz() {
        // The trapezium is the DSP filter, as on the radio's own display.
        // This used to clamp to the roofing filter when that was narrower,
        // which was defensible while the bars were invented: it showed the
        // width actually reaching the DSP. Now the bars are the receiver's
        // real audio the roofing filter shows itself -- a 300 Hz roofing
        // filter behind a 2.9 kHz IF WIDTH draws as a 300 Hz hump of band
        // noise in the middle of a wide trapezium, exactly what the radio
        // shows (Colin, 2026-09-19). Clamping hid that: it drew a 300 Hz
        // box round the flat top of the hump and nothing either side. The
        // "Roof" label still says which roofing filter is in.
        return this._dspWidthHz();
    }

    // The DSP (IF WIDTH) bandwidth in Hz, before any roofing-filter clamp.
    _dspWidthHz() {
        // AM and FM have no WIDTH control: the FTdx101 operating manual
        // fixes AM at 9000 Hz, AM-N at 6000, FM and DATA-FM at 16000 and the
        // narrow FM variants at 9000. The SH code the radio still reports in
        // these modes is whatever the last mode left behind, and it used to
        // fall through to the SSB table -- so AM after CW drew a 400 Hz
        // sliver (seen on the '101MP, 2026-09-19, #166).
        const mode = (this._state.mode || '').toUpperCase();
        if (mode === 'AM') return 9000;
        if (mode === 'AM-N') return 6000;
        if (mode === 'FM' || mode === 'DATA-FM') return 16000;
        if (mode === 'FM-N' || mode === 'DATA-FM-N') return 9000;
        // Prefer the mode-aware lookup so the passband matches what the radio
        // is actually doing in the current mode (CW code 8 = 400 Hz, SSB
        // code 8 = 1650 Hz on the FTdx101 etc.). Falls back to the static
        // SSB table if the mode-aware module is unavailable.
        let hz = null;
        if (window.IfWidth) {
            hz = window.IfWidth.ifWidthHzFor(this._model, this._state.mode, parseInt(this._state.ifWidthCode));
        }
        if (hz == null) hz = this._widthTable[String(this._state.ifWidthCode)] || 3000;
        return hz;
    }

    // CW sidetone pitch in Hz, guarded so a missing or nonsense value falls
    // back to the Yaesu default rather than collapsing the passband to zero.
    _cwPitchHz() {
        const v = Number(this._state.cwPitchHz);
        return Number.isFinite(v) && v > 0 ? v : 700;
    }

    // AM and FM: fixed-width filters about the carrier, which IF SHIFT does
    // not move. The radio's own filter function display draws nothing but
    // the audio bars in these modes -- no trapezium, no shift arrow
    // (FTdx101MP screen, 2026-09-19).
    _isCarrierCentred(mode) {
        const m = (mode || '').toUpperCase();
        return m === 'AM' || m === 'AM-N' || m.includes('FM');
    }

    // Audio frequency the RTTY filter is centred on at zero IF SHIFT: the
    // midpoint of mark and space, both of which are above mark in audio.
    _rttyAudioCentreHz() {
        const mark  = Number(this._state.rttyMarkHz);
        const shift = Number(this._state.rttyShiftHz);
        return (Number.isFinite(mark) && mark > 0 ? mark : 2125)
             + (Number.isFinite(shift) && shift > 0 ? shift : 170) / 2;
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

    // DATA SHIFT (SSB) in Hz, or 1500 when it has not been read.
    _dataCentreHz() {
        const ds = Number(this._state.dataShiftHz);
        return Number.isFinite(ds) && ds >= 0 && ds <= 3000 ? ds : 1500;
    }

    // Returns { lo, hi } passband edges in audio Hz
    _passbandEdges(ifWidthHz) {
        const mode = (this._state.mode || '').toUpperCase();
        const shift = this._state.ifShiftHz || 0;
        if (mode.startsWith('CW')) {
            // Narrow CW filters sit centred on the pitch. A wide one cannot
            // -- 3.5 kHz about a 700 Hz pitch would reach -1050 Hz -- and
            // measured on an FTdx101MP on 2026-09-19 (all 21 SH codes, pitch
            // 700) the radio pins the low edge at about 250 Hz once the
            // centred passband would cross it, and grows upward from there:
            // 500 -> 386..1011 (centred), 800 -> 254..1270, 2000 -> 263..2368,
            // 3500 -> 205..2816 (that top is the 3 kHz roofing filter, which
            // the bars show; the trapezium stays the DSP width, as on the
            // radio's own display).
            const lo = Math.max(250, this._cwPitchHz() - ifWidthHz / 2) + shift;
            return { lo, hi: lo + ifWidthHz };
        } else if (this._isCarrierCentred(mode)) {
            // AM and FM are detected about the carrier, so the audio
            // passband runs from the carrier out to half the IF width. IF
            // SHIFT is deliberately NOT applied: measured on an FTdx101MP on
            // 2026-09-19 (#166) -- parked 5 kHz off a broadcast carrier,
            // +1000 and -1000 sound identical, and the radio's own display
            // shows no shift in AM. The knob still turns and CAT still
            // reports a value, but the filter does not move. The SDR
            // spectrum panel mirrors these edges about the dial for its
            // overlay; this panel draws no outline in these modes (_draw).
            return { lo: 0, hi: ifWidthHz / 2 };
        } else if (mode.startsWith('RTTY')) {
            // The radio's own RTTY-L / RTTY-U. Measured on an FTdx101MP on
            // 2026-09-24 (#178) from band noise at MARK 2125 / SHIFT 170: the
            // filter sits on the midpoint of the two tones, 2210 Hz, not on
            // the 1500 Hz IF centre the SSB branch below uses -- 300 ->
            // 2062..2350, 500 -> 1969..2438, 800 -> 1811..2578, the same in
            // both modes. From 1200 Hz up the top stops near 2750 Hz while the
            // bottom keeps going down (1200 -> 1617..2725, 2000 ->
            // 1219..2777, 3000 -> 721..2754). IF SHIFT moves it one for one
            // (+/-300 -> centre 2499 / 1907). The centre is taken as mark +
            // shift/2 for other MARK / SHIFT settings; only the default was
            // measured, and so was only POLARITY-RX NOR.
            const centre = this._rttyAudioCentreHz() + shift;
            const lo = centre - ifWidthHz / 2;
            const hi = Math.max(lo + 50, Math.min(centre + ifWidthHz / 2, RTTY_AUDIO_TOP_HZ));
            return { lo, hi };
        } else if (mode === 'DATA-L' || mode === 'DATA-U') {
            // DATA-L / DATA-U. Measured on an FTdx101MP on 2026-09-24 (#172)
            // from band noise with DATA SHIFT (SSB) at 1000, every SH code:
            // the filter is centred on the DATA SHIFT, not on SSB's 1500,
            // at the CW-column width (code 9 -> 785..1213, 12 -> 609..1395,
            // 13 -> 422..1588). Wider ones keep their top at DATA SHIFT +
            // width/2 (2000 -> ..1986, 3000 -> ..2467, 3500 -> ..2701) and
            // lose their bottom below about 160 Hz. Swept again at the
            // default 1500: centred on 1500 to 2400 Hz wide (1200 ->
            // 908..2086, 2400 -> 334..2648), then 176..2883 at 3000 and
            // about 2980 at the top from 3200 up. IF shift is taken to add
            // one for one, as it does to the slide.
            const centre = this._dataCentreHz() + shift;
            const lo = Math.max(DATA_AUDIO_BOTTOM_HZ, centre - ifWidthHz / 2);
            return { lo, hi: Math.max(lo + 50, Math.min(centre + ifWidthHz / 2, DATA_AUDIO_TOP_HZ)) };
        } else {
            // SSB. Measured on an FTdx101MP on 2026-09-19 by sweeping
            // every SH width code and reading the receiver's audio spectrum
            // (the same feed that draws the bars): the passband does NOT
            // start at 300 Hz and grow upward, which is what this used to
            // draw and was only right at the 3 kHz default. Two regimes:
            //   - 850 Hz and below sit symmetrically about 1500 Hz -- the
            //     IF centre, where the roofing filter also lands
            //     (300 -> 1314..1713, 850 -> 1071..1927).
            //   - 1100 Hz and above narrow from the ~100..3100 default,
            //     taking about 30% off the low side and 70% off the high
            //     (2400 -> 286..2692, 1950 -> 444..2383, 1100 -> 700..1809).
            // Other models are assumed to do the same until measured.
            if (ifWidthHz < 1000) {
                const centre = 1500 + shift;
                return { lo: centre - ifWidthHz / 2, hi: centre + ifWidthHz / 2 };
            }
            const trimmed = Math.max(0, 3000 - ifWidthHz);
            const lo = 100 + trimmed * 0.3;
            return { lo: lo + shift, hi: lo + shift + ifWidthHz };
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

        // AM / FM: the filter is fixed and wider than the box, and the
        // radio's own display shows only the audio bars, edge to edge. Match
        // it: no trapezium, no shift arrow, bars across the whole span.
        const carrierCentred = this._isCarrierCentred(this._state.mode);

        // Trapezoid path: wider at bottom, narrower at top (filter roll-off shape)
        const trapPath = () => {
            ctx.beginPath();
            if (carrierCentred) {
                ctx.rect(0, pbTop, W, pbBot - pbTop);
                return;
            }
            ctx.moveTo(pxLo,           pbBot);
            ctx.lineTo(pxHi,           pbBot);
            ctx.lineTo(pxHi - slopeW,  pbTop);
            ctx.lineTo(pxLo + slopeW,  pbTop);
            ctx.closePath();
        };

        // Subtle fill inside the trapezoid
        if (!carrierCentred) {
            trapPath();
            ctx.fillStyle = 'rgba(74,138,191,0.10)';
            ctx.fill();
        }

        // Signal bars, clipped to the trapezoid -- drawn only when Remote
        // Audio is attached and actually delivering RX spectrum. There is no
        // decorative fallback: this panel is beside a real receiver, and
        // anything drawn in here is read as what the receiver is hearing.
        const provider = this._activeSpectrumProvider();
        const spectrum = provider ? provider() : null;
        const binCount = spectrum ? spectrum.data.length : 0;

        if (spectrum && binCount > 0) {
            ctx.save();
            trapPath();
            ctx.clip();

            const barW     = 2;
            const maxBarH  = Math.floor((pbBot - pbTop) * 0.85);
            const barBase  = pbBot - 1;
            const hzPerBin = spectrum.sampleRate / spectrum.fftSize;

            const barLo = carrierCentred ? 0 : pxLo;
            const barHi = carrierCentred ? W : pxHi;
            for (let bx = barLo; bx <= barHi; bx += barW) {
                const hz = rangeLo + ((bx + barW * 0.5) / W) * rangeHz;
                const bin = Math.max(0, Math.min(binCount - 1, Math.round(hz / hzPerBin)));
                const nh = spectrum.data[bin] / 255;
                const bh = Math.max(2, Math.round(nh * maxBarH));
                ctx.fillStyle = `rgba(80,210,80,${(0.4 + nh * 0.5).toFixed(2)})`;
                ctx.fillRect(bx, barBase - bh, barW - 1, bh);
            }

            ctx.restore();
        }

        // Red trapezoid border — all sides
        if (!carrierCentred) {
            trapPath();
            ctx.strokeStyle = '#e83535';
            ctx.lineWidth   = 1.5;
            ctx.stroke();
        }

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
            const cwCentre = this._cwPitchHz() + (this._state.ifShiftHz || 0);
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
        if (!carrierCentred && Math.abs(shift) > 50) {
            const m = (this._state.mode || '').toUpperCase();
            const zeroShiftHz = m.startsWith('RTTY') ? this._rttyAudioCentreHz()
                : m.startsWith('DATA') ? this._dataCentreHz() : 1500;
            const arrowX = x(zeroShiftHz + shift);
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
