// Yaesu Web Control – Unified Gauge Update Engine
// No DOM queries, no layout logic, no WebSocket logic.
// Only updates existing gauge instances with calibrated values.

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}

/**
 * Update a single gauge with a calibrated value.
 *
 * @param {object} gaugeInstance - An object wrapping the underlying canvas gauge.
 * @param {number} calibratedValue - The already calibrated value.
 * @param {object} options - Optional constraints.
 * @param {number} [options.min] - Minimum allowed value.
 * @param {number} [options.max] - Maximum allowed value.
 */
export function updateGaugeValue(gaugeInstance, calibratedValue, options = {}) {
    if (!gaugeInstance || !gaugeInstance.gauge) {
        return;
    }

    const { min, max } = options;

    let value = calibratedValue;

    if (typeof min === 'number' && typeof max === 'number') {
        value = clamp(value, min, max);
    }

    // canvas-gauges remembers the target of the animation that started a
    // run of overlapping animations, and every later one in that run ends on
    // it instead of on its own value. Meters update every 200 ms against a
    // 400 ms animation, so during an over the run never breaks, and the 0 at
    // the end left the power needle on a value from early in the over while
    // the label read 0 W (headless repro: 15, 30, 31, 30, 0 ends on 15).
    // Cancelling the running animation first makes the new one start from
    // where the needle is drawn and end on the value just sent.
    const g = gaugeInstance.gauge;
    if (g.animation && g.animation.frame) {
        g.animation.cancel();
        delete g._value;
    }
    g.value = value;
}

/**
 * Bulk update multiple gauges from a map of values.
 *
 * @param {object} gaugeMap - Map of keys to gauge instances.
 * @param {object} valueMap - Map of keys to calibrated values.
 * @param {object} constraintsMap - Optional map of keys to { min, max }.
 */
export function updateMultipleGauges(gaugeMap, valueMap, constraintsMap = {}) {
    if (!gaugeMap || !valueMap) {
        return;
    }

    for (const key of Object.keys(valueMap)) {
        const gaugeInstance = gaugeMap[key];
        const calibratedValue = valueMap[key];
        const constraints = constraintsMap[key] || {};

        if (gaugeInstance && typeof calibratedValue === 'number') {
            updateGaugeValue(gaugeInstance, calibratedValue, constraints);
        }
    }
}
