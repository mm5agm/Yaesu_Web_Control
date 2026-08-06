// Yaesu Web Control – Meter Orchestrator
// Connects calibration-engine → MeterPanel.
// No DOM queries, no SignalR, no string formatting.
// Owns TX state, smoothing, noise filtering, calibration, and gauge updates.
// Returns plain numeric displayValue objects so the caller can format and update DOM labels.

export class FTdx101Meters {
    /**
     * @param {object} meterPanel        An initialised MeterPanel instance
     * @param {object} calibrationEngine An object exposing calibrateNumeric(key, raw)
     */
    constructor(meterPanel, calibrationEngine) {
        this._meterPanel   = meterPanel;
        this._calibration  = calibrationEngine;

        // TX state
        this._isTransmitting = false;

        // Smoothing: rolling-average windows for power and SWR.
        // Power uses a longer window (15 samples ≈ 1.5 s at 10 Hz polling)
        // because the PWR calibration curve gets steep above 100 W — each raw
        // ADC unit is ~1.6 W there, so even a few units of ADC noise visibly
        // jolts the gauge needle. SWR stays at 7 samples (~0.7 s) so the
        // operator sees a high SWR fault quickly enough to react.
        this._powerHistory        = [];
        this._swrHistory          = [];
        this._powerHistoryLength  = 15;
        this._swrHistoryLength    = 7;
        this._wasTransmittingPower = false;
        this._wasTransmittingSWR   = false;

        // Compression / ALC: same CAT path as SWR (MS13+RM0 or RM3/RM4) and
        // the same transient-zero problem — a single 0 mid-burst would slam
        // the bar to empty then snap back. Hold zeros until sustained, and
        // average non-zero samples so the display eases between readings.
        this._compHistory          = [];
        this._alcHistory           = [];
        this._compHistoryLength    = 7;
        this._alcHistoryLength     = 7;
        this._compZeroCount        = 0;
        this._alcZeroCount         = 0;
        this._compZeroThreshold    = 3;
        this._alcZeroThreshold     = 3;
        this._wasTransmittingComp  = false;
        this._wasTransmittingALC   = false;

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
        const clampedWatts = Math.round(Math.max(0, Math.min(watts, 200)));
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
        if (!this._isTransmitting) {
            this._compHistory         = [];
            this._compZeroCount       = 0;
            this._wasTransmittingComp = false;
            this._meterPanel.update('compression', 0);
            return { skip: false, gaugeKey: 'compression', displayValue: { db: 0 } };
        }
        if (!this._wasTransmittingComp) {
            this._compHistory = [];
            this._compZeroCount = 0;
        }
        this._wasTransmittingComp = true;

        // Transient zero while TX: hold the last averaged reading so the bar
        // doesn't flicker empty between syllables / CAT glitches.
        if (raw === 0) {
            this._compZeroCount++;
            if (this._compZeroCount < this._compZeroThreshold) {
                if (this._compHistory.length === 0) return { skip: true };
                const held = this._compHistory.reduce((s, v) => s + v, 0) / this._compHistory.length;
                const db = Math.max(0, Math.min(20, this._calibration.calibrateNumeric('Compression', held)));
                this._meterPanel.update('compression', db);
                return { skip: false, gaugeKey: 'compression', displayValue: { db } };
            }
            // Sustained zero — clear history and show 0.
            this._compHistory = [];
            this._meterPanel.update('compression', 0);
            return { skip: false, gaugeKey: 'compression', displayValue: { db: 0 } };
        }

        this._compZeroCount = 0;
        this._compHistory.push(raw);
        if (this._compHistory.length > this._compHistoryLength) this._compHistory.shift();
        if (this._compHistory.length < 2) return { skip: true };
        const rawAvg = this._compHistory.reduce((s, v) => s + v, 0) / this._compHistory.length;
        const db = Math.max(0, Math.min(20, this._calibration.calibrateNumeric('Compression', rawAvg)));
        this._meterPanel.update('compression', db);
        return { skip: false, gaugeKey: 'compression', displayValue: { db } };
    }

    _processALC(raw) {
        if (!this._isTransmitting) {
            this._alcHistory         = [];
            this._alcZeroCount       = 0;
            this._wasTransmittingALC = false;
            this._meterPanel.update('alc', 0);
            return { skip: false, gaugeKey: 'alc', displayValue: { percent: 0, alcVolts: 0, rawValue: 0 } };
        }
        if (!this._wasTransmittingALC) {
            this._alcHistory = [];
            this._alcZeroCount = 0;
        }
        this._wasTransmittingALC = true;

        if (raw === 0) {
            this._alcZeroCount++;
            if (this._alcZeroCount < this._alcZeroThreshold) {
                if (this._alcHistory.length === 0) return { skip: true };
                const held = this._alcHistory.reduce((s, v) => s + v, 0) / this._alcHistory.length;
                const alcVolts = this._calibration.calibrateNumeric('ALC', held);
                const percent  = Math.round((held / 255) * 100);
                this._meterPanel.update('alc', held);
                return { skip: false, gaugeKey: 'alc', displayValue: { percent, alcVolts, rawValue: held } };
            }
            this._alcHistory = [];
            this._meterPanel.update('alc', 0);
            return { skip: false, gaugeKey: 'alc', displayValue: { percent: 0, alcVolts: 0, rawValue: 0 } };
        }

        this._alcZeroCount = 0;
        this._alcHistory.push(raw);
        if (this._alcHistory.length > this._alcHistoryLength) this._alcHistory.shift();
        if (this._alcHistory.length < 2) return { skip: true };
        const rawAvg   = this._alcHistory.reduce((s, v) => s + v, 0) / this._alcHistory.length;
        const alcVolts = this._calibration.calibrateNumeric('ALC', rawAvg);
        const percent  = Math.round((rawAvg / 255) * 100);
        this._meterPanel.update('alc', rawAvg);
        return { skip: false, gaugeKey: 'alc', displayValue: { percent, alcVolts, rawValue: rawAvg } };
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
