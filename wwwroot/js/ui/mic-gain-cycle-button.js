/**
 * Render MIC Gain / Data Out as a Yaesu key.
 * The level slider is available from the key's right-click context menu.
 */
import { CycleContextButton } from "/js/ui/toggle-dropdown-button.js";

export function initMicGainCycleButton(root = document.getElementById("micGainButton")) {
    if (!root) return null;

    const value = Math.min(100, Math.max(0, Number(root.dataset.value) || 0));
    let lastValue = value;
    const widget = new CycleContextButton(root, {
        label: root.dataset.label || "MIC Gain",
        options: [{ id: "level", label: root.dataset.label || "MIC Gain" }],
        selectedId: "level",
        offId: null,
        showLed: false,
        context: { type: "slider", min: 0, max: 100, step: 1, value },
        a11yKey: "controls.micGain",
        onChange: (state) => {
            if (state.value === undefined || state.value === lastValue) return;
            lastValue = state.value;
            window.setMicGain?.(state.value);
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        lastValue = widget.getState().value;
    };

    window.micGainCycleButton = widget;
    return widget;
}
