// Yaesu Web Control – Spectrum Panel
// UI module — DOM access is intentional and correct here.
// Owns a single <canvas> element that is divided into two rendering zones:
//   Top 45%  — spectrum trace (line graph of dBFS vs frequency)
//   Bottom 55% — waterfall  (scrolling time–frequency heatmap)
//
// Frequency axis labels are computed from the VFO frequency reported by
// SdrSpectrumPipeline so the display is always centred on the current band.

import { modeForHz } from '../ui/band-plan.js';

export class SpectrumPanel {

    /**
     * @param {string} canvasId       ID of the <canvas> element to render into.
     * @param {string} containerId    ID of the wrapper element to show/hide.
     * @param {number} initialVfoHz   Starting VFO frequency in Hz.
     * @param {string} vfo            "A" or "B" — which VFO this panel represents.
     *                                Click-to-tune and wheel-tune target the
     *                                /api/cat/frequency/{a|b} endpoint accordingly.
     */
    constructor(canvasId, containerId, initialVfoHz = 14_074_000, vfo = 'A') {
        this._canvasId    = canvasId;
        this._containerId = containerId;
        this._vfo         = (vfo === 'B') ? 'B' : 'A';   // normalise + default
        this._vfoLower    = this._vfo.toLowerCase();      // "a"/"b" for URL paths
        this._vfoHz       = initialVfoHz;
        this._status      = 'unconfigured';

        // Waterfall state: ImageData that is scrolled down one row per frame.
        this._waterfallData = null;
        this._waterfallRows = 0;
        this._waterfallCols = 0;

        this._errorDetail = null;

        // Last received spectrum data; held so the canvas can be redrawn on resize.
        this._lastBins    = null;
        this._lastCentreHz = 0;
        this._lastSpanHz   = 0;

        // DX cluster spots overlaid on the spectrum. Each entry is the JSON
        // shape from /api/dxcluster/spots — { callsign, frequencyHz, spotter,
        // comment, receivedUtc }. Only spots within the current span are
        // drawn; clicks within a few pixels of a spot QSY to it precisely.
        this._spots = [];

        // DX cluster connection status — shown as a small badge in the
        // top-right of the spectrum canvas. Updated by setDxStatus().
        this._dxStatus = 'off';
        this._dxDetail = '';

        // Crosshair state — null when mouse is outside the canvas.
        this._crosshairX  = null;
        this._crosshairY  = null;

        // Persistent cursor: a "bookmarked" frequency the operator dropped
        // with Shift+click. Stays on the spectrum until cleared even as
        // the user tunes elsewhere — useful for marking a station you want
        // to come back to. null when no cursor is set.
        this._pinnedCursorHz = null;

        // Band-plan segment data for marker overlay (CW / FT8 / SSB / RTTY etc.
        // tick marks under the spectrum). Set via setBandPlan(); shape is the
        // region-specific subset of BAND_PLANS, e.g. BAND_PLANS.Region1.
        this._bandPlan = null;

        this._resizeObserver = null;
        this._init();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /** Update the spectrum/waterfall with a new frame of FFT data. */
    update({ bins, centreHz, spanHz }) {
        // Hold mode (set via setHold(true)) freezes the display at the
        // last received frame so the operator can inspect a fleeting signal
        // without it scrolling off the waterfall. Incoming frames are
        // dropped — _lastBins / _lastCentreHz / _lastSpanHz are not
        // updated so a forced re-render shows the frozen frame.
        if (this._hold) return;
        this._lastBins    = bins;
        this._lastCentreHz = centreHz;
        this._lastSpanHz   = spanHz;
        this._render();
    }

    /**
     * Freeze (true) or resume (false) the display. While held the spectrum
     * and waterfall stop scrolling and the panel ignores incoming SDR frames.
     * Click-to-tune, wheel-tune, and cursor tracking still work — only
     * automatic updates are paused.
     * @param {boolean} hold
     */
    setHold(hold) {
        this._hold = !!hold;
        // Force a re-render so the "Held" overlay (if any) shows immediately.
        if (this._lastBins) this._render();
    }

    /** Returns the current hold state (true = frozen, false = streaming live). */
    isHeld() { return !!this._hold; }

    /** Store the latest error detail string for display alongside status overlays. */
    setError(detail) {
        this._errorDetail = detail;
    }

    /**
     * Update the DX cluster connection status. Drives the small badge in
     * the top-right of the spectrum canvas.
     * @param {string} status  "off" | "connecting" | "connected" | "disconnected"
     * @param {string} detail  Optional human-readable detail / error message
     */
    setDxStatus(status, detail) {
        this._dxStatus = status || 'off';
        this._dxDetail = detail || '';
        if (this._lastBins) this._render();
    }

    /**
     * Provide the region-specific band plan so the spectrum can draw marker
     * ticks at the standard CW / FT8 / SSB / RTTY activity centres. The data
     * is the region subset of BAND_PLANS (e.g. BAND_PLANS.Region1), keyed by
     * band name → { CW: {freq, label}, FT8: ..., ... }.
     */
    setBandPlan(planForRegion) {
        this._bandPlan = planForRegion || null;
        if (this._lastBins) this._render();
    }

    /**
     * Provide region-specific band edges (lo/hi frequency limits per band).
     * Used to draw the red dashed guard-rail lines on the spectrum.
     * Falls back to the class-static SpectrumPanel.BAND_EDGES (worldwide
     * broadest envelope) if not set.
     * @param {Array<{name:string, lo:number, hi:number}>} edgesForRegion
     */
    setBandEdges(edgesForRegion) {
        this._bandEdges = Array.isArray(edgesForRegion) ? edgesForRegion : null;
        if (this._lastBins) this._render();
    }

    /**
     * Force a re-render with the existing spectrum/state — used when an
     * external setting (e.g. the "only watched" filter toggle in the DX Watch
     * popup) changes and we want the overlay to update immediately rather
     * than wait for the next FFT frame.
     */
    redraw() {
        if (this._lastBins) this._render();
    }

    /** Replace the full DX spots array (used on page load when fetching /api/dxcluster/spots). */
    setSpots(spots) {
        this._spots = Array.isArray(spots) ? spots.slice() : [];
        if (this._lastBins) this._render();
    }

    /** Add or update a single spot — used when SignalR pushes a new DxSpot. */
    addSpot(spot) {
        if (!spot || !spot.callsign) return;
        // De-duplicate by callsign + frequency: a re-spot replaces the older entry.
        const i = this._spots.findIndex(s =>
            s.callsign === spot.callsign && Math.abs(s.frequencyHz - spot.frequencyHz) < 100);
        if (i >= 0) this._spots[i] = spot;
        else        this._spots.unshift(spot);
        // Cap memory: don't accumulate more than 500 spots client-side.
        if (this._spots.length > 500) this._spots.length = 500;
        if (this._lastBins) this._render();
    }

    /** Receive the current VFO frequency so the axis labels stay accurate. */
    setVfoFrequency(hz) {
        this._vfoHz = hz;
        if (this._lastBins) this._render();
        // Keep data-reading on the canvas current so the hover live region can announce it.
        const canvas = document.getElementById(this._canvasId);
        if (canvas) canvas.dataset.reading = 'centred on ' + (hz / 1e6).toFixed(6) + ' MHz';
    }

    /**
     * Respond to SDR lifecycle state changes.
     * @param {string} status  One of: "unconfigured" | "connecting" | "streaming"
     *                                 | "disconnected" | "nodll"
     */
    setStatus(status) {
        this._status = status;

        const container = document.getElementById(this._containerId);
        if (!container) return;

        if (status === 'unconfigured') {
            container.style.display = 'none';
            return;
        }

        container.style.display = '';
        this._drawStatusOverlay(status);
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    _init() {
        const container = document.getElementById(this._containerId);
        if (container) container.style.display = 'none';   // hidden until streaming

        const canvas = document.getElementById(this._canvasId);
        if (!canvas) return;

        // Size the canvas to match its CSS layout width.
        this._sizeCanvas(canvas);

        // Rebuild waterfall buffer whenever the canvas is resized.
        this._resizeObserver = new ResizeObserver(() => {
            this._sizeCanvas(canvas);
            this._waterfallData = null;  // force rebuild on next frame
            if (this._lastBins) this._render();
        });
        this._resizeObserver.observe(canvas.parentElement ?? canvas);

        // Tune VFO A to the clicked frequency.
        canvas.addEventListener('click', (e) => this._onCanvasClick(e));

        // Mouse-wheel tunes VFO A up/down in 1 kHz steps.
        // { passive: false } required so preventDefault() suppresses page scroll.
        canvas.addEventListener('wheel', (e) => this._onCanvasWheel(e), { passive: false });

        // Crosshair tracking.
        canvas.addEventListener('mousemove', (e) => {
            const rect = canvas.getBoundingClientRect();
            this._crosshairX = (e.clientX - rect.left) * (canvas.width  / rect.width);
            this._crosshairY = (e.clientY - rect.top)  * (canvas.height / rect.height);
            if (this._lastBins) this._render();
            // Announce cursor frequency to screen readers via a live region (debounced to 1 s).
            if (this._lastSpanHz > 0 && this._vfoHz > 0) {
                clearTimeout(this._announceTimer);
                this._announceTimer = setTimeout(() => {
                    const canvas2 = document.getElementById(this._canvasId);
                    if (!canvas2) return;
                    const W = canvas2.width;
                    const leftHz = this._vfoHz - this._lastSpanHz / 2;
                    const cx = this._crosshairX;
                    if (cx == null) return;
                    const freqHz = leftHz + (cx / W) * this._lastSpanHz;
                    const freqLabel = (freqHz / 1e6).toFixed(6) + ' MHz';
                    this._announceToScreenReader('Spectrum cursor at ' + freqLabel);
                }, 1000);
            }
        });
        canvas.addEventListener('mouseleave', () => {
            this._crosshairX = null;
            this._crosshairY = null;
            clearTimeout(this._announceTimer);
            if (this._lastBins) this._render();
        });

        canvas.style.cursor = 'crosshair';
    }

    _onCanvasClick(e) {
        if (!this._lastBins || this._lastSpanHz <= 0 || this._vfoHz <= 0) return;

        const canvas = document.getElementById(this._canvasId);
        const rect   = canvas.getBoundingClientRect();
        const x      = e.clientX - rect.left;
        const W      = canvas.width;

        // Click-to-tune is active across the whole panel — both the live
        // spectrum (top ~45%) and the waterfall (bottom ~55%). Clicking a
        // signal trail in the waterfall QSYs to that column's frequency.

        // Convert canvas-relative x (CSS pixels) to canvas-internal pixels.
        const canvasX = x * (W / rect.width);
        const leftHz  = this._vfoHz - this._lastSpanHz / 2;
        const clickHz = Math.round(leftHz + (canvasX / W) * this._lastSpanHz);

        // Shift+click → drop / toggle a persistent cursor at the click freq
        // instead of tuning. The operator can mark a station to come back to
        // while tuning around with normal clicks.
        if (e.shiftKey) {
            // If shift-click lands within 8 internal-pixels of the existing
            // pinned cursor, treat that as "remove the cursor". Otherwise
            // (re-)pin at the click frequency.
            if (this._pinnedCursorHz != null) {
                const px = ((this._pinnedCursorHz - leftHz) / this._lastSpanHz) * W;
                if (Math.abs(px - canvasX) <= 8) {
                    this._pinnedCursorHz = null;
                    if (this._lastBins) this._render();
                    return;
                }
            }
            this._pinnedCursorHz = clickHz;
            if (this._lastBins) this._render();
            return;
        }

        // If the click is within 8 internal-pixels of a spot's marker, snap
        // to that spot's exact frequency. Otherwise tune to the click x.
        let targetHz = clickHz;
        for (const s of this._spots) {
            const sx = ((s.frequencyHz - leftHz) / this._lastSpanHz) * W;
            if (Math.abs(sx - canvasX) <= 8) {
                targetHz = s.frequencyHz;
                break;
            }
        }

        fetch(`/api/cat/frequency/${this._vfoLower}`, {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify({ frequencyHz: targetHz }),
        }).catch(() => { /* ignore network errors */ });

        // Follow the click with a best-guess mode change. Most operators expect
        // jumping from 14.074 (FT8) to 14.284 (SSB) to also flip the radio to
        // USB rather than leave it stuck in DATA-U. window.setMode is defined
        // by site.js and uses the same CAT path the mode buttons use.
        const targetMode = modeForHz(targetHz);
        if (targetMode && window.setMode) {
            try { window.setMode(this._vfo, targetMode); } catch { /* ignore */ }
        }
    }

    _onCanvasWheel(e) {
        e.preventDefault();   // stop the page from scrolling
        if (!this._lastBins || this._vfoHz <= 0) return;

        // 1 kHz per notch — accumulate on _wheelTargetHz so rapid scrolling
        // compounds correctly before the radio confirms the new frequency.
        const step = 1000;
        const direction = e.deltaY > 0 ? -1 : 1;   // scroll up = higher freq
        this._wheelTargetHz = Math.max(30_000, Math.min(75_000_000,
            (this._wheelTargetHz ?? this._vfoHz) + direction * step));

        // Debounce: send once scrolling pauses for 60 ms.
        clearTimeout(this._wheelTimer);
        this._wheelTimer = setTimeout(() => {
            const hz = this._wheelTargetHz;
            this._wheelTargetHz = null;
            fetch(`/api/cat/frequency/${this._vfoLower}`, {
                method:  'POST',
                headers: { 'Content-Type': 'application/json' },
                body:    JSON.stringify({ frequencyHz: hz }),
            }).catch(() => {});
        }, 60);
    }

    _sizeCanvas(canvas) {
        const w = canvas.parentElement
            ? canvas.parentElement.clientWidth || 800
            : 800;
        canvas.width  = w;
        canvas.height = 280;   // 126px spectrum + 154px waterfall
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    _render() {
        const canvas = document.getElementById(this._canvasId);
        if (!canvas || !this._lastBins) return;

        const ctx         = canvas.getContext('2d');
        const W           = canvas.width;
        const H           = canvas.height;
        const specH       = Math.floor(H * 0.45);
        const wfH         = H - specH;
        const bins        = this._lastBins;
        const centreHz    = this._lastCentreHz;
        const spanHz      = this._lastSpanHz;

        this._drawSpectrum(ctx, bins, W, specH);
        this._drawFrequencyAxis(ctx, bins, W, specH, centreHz, spanHz);
        this._drawBandEdges(ctx, W, specH);
        this._drawBandMarkers(ctx, W, specH);
        this._drawSpots(ctx, W, specH);
        this._drawDxBadge(ctx, W);
        this._scrollWaterfall(ctx, bins, W, specH, wfH);
        this._drawPinnedCursor(ctx, W, specH);
        this._drawCrosshair(ctx, W, specH, spanHz);
        this._drawHoldOverlay(ctx, W, specH);
    }

    // ── Persistent (pinned) cursor ───────────────────────────────────────────
    //
    // A "bookmark" cursor the operator dropped with Shift+click. Distinct
    // visual from the live mouse crosshair: solid cyan vertical line plus a
    // boxed frequency label so it stands out. Shift+click on or very near
    // the existing cursor clears it.
    _drawPinnedCursor(ctx, W, specH) {
        if (this._pinnedCursorHz == null || this._lastSpanHz <= 0 || this._vfoHz <= 0) return;
        const leftHz = this._vfoHz - this._lastSpanHz / 2;
        const rightHz = this._vfoHz + this._lastSpanHz / 2;
        if (this._pinnedCursorHz < leftHz || this._pinnedCursorHz > rightHz) return;

        const x = ((this._pinnedCursorHz - leftHz) / this._lastSpanHz) * W;
        const axisH = 20;
        const specTop = specH - axisH;

        ctx.save();

        // Solid cyan vertical line spanning the whole panel (spectrum + waterfall)
        // so the marker is visible even when the operator is studying the waterfall.
        ctx.strokeStyle = '#00d4ff';
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.moveTo(x + 0.5, 0);
        ctx.lineTo(x + 0.5, specH);
        ctx.stroke();

        // Frequency label in a boxed background near the top of the spectrum.
        const label = (this._pinnedCursorHz / 1e6).toFixed(6) + ' MHz';
        ctx.font = 'bold 11px monospace';
        const padX = 4, padY = 2;
        const textWidth = ctx.measureText(label).width;
        const boxW = textWidth + padX * 2;
        const boxH = 16;
        // Prefer the label to the right of the cursor; flip to left near the right edge.
        const boxX = (x + boxW + 8 < W) ? x + 4 : x - boxW - 4;
        const boxY = 24;

        ctx.fillStyle = 'rgba(0, 60, 90, 0.9)';
        ctx.fillRect(boxX, boxY, boxW, boxH);
        ctx.strokeStyle = '#00d4ff';
        ctx.lineWidth = 1;
        ctx.strokeRect(boxX + 0.5, boxY + 0.5, boxW - 1, boxH - 1);

        ctx.fillStyle = '#ffffff';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'top';
        ctx.fillText(label, boxX + padX, boxY + padY);

        ctx.restore();
    }

    // ── Hold overlay ─────────────────────────────────────────────────────────
    //
    // When setHold(true) freezes the spectrum, paint a subtle banner so the
    // operator sees the display isn't live. The status badge in the panel
    // header also says "Hold" (yellow), and the canvas is intentionally
    // not faded — operators want to study what's frozen, not look at
    // greyed-out data.
    _drawHoldOverlay(ctx, W, specH) {
        if (!this._hold) return;
        ctx.save();
        ctx.font         = 'bold 12px sans-serif';
        ctx.textAlign    = 'left';
        ctx.textBaseline = 'top';
        const label = 'HOLD';
        const padX = 6, padY = 3;
        const textWidth = ctx.measureText(label).width;
        ctx.fillStyle = 'rgba(200, 140, 0, 0.85)';
        ctx.fillRect(4, 4, textWidth + padX * 2, 20);
        ctx.fillStyle = '#000000';
        ctx.fillText(label, 4 + padX, 4 + padY);
        ctx.restore();
    }

    // ── Band-edge guard rails ────────────────────────────────────────────────
    // Vertical red lines at the lower and upper edges of each amateur band
    // visible in the current spectrum window. Makes it visually obvious if
    // the operator tunes (deliberately or accidentally) outside the amateur
    // allocation. Edges are the broadest amateur envelopes (worldwide), so a
    // line at e.g. 7.300 MHz on 40m might be lenient in a Region 1 country
    // where the band ends at 7.200 — but it's never wrong (no transmission
    // is legal beyond these limits in any region).
    _drawBandEdges(ctx, W, specH) {
        if (this._lastSpanHz <= 0 || this._vfoHz <= 0) return;
        const leftHz  = this._vfoHz - this._lastSpanHz / 2;
        const rightHz = this._vfoHz + this._lastSpanHz / 2;

        // Per-region edges (set by Index.cshtml from BAND_EDGES[region]) take
        // priority over the class-static worldwide envelope. Fall back if no
        // region-specific data has been supplied.
        const edges = this._bandEdges ?? SpectrumPanel.BAND_EDGES;

        ctx.save();
        ctx.strokeStyle = '#ff4040';
        ctx.lineWidth   = 1.5;
        ctx.setLineDash([4, 3]);   // dashed so it's clearly a "guard" not a "marker"

        for (const edge of edges) {
            for (const hz of [edge.lo, edge.hi]) {
                if (hz < leftHz || hz > rightHz) continue;
                const x = ((hz - leftHz) / this._lastSpanHz) * W;
                ctx.beginPath();
                ctx.moveTo(x, 0);
                ctx.lineTo(x, specH - 2);
                ctx.stroke();
            }
        }
        ctx.restore();
    }

    // ── Band-plan marker ticks ───────────────────────────────────────────────
    // Vertical ticks at the standard CW / FT8 / FT4 / RTTY / SSB activity
    // frequencies for the current region's band plan. Helps orient newer
    // operators who don't yet have the watering holes memorised.
    //
    // Labels are staggered across up to three rows when they would overlap
    // (e.g. FT8 at 14.074 and RTTY at 14.080 are only 6 kHz apart and would
    // collide at any reasonable span).
    _drawBandMarkers(ctx, W, specH) {
        if (!this._bandPlan || this._lastSpanHz <= 0 || this._vfoHz <= 0) return;

        const leftHz  = this._vfoHz - this._lastSpanHz / 2;
        const rightHz = this._vfoHz + this._lastSpanHz / 2;

        // Collect all in-window markers with their pixel x and label width.
        const markers = [];
        for (const bandName in this._bandPlan) {
            const segments = this._bandPlan[bandName];
            for (const segKey in segments) {
                const seg = segments[segKey];
                if (!seg || typeof seg.freq !== 'number') continue;
                if (seg.freq < leftHz || seg.freq > rightHz) continue;
                const x     = ((seg.freq - leftHz) / this._lastSpanHz) * W;
                const label = seg.label || segKey;
                markers.push({ x, label });
            }
        }
        if (markers.length === 0) return;
        markers.sort((a, b) => a.x - b.x);

        ctx.save();
        // Band-plan segment marker font: bumped from 11px → 13px for
        // accessibility (Colin 2026-06-13 — the original size required a
        // magnifying glass to read the FT8/CW/SSB labels above the
        // spectrum).
        ctx.font        = '13px sans-serif';
        ctx.textAlign   = 'center';
        ctx.lineWidth   = 1;
        ctx.strokeStyle = '#80c0ff';
        ctx.fillStyle   = '#80c0ff';

        // Stagger labels across up to three rows so closely-spaced markers
        // (FT8 + RTTY on 20m being the worst case) remain readable.
        const rowGap       = 12;   // vertical spacing between label rows
        const maxRows      = 3;
        const minLabelGap  = 4;
        const rowRightEdge = new Array(maxRows).fill(-Infinity);

        for (const m of markers) {
            const halfWidth = ctx.measureText(m.label).width / 2;
            const leftEdge  = m.x - halfWidth;
            const rightEdge = m.x + halfWidth;

            let row = 0;
            for (let r = 0; r < maxRows; r++) {
                if (leftEdge >= rowRightEdge[r] + minLabelGap) { row = r; break; }
                row = r;
            }
            rowRightEdge[row] = rightEdge;

            // Tick mark — same position regardless of which row the label is on.
            ctx.beginPath();
            ctx.moveTo(m.x, specH - 18);
            ctx.lineTo(m.x, specH - 4);
            ctx.stroke();

            // Label — rendered above the tick, pushed up by `row` × rowGap
            // so overlapping labels stack vertically instead of overwriting.
            const labelY = specH - 20 - row * rowGap;
            ctx.fillText(m.label, m.x, labelY);
        }
        ctx.restore();
    }

    // ── DX cluster status badge ──────────────────────────────────────────────
    _drawDxBadge(ctx, W) {
        const label = `DX: ${this._dxStatus}`;
        let bg, fg;
        switch (this._dxStatus) {
            case 'connected':    bg = '#1e7e34'; fg = '#ffffff'; break;
            case 'connecting':   bg = '#b58900'; fg = '#000000'; break;
            case 'disconnected': bg = '#a03030'; fg = '#ffffff'; break;
            default:             bg = '#3a3a3a'; fg = '#aaaaaa'; break;
        }

        ctx.save();
        ctx.font         = 'bold 14px sans-serif';
        ctx.textBaseline = 'top';
        const padX = 8;
        const padY = 5;
        const textWidth = ctx.measureText(label).width;
        const w = textWidth + padX * 2;
        const h = 24;
        const x = W - w - 4;
        const y = 4;
        ctx.fillStyle = bg;
        ctx.fillRect(x, y, w, h);
        ctx.fillStyle = fg;
        ctx.textAlign = 'left';
        ctx.fillText(label, x + padX, y + padY);
        ctx.restore();
    }

    // ── DX cluster spot overlay ──────────────────────────────────────────────
    //
    // Draws a small downward-pointing tick at each spot's frequency with the
    // callsign label above. Spots outside the current span are skipped. When
    // multiple spots fall within ~50 px of each other their labels are
    // staggered vertically so they don't overlap.
    _drawSpots(ctx, W, specH) {
        if (!this._spots.length || this._lastSpanHz <= 0 || this._vfoHz <= 0) return;

        const leftHz  = this._vfoHz - this._lastSpanHz / 2;
        const rightHz = this._vfoHz + this._lastSpanHz / 2;
        // When the user has ticked "Show only watched callsigns" in the DX
        // Watch popup, hide every spot that isn't flagged isWatched. The flag
        // is set by DxClusterService on the backend, so we just respect it.
        const onlyWatched = !!window.dxOnlyWatched;

        // Build a list of (x, spot) for spots in range, sorted left-to-right.
        const drawList = [];
        for (const s of this._spots) {
            if (s.frequencyHz < leftHz || s.frequencyHz > rightHz) continue;
            if (onlyWatched && !s.isWatched) continue;
            const x = ((s.frequencyHz - leftHz) / this._lastSpanHz) * W;
            drawList.push({ x, spot: s });
        }
        if (drawList.length === 0) return;
        drawList.sort((a, b) => a.x - b.x);

        ctx.save();
        ctx.font      = 'bold 13px sans-serif';
        ctx.textAlign = 'center';
        ctx.lineWidth   = 1.5;

        // Stagger labels across multiple rows so they don't overlap. For each
        // candidate label, measure its actual rendered width and find the
        // first row whose previous label's right edge is far enough left.
        // If no row has space, drop the label rather than overlap. Newer
        // spots are drawn last so they win when crowded — drawList is sorted
        // by x but spots are most-recent-first within the same x.
        //
        // Watched callsigns are drawn in bright red instead of yellow so the
        // operator can spot them at a glance amongst the regular cluster
        // traffic.
        const rowHeight    = 16;
        const maxRows      = 5;
        const minLabelGap  = 4;   // px between adjacent labels in the same row
        const rowRightEdge = new Array(maxRows).fill(-Infinity);

        for (const { x, spot } of drawList) {
            const halfWidth = ctx.measureText(spot.callsign).width / 2;
            const leftEdge  = x - halfWidth;
            const rightEdge = x + halfWidth;

            let row = -1;
            for (let r = 0; r < maxRows; r++) {
                if (leftEdge >= rowRightEdge[r] + minLabelGap) { row = r; break; }
            }
            if (row < 0) continue; // crowded — skip this label rather than overlap
            rowRightEdge[row] = rightEdge;
            const labelY = 14 + row * rowHeight;

            // Colour per spot — bright red for watched callsigns, yellow otherwise.
            const colour = spot.isWatched ? '#ff4040' : '#ffcc33';
            ctx.fillStyle   = colour;
            ctx.strokeStyle = colour;

            // Callsign label.
            ctx.fillText(spot.callsign, x, labelY);

            // Tick mark dropping from the label to mid-spectrum.
            const tickTop = labelY + 2;
            const tickBot = tickTop + 6;
            ctx.beginPath();
            ctx.moveTo(x, tickTop);
            ctx.lineTo(x, tickBot);
            ctx.stroke();
        }

        ctx.restore();
    }

    // ── Spectrum trace ───────────────────────────────────────────────────────

    _drawSpectrum(ctx, bins, W, H) {
        const N      = bins.length;
        const dbMin  = -120;
        const dbMax  = 0;
        const range  = dbMax - dbMin;

        // Background
        ctx.fillStyle = '#0a0a14';
        ctx.fillRect(0, 0, W, H);

        // Grid lines at every 20 dB
        ctx.strokeStyle = '#1e2030';
        ctx.lineWidth   = 1;
        for (let db = dbMin; db <= dbMax; db += 20) {
            const y = H - ((db - dbMin) / range) * H;
            ctx.beginPath();
            ctx.moveTo(0, y);
            ctx.lineTo(W, y);
            ctx.stroke();
        }

        // Build the trace path
        ctx.beginPath();
        for (let i = 0; i < N; i++) {
            const x = (i / N) * W;
            const y = H - Math.max(0, Math.min(1, (bins[i] - dbMin) / range)) * H;
            if (i === 0) ctx.moveTo(x, y);
            else         ctx.lineTo(x, y);
        }

        // Close path to the bottom to fill
        ctx.lineTo(W, H);
        ctx.lineTo(0, H);
        ctx.closePath();

        ctx.fillStyle = 'rgba(0, 140, 255, 0.18)';
        ctx.fill();

        // Redraw the outline on top of the fill
        ctx.beginPath();
        for (let i = 0; i < N; i++) {
            const x = (i / N) * W;
            const y = H - Math.max(0, Math.min(1, (bins[i] - dbMin) / range)) * H;
            if (i === 0) ctx.moveTo(x, y);
            else         ctx.lineTo(x, y);
        }
        ctx.strokeStyle = '#00aaff';
        ctx.lineWidth   = 1.5;
        ctx.stroke();

        // dB scale labels (right-aligned). Font bumped from 10px → 12px
        // for accessibility (same complaint, same release: 2026-06-13).
        ctx.fillStyle  = '#667799';
        ctx.font       = '12px monospace';
        ctx.textAlign  = 'right';
        for (let db = dbMin; db <= dbMax; db += 20) {
            const y = H - ((db - dbMin) / range) * H;
            ctx.fillText(`${db} dB`, W - 4, y - 2);
        }

        // Centre frequency label
        if (this._vfoHz > 0) {
            const label = (this._vfoHz / 1e6).toFixed(6) + ' MHz';
            ctx.font      = '12px monospace';
            ctx.fillStyle = '#44aaff';
            ctx.textAlign = 'center';
            ctx.fillText(label, W / 2, 14);
        }
    }

    // ── Frequency axis ───────────────────────────────────────────────────────

    _drawFrequencyAxis(ctx, bins, W, specH, centreHz, spanHz) {
        const axisH  = 20;
        const tickY0 = specH - axisH;       // top of axis strip
        const labelY = specH - 4;           // baseline for text

        ctx.fillStyle = '#111118';
        ctx.fillRect(0, tickY0, W, axisH);

        // VFO centre marker line (drawn first, behind labels)
        ctx.strokeStyle = 'rgba(0, 170, 255, 0.4)';
        ctx.lineWidth   = 1;
        ctx.beginPath();
        ctx.moveTo(W / 2, 0);
        ctx.lineTo(W / 2, tickY0);
        ctx.stroke();

        // Only skip labels when FrequencyA has never been set (C# long default = 0).
        // Any non-zero persisted frequency is treated as valid; FTdx101MP range is 30 kHz–75 MHz.
        if (this._vfoHz <= 0) {
            ctx.fillStyle = '#667799';
            ctx.font = '10px monospace';
            ctx.textAlign = 'center';
            ctx.fillText('No VFO frequency available', W / 2, labelY);
            return;
        }

        // Choose a "nice" tick interval that gives roughly 6–12 ticks across the span.
        // Candidate steps in Hz: 50k, 100k, 200k, 250k, 500k, 1M, 2M, 5M, 10M
        const steps = [50e3, 100e3, 200e3, 250e3, 500e3, 1e6, 2e6, 5e6, 10e6];
        const targetTicks = 8;
        const stepHz = steps.find(s => spanHz / s <= targetTicks) ?? steps[steps.length - 1];

        // First tick at the next multiple of stepHz above the left edge
        const leftHz  = this._vfoHz - spanHz / 2;
        const firstHz = Math.ceil(leftHz / stepHz) * stepHz;

        // Frequency-axis tick label font: bumped from 10px → 13px for
        // accessibility (Colin 2026-06-13 — the MHz numbers under the
        // tick marks were unreadable without a magnifying glass).
        // 13px fits within the existing 20px axisH (label baseline at
        // specH-4 leaves room for glyphs above).
        ctx.font      = '13px monospace';
        ctx.textAlign = 'center';

        for (let tickHz = firstHz; tickHz <= leftHz + spanHz; tickHz += stepHz) {
            const x = ((tickHz - leftHz) / spanHz) * W;

            // Tick line
            const isVfo = Math.abs(tickHz - this._vfoHz) < stepHz * 0.01;
            ctx.strokeStyle = isVfo ? 'rgba(0,170,255,0.8)' : '#334466';
            ctx.lineWidth   = 1;
            ctx.beginPath();
            ctx.moveTo(x, tickY0);
            ctx.lineTo(x, tickY0 + 4);
            ctx.stroke();

            // Label — skip if too close to edge or below 0 Hz
            if (x < 24 || x > W - 24 || tickHz <= 0) continue;

            const mhz    = tickHz / 1e6;
            const label  = mhz.toFixed(6);

            ctx.fillStyle = isVfo ? '#44aaff' : '#8899bb';
            ctx.fillText(label, x, labelY);
        }
    }

    // ── Crosshair overlay ────────────────────────────────────────────────────

    _drawCrosshair(ctx, W, specH, spanHz) {
        if (this._crosshairX === null || this._lastSpanHz <= 0 || this._vfoHz <= 0) return;

        const x = this._crosshairX;
        const y = this._crosshairY;

        // Only draw inside the spectrum area (above the axis strip).
        const axisH = 20;
        const specTop = specH - axisH;
        if (y < 0 || y > specTop) return;

        // Vertical line
        ctx.save();
        ctx.strokeStyle = 'rgba(255, 255, 255, 0.5)';
        ctx.lineWidth   = 1;
        ctx.setLineDash([4, 4]);
        ctx.beginPath();
        ctx.moveTo(x, 0);
        ctx.lineTo(x, specTop);
        ctx.stroke();

        // Horizontal line
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(W, y);
        ctx.stroke();
        ctx.setLineDash([]);

        // Frequency at cursor
        const leftHz  = this._vfoHz - spanHz / 2;
        const freqHz  = leftHz + (x / W) * spanHz;
        const label   = (freqHz / 1e6).toFixed(6) + ' MHz';

        // Crosshair frequency readout font: bumped from 11px → 14px for
        // accessibility (Colin 2026-06-13, same release as the axis-label
        // bumps). Background box height grew from 16px → 20px and the
        // y-offset from -12 to -15 to fit the larger glyphs cleanly.
        ctx.font      = '14px monospace';
        const pad     = 4;
        const tw      = ctx.measureText(label).width;

        // Position label to the right of cursor, flip left near the right edge.
        const lx = (x + tw + pad * 2 + 6 < W) ? x + pad : x - tw - pad * 2;
        const ly = Math.max(17, Math.min(y - pad, specTop - 4));

        ctx.fillStyle = 'rgba(0, 0, 0, 0.6)';
        ctx.fillRect(lx, ly - 15, tw + pad * 2, 20);

        ctx.fillStyle = '#ffffff';
        ctx.textAlign = 'left';
        ctx.fillText(label, lx + pad, ly);

        ctx.restore();
    }

    // ── Waterfall ────────────────────────────────────────────────────────────

    _scrollWaterfall(ctx, bins, W, specH, wfH) {
        // Lazily allocate or reallocate when the canvas size changes.
        if (!this._waterfallData || this._waterfallCols !== W || this._waterfallRows !== wfH) {
            this._waterfallCols = W;
            this._waterfallRows = wfH;
            this._waterfallData = ctx.createImageData(W, wfH);
            // Initialise to black.
            this._waterfallData.data.fill(0);
            for (let p = 3; p < this._waterfallData.data.length; p += 4)
                this._waterfallData.data[p] = 255;   // alpha = 255
        }

        const data = this._waterfallData.data;
        const N    = bins.length;

        // Shift all existing rows down by one pixel (4 bytes per pixel, W pixels per row).
        const rowBytes = W * 4;
        data.copyWithin(rowBytes, 0, data.length - rowBytes);

        // Draw new row at the top.
        for (let x = 0; x < W; x++) {
            const binIdx = Math.floor((x / W) * N);
            const [r, g, b] = SpectrumPanel._dbToColor(bins[binIdx]);
            const p = x * 4;
            data[p + 0] = r;
            data[p + 1] = g;
            data[p + 2] = b;
            data[p + 3] = 255;
        }

        ctx.putImageData(this._waterfallData, 0, specH);
    }

    // ── Status overlay ───────────────────────────────────────────────────────

    _drawStatusOverlay(status) {
        const canvas = document.getElementById(this._canvasId);
        if (!canvas) return;

        const ctx = canvas.getContext('2d');
        const W   = canvas.width;
        const H   = canvas.height;

        if (status === 'streaming') {
            // Clear any previous overlay; the next data frame will paint correctly.
            return;
        }

        // If we have a previous frame and this is just a brief transition
        // (span change, SDR restart), keep the existing spectrum visible
        // instead of wiping to a "connecting…" message. The status badge in
        // the panel header carries the state info; blanking out a working
        // spectrum for a 3-second reconnect is jarring.
        if ((status === 'connecting' || status === 'disconnected') && this._lastBins) {
            this._render();
            return;
        }

        const messages = {
            connecting:   'Connecting to SDR device…',
            disconnected: 'SDR device unavailable — retrying every 5 s',
            nodll:        'SoapySDR.dll not found — install SoapySDR + device driver',
        };

        const line1 = messages[status] ?? `SDR status: ${status}`;
        const line2 = status === 'disconnected' && this._errorDetail
            ? this._errorDetail
            : null;

        ctx.fillStyle = '#0a0a14';
        ctx.fillRect(0, 0, W, H);

        ctx.fillStyle = '#8899bb';
        ctx.textAlign = 'center';

        ctx.font = '14px sans-serif';
        ctx.fillText(line1, W / 2, H / 2 - (line2 ? 10 : 0));

        if (line2) {
            ctx.font      = '11px sans-serif';
            ctx.fillStyle = '#cc6655';
            ctx.fillText(line2, W / 2, H / 2 + 12);
        }
    }

    // ── Accessibility ────────────────────────────────────────────────────────

    /** Announce text to screen readers via the shared ARIA live region in site.js. */
    _announceToScreenReader(text) {
        let lr = document.getElementById('_sr_live');
        if (!lr) {
            // Fallback: create a local live region if site.js hasn't run yet.
            // assertive (matching site.js's primary live region) so new
            // cursor announcements interrupt rather than queue.
            lr = document.createElement('div');
            lr.id = '_sr_live';
            lr.setAttribute('aria-live', 'assertive');
            lr.setAttribute('aria-atomic', 'true');
            lr.style.cssText = 'position:absolute;left:-9999px;width:1px;height:1px;overflow:hidden;';
            document.body.appendChild(lr);
        }
        lr.textContent = '';
        requestAnimationFrame(() => { lr.textContent = text; });
    }

    // ── Color mapping ────────────────────────────────────────────────────────

    /**
     * Maps a dBFS value (−120 … 0) to an RGB thermal colour.
     * Black → blue → cyan → green → yellow → red.
     */
    static _dbToColor(db) {
        const t = Math.max(0, Math.min(1, (db + 120) / 120));
        if (t < 0.2)  return [0,                   0,                   Math.round(t * 5 * 180)];
        if (t < 0.4)  return [0,                   Math.round((t - 0.2) * 5 * 200), 180];
        if (t < 0.6)  return [0,                   200,                 Math.round(180 - (t - 0.4) * 5 * 180)];
        if (t < 0.8)  return [Math.round((t - 0.6) * 5 * 255), 200,    0];
        return               [255,                 Math.round(200 - (t - 0.8) * 5 * 200), 0];
    }
}

// Amateur band envelopes in Hz — the broadest worldwide limits, used to draw
// red guard-rail lines at the edges of each band on the spectrum. These are
// not per-region; a Region-1 operator may see a guard rail at 7.300 MHz where
// the legal limit is actually 7.200, but the inverse is never true (no region
// permits TX beyond these envelopes).
SpectrumPanel.BAND_EDGES = [
    { name: '160m', lo:   1800000, hi:   2000000 },
    { name:  '80m', lo:   3500000, hi:   4000000 },
    { name:  '60m', lo:   5250000, hi:   5450000 },
    { name:  '40m', lo:   7000000, hi:   7300000 },
    { name:  '30m', lo:  10100000, hi:  10150000 },
    { name:  '20m', lo:  14000000, hi:  14350000 },
    { name:  '17m', lo:  18068000, hi:  18168000 },
    { name:  '15m', lo:  21000000, hi:  21450000 },
    { name:  '12m', lo:  24890000, hi:  24990000 },
    { name:  '10m', lo:  28000000, hi:  29700000 },
    { name:   '6m', lo:  50000000, hi:  54000000 },
    { name:   '4m', lo:  70000000, hi:  70500000 },
    { name:   '2m', lo: 144000000, hi: 148000000 },
];
