/**
 * Wire the speech processor to the Yaesu cycle key.
 * Left-click toggles PROC; right-click opens the processor level slider.
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

async function setProcEnabled(enabled) {
    try {
        const response = await fetch("/api/cat/proc", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ enabled }),
        });
        if (!response.ok) {
            console.error("Failed to set PROC:", response.status);
        }
    } catch (error) {
        console.error("Error setting PROC:", error.message);
    }
}

async function setProcLevel(value) {
    try {
        const response = await fetch("/api/cat/proclevel", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ value: Number(value) }),
        });
        if (!response.ok) {
            console.error("Failed to set PROC level:", response.status);
        }
    } catch (error) {
        console.error("Error setting PROC level:", error.message);
    }
}

export function initProcCycleButton(root = document.getElementById("procButton")) {
    if (!root) return null;

    const selectedId = root.dataset.enabled === "1" ? "1" : "0";
    const level = Math.min(100, Math.max(0, Number(root.dataset.value) || 0));
    let lastSelectedId = selectedId;
    let lastValue = level;

    const postLevel = debounce((value) => {
        setProcLevel(value);
    }, 150);

    const widget = new CycleContextButton(root, {
        label: "PROC",
        options: [
            { id: "0", label: "OFF" },
            { id: "1", label: "PROC" },
        ],
        selectedId,
        offId: "0",
        context: { type: "slider", min: 0, max: 100, value: level },
        a11yKey: "controls.proc",
        onChange: (state) => {
            if (state.selectedId !== lastSelectedId) {
                lastSelectedId = state.selectedId;
                setProcEnabled(state.selectedId === "1");
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

    window.procCycleButton = widget;
    return widget;
}
