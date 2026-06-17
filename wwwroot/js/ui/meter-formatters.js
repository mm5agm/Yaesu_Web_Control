// Yaesu Web Control – Meter Formatters
// Pure UI formatting helpers. No calibration, no DOM, no gauge logic, no side effects.
//
// Method naming convention:
//   Gauge overlay spans: the gauge config supplies the unit suffix, so formatters
//   return only the numeric string (e.g. "47.5") and the suffix is appended by the DOM.
//   Freestanding labels / bars: the formatter includes the unit (e.g. "47.5V", "35%").

export const MeterFormatters = {

    // ----------------------------------------------------------------
    // POWER
    // ----------------------------------------------------------------

    // Gauge overlay value (PowerGauge appends 'W' as gaugeTitleSuffix).
    powerOverlay(watts) {
        return String(Math.round(watts));
    },

    // Slider label and any freestanding power display (unit included).
    // Space before the unit per SI typography conventions and SP3L-Jacek's
    // request on #36 ("100 W" instead of "100W").
    powerLabel(watts) {
        return `${Math.round(watts)} W`;
    },

    // ----------------------------------------------------------------
    // SWR  (SWRGauge has no gaugeTitleSuffix — full text goes in span)
    // ----------------------------------------------------------------

    swr(ratio) {
        return `${ratio.toFixed(1)}:1`;
    },

    // ----------------------------------------------------------------
    // ALC  (ALCGauge has no gaugeTitleSuffix — full text goes in span)
    // ----------------------------------------------------------------

    // Gauge overlay — calibrated volts display.
    alcVolts(volts) {
        return `${Math.round(volts)}V`;
    },

    // ----------------------------------------------------------------
    // COMPRESSION  (CompressionGauge appends 'dB' as gaugeTitleSuffix)
    // ----------------------------------------------------------------

    compressionOverlay(db) {
        return db.toFixed(1);
    },

    // ----------------------------------------------------------------
    // IDD — drain current  (IDDGauge appends 'A' as gaugeTitleSuffix)
    // ----------------------------------------------------------------

    iddOverlay(amps) {
        return amps.toFixed(1);
    },

    // ----------------------------------------------------------------
    // VDD — supply voltage  (VDDGauge appends 'V' as gaugeTitleSuffix)
    // ----------------------------------------------------------------

    vddOverlay(volts) {
        return volts.toFixed(1);
    },

    // ----------------------------------------------------------------
    // PA TEMPERATURE  (TempGauge appends '°C' as gaugeTitleSuffix)
    // ----------------------------------------------------------------

    tempOverlay(tempC) {
        return String(Math.round(tempC));
    },

    // ----------------------------------------------------------------
    // GENERIC PERCENTAGE — used for ALC bar, MIC bar, compression bar
    // Takes an already-computed 0–100 percentage value.
    // ----------------------------------------------------------------

    percent(pct) {
        return `${Math.round(pct)}%`;
    }
};
