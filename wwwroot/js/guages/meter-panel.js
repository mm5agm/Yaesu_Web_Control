// Yaesu Web Control – Meter Panel
// Owns all gauge instances. Creates them via gaugeFactory, renders them, and
// exposes update() as the single point of entry for needle changes.
// No calibration, no WebSocket, no DOM queries beyond canvas IDs.

// ?v=1 is a one-time cache-buster, not a number to bump — see gaugeFactory.js.
import { createGauge } from './gaugeFactory.js?v=1';
import { updateGaugeValue } from './update-engine.js?v=1';

export class MeterPanel {

    /**
     * @param {Object} config  Keys are logical meter names; values are config objects.
     *
     * Config object shape:
     *   { canvasId: 'swrMeterCanvas' }          — key is used as gauge type
     *   { type: 'smeter', canvasId: 'sMeterCanvas' }  — explicit type overrides key
     *
     * Example:
     *   {
     *     smeter:      { type: 'smeter', canvasId: 'sMeterCanvas' },
     *     power:       { canvasId: 'powerMeterCanvas' },
     *     swr:         { canvasId: 'swrMeterCanvas' },
     *     alc:         { canvasId: 'alcMeterCanvas' },
     *     compression: { canvasId: 'compressionMeterCanvas' },
     *     idd:         { canvasId: 'iddMeterCanvas' },
     *     vdd:         { canvasId: 'vddMeterCanvas' },
     *     temp:        { canvasId: 'tempMeterCanvas' }
     *   }
     */
    constructor(config = {}) {
        this.config = config;
        this.gauges = {};
        this._createGauges();
    }

    _createGauges() {
        for (const key of Object.keys(this.config)) this._ensureGauge(key);
    }

    /**
     * Create a gauge for `key` if its canvas is present and it does not exist
     * yet. Called for every key at construction, and again via ensureGauges()
     * when a panel that was not mounted then — a Flex UI tab first opened
     * after page init, the inactive VFO — brings its canvas into the DOM.
     * Without this a meter whose canvas appeared late stayed dead for the
     * session, because nothing retried after construction.
     */
    _ensureGauge(key) {
        if (this.gauges[key]) return this.gauges[key];

        const entry = this.config[key];
        if (!entry || !document.getElementById(entry.canvasId)) return null;

        const { type, canvasId, ...options } = entry;
        const gaugeType = type || key;
        const gauge = createGauge(gaugeType, canvasId, options);

        if (!gauge) {
            console.warn(`MeterPanel: Failed to create gauge "${key}" (type "${gaugeType}")`);
            return null;
        }
        gauge.render();
        this.gauges[key] = gauge;
        return gauge;
    }

    /** Create any gauges whose canvases have appeared since construction. */
    ensureGauges() {
        for (const key of Object.keys(this.config)) this._ensureGauge(key);
    }

    /**
     * Return a gauge instance by key for direct access (e.g. diagnostics).
     * Prefer update() for normal needle changes.
     */
    getGauge(key) {
        return this.gauges[key] || null;
    }

    setMarker(key, value) {
        const gauge = this.gauges[key];
        if (!gauge || typeof gauge.setMarker !== 'function') return;
        gauge.setMarker(value);
    }

    /**
     * Set a gauge needle to value and redraw.
     * The caller is responsible for calibrating and clamping the value
     * to the gauge's display scale before calling update().
     *
     * For meters that have a compact linear sibling, also mirrors the same
     * display-scale value so the orchestrator stays single-face.
     *
     * @param {string} key    Logical meter name matching a key in the config
     * @param {number} value  Display-scale value for the gauge needle
     */
    update(key, value) {
        this._updateOne(key, value);
        // Compact linear siblings share the same display scale as the radials.
        const linearSibling = {
            power: 'powerLinear',
            swr: 'swrLinear',
            alc: 'alcLinear',
            compression: 'compressionLinear',
            temp: 'tempLinear',
            idd: 'iddLinear',
            vdd: 'vddLinear',
            smeter: 'smeterLinearA',
            smeterB: 'smeterLinearB'
        }[key];
        if (linearSibling) this._updateOne(linearSibling, value);
    }

    _updateOne(key, value) {
        // _ensureGauge covers the canvas having arrived after construction.
        const gauge = this.gauges[key] || this._ensureGauge(key);
        if (!gauge) return;
        updateGaugeValue(gauge, value);
        gauge.gauge.draw();
    }

    /** Bulk-update multiple gauges. valueMap: { key: value, ... } */
    updateAll(valueMap) {
        for (const [key, value] of Object.entries(valueMap)) {
            this.update(key, value);
        }
    }
}
