/**
 * Wire TX monitor to the Yaesu cycle key.
 * Left-click toggles MON; right-click opens the monitor level slider.
 */
import { CycleContextButton } from "/js/ui/toggle-dropdown-button.js";

function debounce(fn, ms) {
    let timer = null;
    return (...args) => {
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => {
            timer = null;
            fn(...args);
        }, ms);
    };
}

async function setMonitorEnabled(enabled) {
    try {
        const response = await fetch("/api/cat/monitoron", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ on: enabled }),
        });
        if (!response.ok) {
            console.error("Failed to set MON:", response.status);
        }
    } catch (error) {
        console.error("Error setting MON:", error.message);
    }
}

async function setMonitorLevel(value) {
    try {
        const response = await fetch("/api/cat/monitorlevel/a", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ level: Number(value) }),
        });
        if (!response.ok) {
            console.error("Failed to set MON level:", response.status);
        }
    } catch (error) {
        console.error("Error setting MON level:", error.message);
    }
}

export function initMonitorCycleButton(root = document.getElementById("monitorButton")) {
    if (!root) return null;

    const selectedId = root.dataset.enabled === "1" ? "1" : "0";
    const level = Math.min(100, Math.max(0, Number(root.dataset.value) || 0));
    let lastSelectedId = selectedId;
    let lastValue = level;

    const postLevel = debounce((value) => {
        setMonitorLevel(value);
    }, 150);

    const widget = new CycleContextButton(root, {
        label: "MON",
        options: [
            { id: "0", label: "OFF" },
            { id: "1", label: "MON" },
        ],
        selectedId,
        offId: "0",
        context: { type: "slider", min: 0, max: 100, value: level },
        a11yKey: "controls.monitor",
        onChange: (state) => {
            if (state.selectedId !== lastSelectedId) {
                lastSelectedId = state.selectedId;
                setMonitorEnabled(state.selectedId === "1");
            }
            if (state.value !== undefined && state.value !== lastValue) {
                lastValue = state.value;
                postLevel(state.value);
            }
        },
    });

    // Keep the local change guards aligned when CAT state arrives over SignalR.
    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        const state = widget.getState();
        lastSelectedId = state.selectedId;
        if (state.value !== undefined) lastValue = state.value;
    };

    window.monitorCycleButton = widget;
    return widget;
}
