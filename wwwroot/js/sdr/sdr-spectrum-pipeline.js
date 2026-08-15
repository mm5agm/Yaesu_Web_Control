// Yaesu Web Control – SDR Spectrum Pipeline
// Transport and routing only. No DOM, no canvas, no calibration, no formatting.
//
// Creates its own SignalR connection to /radioHub and dispatches the three
// message types the spectrum display needs:
//   "SpectrumUpdate"  — FFT bin array from SdrManager (v2.3.0+) — now
//                       sdrId-tagged so dual-SDR setups route to the right panel
//   "SdrStatus"       — device lifecycle state, also sdrId-tagged
//   "SdrError"        — error detail, sdrId-tagged
//   "FrequencyA/B"    — VFO frequency changes so the axis labels stay correct
//
// Per-VFO routing for dual-SDR: register handlers with onSpectrumUpdate('A', …)
// / onSpectrumUpdate('B', …). The pipeline filters by sdrId on the value
// envelope before dispatching.
//
// Uses the same { property, value } message envelope as the rest of the app
// so the existing WsUpdatePipeline can be reused for dispatching.

// ?v=1 is a one-time cache-buster, not a number to bump — see gaugeFactory.js.
import { WsUpdatePipeline } from '../websocket/ws-update-pipeline.js?v=1';

export class SdrSpectrumPipeline {

    constructor() {
        this._pipeline   = new WsUpdatePipeline();
        this._connection = null;

        // Per-sdrId handler maps for the three SDR-routed messages.
        // Filled in via onSpectrumUpdate / onStatusChange / onError.
        this._spectrumHandlers = {};   // sdrId → handler(value)
        this._statusHandlers   = {};   // sdrId → handler(statusStr)
        this._errorHandlers    = {};   // sdrId → handler(errorStr)

        // Single bridging handlers that dispatch to the right per-sdrId callback.
        this._pipeline.register('SpectrumUpdate', (value) => {
            if (!value || !value.sdrId) return;
            const h = this._spectrumHandlers[value.sdrId];
            if (h) h(value);
        });
        this._pipeline.register('SdrStatus', (value) => {
            if (!value || !value.sdrId) return;
            const h = this._statusHandlers[value.sdrId];
            if (h) h(value.status);
        });
        this._pipeline.register('SdrError', (value) => {
            if (!value || !value.sdrId) return;
            const h = this._errorHandlers[value.sdrId];
            if (h) h(value.error);
        });
    }

    // ── Public API ─────────────────────────────────────────────────���─────────

    /**
     * Open a SignalR connection and begin routing messages.
     * Safe to call multiple times; subsequent calls are ignored.
     */
    connect() {
        if (this._connection) return;

        const conn = new window.signalR
            .HubConnectionBuilder()
            .withUrl('/radioHub')
            .withAutomaticReconnect()
            .build();

        conn.on('RadioStateUpdate', (msg) => {
            // ServerShutdown: tear down our connection so Kestrel has nothing
            // to drain on server-side shutdown — this matches the matching
            // logic in site.js's main connection handler. Without it, this
            // pipeline's long-lived hub connection alone keeps Kestrel
            // politely waiting for ~5 s on every tray Exit.
            if (msg && msg.property === 'ServerShutdown') {
                try { conn.stop(); } catch { /* may already be closed */ }
                return;
            }
            this._pipeline.handleMessage(msg);
        });
        conn.start()
            .catch(() => { /* reconnect is handled by withAutomaticReconnect */ });

        this._connection = conn;
    }

    /**
     * Close the SignalR connection immediately.
     *
     * Call this on `pagehide` (i.e. when navigating away or being frozen into
     * the bfcache). Without it, the browser keeps the socket half-open while the
     * page is frozen, so the server still counts this page as a live client.
     * When the page returns and a fresh connection forms, the worker's per-frame
     * broadcast (`Clients.All.SendAsync`) back-pressures on that zombie client —
     * spectrum frames are large, its send buffer fills, and frame delivery to
     * the *new* connection stalls for several seconds until the transport reaps
     * the zombie. Stopping here lets the server drop it at once, so the reloaded
     * page starts receiving frames in ~100ms instead of ~7s.
     */
    disconnect() {
        const conn = this._connection;
        this._connection = null;
        if (conn) { try { conn.stop(); } catch { /* may already be closed */ } }
    }

    /**
     * Register a handler for spectrum bin data for a specific VFO.
     * @param {string} sdrId   "A" or "B"
     * @param {function({ sdrId, bins, centreHz, spanHz })} handler
     */
    onSpectrumUpdate(sdrId, handler) {
        this._spectrumHandlers[sdrId] = handler;
    }

    /**
     * Register a handler for SDR lifecycle status changes on one VFO.
     * @param {string} sdrId   "A" or "B"
     * @param {function(string)} handler  Receives "streaming", "disconnected", etc.
     */
    onStatusChange(sdrId, handler) {
        this._statusHandlers[sdrId] = handler;
    }

    /**
     * Register a handler for SDR error detail strings on one VFO.
     * @param {string} sdrId   "A" or "B"
     * @param {function(string)} handler
     */
    onError(sdrId, handler) {
        this._errorHandlers[sdrId] = handler;
    }

    /**
     * Register a handler for VFO A frequency changes (Hz).
     * @param {function(number)} handler
     */
    onFrequencyA(handler) {
        this._pipeline.register('FrequencyA', (value) => handler(value));
    }

    /**
     * Register a handler for VFO B frequency changes (Hz).
     * @param {function(number)} handler
     */
    onFrequencyB(handler) {
        this._pipeline.register('FrequencyB', (value) => handler(value));
    }

    /**
     * Register a handler for new DX cluster spots.
     * Each spot is the JSON shape from /api/dxcluster/spots.
     * @param {function(object)} handler
     */
    onDxSpot(handler) {
        this._pipeline.register('DxSpot', (value) => handler(value));
    }

    /**
     * Register a handler for DX cluster connection-status changes.
     * Value is { status, detail }.
     * @param {function({status:string, detail:string})} handler
     */
    onDxClusterStatus(handler) {
        this._pipeline.register('DxClusterStatus', (value) => handler(value));
    }

    /**
     * Register a handler for watched-callsign alerts.
     * Fired in addition to DxSpot when a spot matches the user's watch list.
     * @param {function(object)} handler  receives the full spot object
     */
    onDxAlert(handler) {
        this._pipeline.register('DxAlert', (value) => handler(value));
    }
}
