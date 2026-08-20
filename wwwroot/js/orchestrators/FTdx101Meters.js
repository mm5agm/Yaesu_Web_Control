// Yaesu Web Control – Meter Orchestrator
// Connects calibration-engine → MeterPanel.
// No DOM queries, no SignalR, no string formatting.
// Owns TX state, smoothing, noise filtering, calibration, and gauge updates.
// Returns plain numeric displayValue objects so the caller can format and update DOM labels.

export class FTdx101Meters {
    /**
     * @param {object} meterPanel        An initialised MeterPanel instance
     * @param {object} calibrationEngine An object exposing calibrateNumeric(key, raw)
     * @param {number} maxPowerWatts     The radio's rated output, used to clamp the
     *                                   Power reading to its dial. The shipped
     *                                   per-model PWR tables are still copies of the
     *                                   FTdx101MP's and run to 200 W, so without this
     *                                   a 100 W radio can calibrate past full scale.
     */
    constructor(meterPanel, calibrationEngine, maxPowerWatts = 200) {
        this._meterPanel   = meterPanel;
        this._calibration  = calibrationEngine;
        this._maxPowerWatts = maxPowerWatts;

        // TX state
        this._isTransmitting = false;

        // Smoothing: rolling-average windows for power and SWR.
        // TX tier polls at ~2 Hz, so 4 samples ≈ 2 s of smoothing on power
        // (the PWR calibration curve gets steep above 100 W — even a few ADC
        // units of noise visibly jolts the needle). SWR stays at 3 samples
        // (~1.5 s) so the operator sees a high SWR fault quickly enough to react.
        this._powerHistory        = [];
        this._swrHistory          = [];
        this._powerHistoryLength  = 4;
        this._swrHistoryLength    = 3;
        this._wasTransmittingPower = false;
        this._wasTransmittingSWR   = false;

        // IDD filter state
        this._iddLast      = 0;
        this._iddZeroCount = 0;

        // VDD filter state
        this._lastValidVDD = 204;  // ~48 V default
        this._vddLast      = 48;

        // Temperature filter state
        this._paTempLast      = 0;
        this._paTempZeroCount = 0;
    }

    // ----------------------------------------------------------------
    // TX state
    // ----------------------------------------------------------------

    /**
     * Notify the orchestrator that TX state has changed.
     * Must be called whenever IsTransmitting updates before the next meter update.
     * @param {boolean} value
     */
    setTransmitting(value) {
        this._isTransmitting = value;
    }

    // ----------------------------------------------------------------
    // Public entry point
    // ----------------------------------------------------------------

    /**
     * Route a single meter update from SignalR through processing to the gauge.
     *
     * @param {string} property   SignalR property name, e.g. 'PowerMeter'
     * @param {number} rawValue   Raw ADC value from the radio (0–255)
     * @returns {{ skip: boolean, gaugeKey: string, displayValue: object } | null}
     *   null   — property is not a known meter
     *   skip   — filtered/debounced reading; caller should not update DOM
     *   Otherwise displayValue contains plain numeric fields ready for formatting
     */
    handleMeterUpdate(property, rawValue) {
        switch (property) {
            case 'PowerMeter':       return this._processPower(rawValue);
            case 'SWRMeter':         return this._processSWR(rawValue);
            case 'CompressionMeter': return this._processCompression(rawValue);
            case 'ALCMeter':         return this._processALC(rawValue);
            case 'IDDMeter':         return this._processIDD(rawValue);
            case 'VDDMeter':         return this._processVDD(rawValue);
            case 'Temperature':      return this._processTemp(rawValue);
            default:                 return null;
        }
    }

    /**
     * Returns true if the given property name is handled by handleMeterUpdate.
     * @param {string} property
     */
    isMeterProperty(property) {
        return ['PowerMeter', 'SWRMeter', 'CompressionMeter', 'ALCMeter',
                'IDDMeter', 'VDDMeter', 'Temperature'].includes(property);
    }

    // ----------------------------------------------------------------
    // Per-meter processors
    // ----------------------------------------------------------------

    _processPower(raw) {
        if (!this._isTransmitting) {
            this._powerHistory        = [];
            this._wasTransmittingPower = false;
            this._meterPanel.update('power', 0);
            return { skip: false, gaugeKey: 'power', displayValue: { watts: 0, rawAvg: 0 } };
        }
        if (!this._wasTransmittingPower) {
            this._powerHistory = [];
        }
        this._wasTransmittingPower = true;
        this._powerHistory.push(raw);
        if (this._powerHistory.length > this._powerHistoryLength) this._powerHistory.shift();
        const rawAvg      = this._powerHistory.reduce((s, v) => s + v, 0) / this._powerHistory.length;
        const watts       = this._calibration.calibrateNumeric('PWR', rawAvg);
        const clampedWatts = Math.round(Math.max(0, Math.min(watts, this._maxPowerWatts)));
        this._meterPanel.update('power', clampedWatts);
        return { skip: false, gaugeKey: 'power', displayValue: { watts: clampedWatts, rawAvg } };
    }

    _processSWR(raw) {
        if (!this._isTransmitting) {
            this._swrHistory        = [];
            this._wasTransmittingSWR = false;
            this._meterPanel.update('swr', 0);
            return { skip: false, gaugeKey: 'swr', displayValue: { swr: 1.0 } };
        }
        if (!this._wasTransmittingSWR) {
            this._swrHistory = [];
        }
        this._wasTransmittingSWR = true;
        this._swrHistory.push(raw);
        if (this._swrHistory.length > this._swrHistoryLength) this._swrHistory.shift();
        // Require at least 2 readings before displaying — single-reading bursts are likely noise.
        if (this._swrHistory.length < 2) return { skip: true };
        const rawAvg    = this._swrHistory.reduce((s, v) => s + v, 0) / this._swrHistory.length;
        const swr       = this._calibration.calibrateNumeric('SWR', rawAvg);
        const swrClamped = Math.min(swr, 10.0);
        this._meterPanel.update('swr', (swrClamped - 1.0) * 127.5);
        return { skip: false, gaugeKey: 'swr', displayValue: { swr: swrClamped } };
    }

    _processCompression(raw) {
        const db = this._isTransmitting
            ? Math.max(0, Math.min(20, this._calibration.calibrateNumeric('Compression', raw)))
            : 0;
        this._meterPanel.update('compression', db);
        return { skip: false, gaugeKey: 'compression', displayValue: { db } };
    }

    _processALC(raw) {
        if (!this._isTransmitting) {
            this._meterPanel.update('alc', 0);
            return { skip: false, gaugeKey: 'alc', displayValue: { percent: 0, alcVolts: 0, rawValue: 0 } };
        }
        const alcVolts = this._calibration.calibrateNumeric('ALC', raw);
        const percent  = Math.round((raw / 255) * 100);
        this._meterPanel.update('alc', raw);  // gauge uses raw 0–255 scale
        return { skip: false, gaugeKey: 'alc', displayValue: { percent, alcVolts, rawValue: raw } };
    }

    _processIDD(raw) {
        if (!this._isTransmitting) {
            this._iddLast = 0;
            this._iddZeroCount = 0;
            this._meterPanel.update('idd', 0);
            return { skip: false, gaugeKey: 'idd', displayValue: { amps: 0 } };
        }
        const amps = this._calibration.calibrateNumeric('IDD', raw);
        if (amps === 0) {
            this._iddZeroCount++;
            if (this._iddZeroCount < 2) return { skip: true };
        } else {
            this._iddZeroCount = 0;
        }
        if (Math.abs(amps - this._iddLast) > 5 && this._iddLast !== 0) return { skip: true };
        this._iddLast = amps;
        this._meterPanel.update('idd', Math.max(0, Math.min(amps, 25)));
        return { skip: false, gaugeKey: 'idd', displayValue: { amps } };
    }

    _processVDD(raw) {
        const minRaw = 175;  // ~41.2 V — margin above gauge minimum
        const maxRaw = 235;  // ~55 V
        if (raw < minRaw || raw > maxRaw) return { skip: true };
        this._lastValidVDD = raw;
        const volts = this._calibration.calibrateNumeric('VPA', this._lastValidVDD);
        if (Math.abs(volts - this._vddLast) > 3 && this._vddLast !== 0) return { skip: true };
        this._vddLast = volts;
        this._meterPanel.update('vdd', Math.max(40, Math.min(volts, 55)));
        return { skip: false, gaugeKey: 'vdd', displayValue: { volts } };
    }

    _processTemp(tempC) {
        if (tempC === 0) {
            this._paTempZeroCount++;
            if (this._paTempZeroCount < 2) return { skip: true };
        } else {
            this._paTempZeroCount = 0;
        }
        if (Math.abs(tempC - this._paTempLast) > 10 && this._paTempLast !== 0) return { skip: true };
        this._paTempLast = tempC;
        const calibrated = this._calibration.calibrateNumeric('TPA', tempC);
        this._meterPanel.update('temp', calibrated);
        return { skip: false, gaugeKey: 'temp', displayValue: { tempC: calibrated } };
    }
}
