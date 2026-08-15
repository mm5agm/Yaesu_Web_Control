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
        for (const [key, entry] of Object.entries(this.config)) {
            if (!document.getElementById(entry.canvasId)) continue;

            const { type, canvasId, ...options } = entry;
            const gaugeType = type || key;
            const gauge = createGauge(gaugeType, canvasId, options);

            if (gauge) {
                gauge.render();
                this.gauges[key] = gauge;
            } else {
                console.warn(`MeterPanel: Failed to create gauge "${key}" (type "${gaugeType}")`);
            }
        }
    }

    /**
     * Return a gauge instance by key for direct access (e.g. diagnostics).
     * Prefer update() for normal needle changes.
     */
    getGauge(key) {
        return this.gauges[key] || null;
    }

    /**
     * Set a gauge needle to value and redraw.
     * The caller is responsible for calibrating and clamping the value
     * to the gauge's display scale before calling update().
     *
     * @param {string} key    Logical meter name matching a key in the config
     * @param {number} value  Display-scale value for the gauge needle
     */
    update(key, value) {
        const gauge = this.gauges[key];
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
