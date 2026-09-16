/**
 * Render the transmit-power control as a Yaesu key.
 * The power slider is available from the key's right-click context menu.
 * POWER is a value, not an on/off function, so the key has no LED state.
 */
import { CycleContextButton } from "/js/ui/toggle-dropdown-button.js";

export function initPowerCycleButton(root = document.getElementById("powerButton")) {
    if (!root) return null;

    const max = Math.max(5, Number(root.dataset.maxPower) || 200);
    const value = Math.min(max, Math.max(5, Number(root.dataset.value) || 5));
    let lastValue = value;
    const syncPowerMarker = (watts) => {
        window.meterPanel?.setMarker?.("powerLinear", watts);
    };

    const widget = new CycleContextButton(root, {
        label: "POWER",
        options: [{ id: "power", label: "POWER" }],
        selectedId: "power",
        offId: null,
        showLed: false,
        context: { type: "slider", min: 5, max, step: 5, value, valueSuffix: " W" },
        a11yKey: "controls.power",
        onChange: (state) => {
            if (state.value === undefined || state.value === lastValue) return;
            lastValue = state.value;
            syncPowerMarker(state.value);
            window.radioControl?.setPower?.(
                window.txVfo === 1 ? "B" : "A",
                state.value
            );
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        const currentValue = widget.getState().value;
        lastValue = currentValue;
        syncPowerMarker(currentValue);
    };

    syncPowerMarker(value);
    window.powerCycleButton = widget;
    return widget;
}
