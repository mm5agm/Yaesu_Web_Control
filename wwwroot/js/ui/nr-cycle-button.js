/**
 * Wire CycleContextButton instances for VFO A/B NR (or DNR) keys.
 * Left-click cycles OFF/NR1/NR2 (or OFF/ON); right-click opens level 1–15.
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

/**
 * @param {HTMLElement | null} root
 * @returns {CycleContextButton | null}
 */
export function initNrCycleButton(root) {
    if (!root) return null;

    const vfo = root.dataset.vfo || "A";
    const label = root.dataset.label || "NR";
    const isDnr = root.dataset.dnr === "true";
    const selectedId = root.dataset.selected || "0";
    const level = Math.min(15, Math.max(1, parseInt(root.dataset.level, 10) || 1));

    const options = isDnr
        ? [
              { id: "0", label: "OFF" },
              { id: "1", label: "DNR" },
          ]
        : [
              { id: "0", label: "OFF" },
              { id: "1", label: "NR1" },
              { id: "2", label: "NR2" },
          ];

    let lastSelectedId = selectedId;
    let lastValue = level;

    const postLevel = debounce((value) => {
        if (typeof window.setNrLevel === "function") {
            window.setNrLevel(vfo, value);
        }
    }, 150);

    const widget = new CycleContextButton(root, {
        label,
        options,
        selectedId,
        offId: "0",
        context: { type: "slider", min: 1, max: 15, value: level },
        onChange: (state) => {
            if (state.selectedId !== lastSelectedId) {
                lastSelectedId = state.selectedId;
                if (window.radioControl?.setNr) {
                    window.radioControl.setNr(vfo, state.selectedId);
                }
            }
            if (state.value !== undefined && state.value !== lastValue) {
                lastValue = state.value;
                postLevel(state.value);
            }
        },
    });

    // Keep last-* in sync when SignalR drives setState({ silent: true }).
    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        const s = widget.getState();
        lastSelectedId = s.selectedId;
        if (s.value !== undefined) lastValue = s.value;
    };

    return widget;
}

export function initNrCycleButtons() {
    const a = initNrCycleButton(document.getElementById("nrButtonA"));
    const b = initNrCycleButton(document.getElementById("nrButtonB"));
    if (a) window.nrCycleButtonA = a;
    if (b) window.nrCycleButtonB = b;
    return { a, b };
}
