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

    // The gauge face is labelled 1.0 to 3.0 (see SWRGauge in gauge.js), so the
    // needle pins at the top for anything above 3:1. A dead-flat 3:1 and a
    // rig-damaging 10:1 therefore look identical on the dial — issue #128, and
    // it is not academic: it cost real time while diagnosing #124, because a
    // genuinely pinned reading and a fabricated 3.0 parked the needle in the
    // same place. Rather than redraw the face non-linearly, the readout says
    // when the needle has run out of scale.
    SWR_GAUGE_FULL_SCALE: 3.0,

    swrIsOffScale(ratio) {
        return ratio > this.SWR_GAUGE_FULL_SCALE;
    },

    // Badge text. Short by necessity — the badge is absolutely positioned and
    // centred under a 165px meter cell, so words would overflow into the
    // neighbouring gauges. The marker plus the colour change carries it for a
    // sighted operator; swrAnnouncement() below carries it for everyone else.
    swr(ratio) {
        const text = `${ratio.toFixed(1)}:1`;
        return this.swrIsOffScale(ratio) ? `${text} ▲` : text;
    },

    // What goes in canvas.dataset.reading, which is what the a11y live region
    // announces on hover. No layout constraint here, so it gets the words. This
    // is deliberately not identical to the badge text.
    swrAnnouncement(ratio) {
        const text = `${ratio.toFixed(1)}:1`;
        return this.swrIsOffScale(ratio) ? `${text} — off scale` : text;
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
