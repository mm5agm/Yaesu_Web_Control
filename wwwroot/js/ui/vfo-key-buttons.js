/**
 * Wire Yaesu-key widgets for Band, Mode, Roofing, IF Width, IF Shift, AGC,
 * IPO, ATT, NB, Contour, APF, Auto Notch, Man Notch, and QMB.
 */
import {
    ToggleDropdownButton,
    ToggleSliderButton,
    ToggleButton,
    CycleContextButton,
    ActionMenuButton,
} from "/js/ui/toggle-dropdown-button.js";
import { rebuildIfWidthSelect, ifWidthKeyConfig } from "/js/ui/if-width-tables.js";

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

const BAND_OPTIONS = [
    { id: "160m", label: "160m" },
    { id: "80m", label: "80m" },
    { id: "60m", label: "60m" },
    { id: "40m", label: "40m" },
    { id: "30m", label: "30m" },
    { id: "20m", label: "20m" },
    { id: "17m", label: "17m" },
    { id: "15m", label: "15m" },
    { id: "12m", label: "12m" },
    { id: "10m", label: "10m" },
    { id: "6m", label: "6m" },
    { id: "4m", label: "4m" },
];

const MODE_OPTIONS = [
    { id: "LSB", label: "LSB" },
    { id: "USB", label: "USB" },
    { id: "CW-U", label: "CW-U" },
    { id: "CW-L", label: "CW-L" },
    { id: "FM", label: "FM" },
    { id: "FM-N", label: "FM-N" },
    { id: "AM", label: "AM" },
    { id: "AM-N", label: "AM-N" },
    { id: "RTTY-L", label: "RTTY-L" },
    { id: "RTTY-U", label: "RTTY-U" },
    { id: "DATA-L", label: "DATA-L" },
    { id: "DATA-U", label: "DATA-U" },
    { id: "DATA-FM", label: "DATA-FM" },
    { id: "DATA-FM-N", label: "D-FM-N" },
    { id: "PSK", label: "PSK" },
];

const ANTENNA_OPTIONS = [
    { id: "1", label: "ANT 1" },
    { id: "2", label: "ANT 2" },
    { id: "3", label: "ANT 3" },
];

const AGC_OPTIONS = [
    { id: "0", label: "OFF" },
    { id: "1", label: "FAST" },
    { id: "2", label: "MID" },
    { id: "3", label: "SLOW" },
    { id: "4", label: "AUTO" },
];

const IPO_OPTIONS = [
    { id: "0", label: "IPO" },
    { id: "1", label: "AMP1" },
    { id: "2", label: "AMP2" },
];

const ATT_OPTIONS = [
    { id: "00", label: "OFF" },
    { id: "06", label: "6 dB" },
    { id: "12", label: "12 dB" },
    { id: "18", label: "18 dB" },
];

/**
 * Parse [{id,label},…] from data-options on a Yaesu-key root.
 * @param {HTMLElement} root
 * @returns {{ id: string, label: string }[]}
 */
function parseOptionsFromRoot(root) {
    try {
        const raw = root.dataset.options;
        if (!raw) return [];
        const parsed = JSON.parse(raw);
        if (!Array.isArray(parsed)) return [];
        return parsed
            .filter((o) => o && o.id != null && o.label != null)
            .map((o) => ({ id: String(o.id), label: String(o.label) }));
    } catch {
        return [];
    }
}

/**
 * Menu-only key (Band / Segment / Mode / Roofing): left-click opens menu; right-click disabled.
 * @param {HTMLElement | null} root
 * @param {{ label: string, options: { id: string, label: string }[], menuClass?: string, a11yKey?: string, onSelect: (id: string) => void }} cfg
 */
function initMenuOnlyButton(root, cfg) {
    if (!root) return null;
    if (!cfg.options?.length) return null;
    // Allow empty-string selectedId only when explicitly provided (Segment "--").
    // An empty data-selected on Band would otherwise leave the face blank until
    // SignalR arrives — fall back to the first option as a temporary stand-in.
    const rawSelected = root.dataset.selected;
    const selectedId =
        rawSelected !== undefined && rawSelected !== ""
            ? rawSelected
            : (cfg.allowEmptySelected ? "" : cfg.options[0].id);
    let lastId = selectedId;

    const widget = new ToggleDropdownButton(root, {
        label: cfg.label,
        options: cfg.options,
        selectedId,
        clickAction: "openMenu",
        contextMenu: false,
        showLed: false,
        menuClass: cfg.menuClass ?? "",
        a11yKey: cfg.a11yKey,
        onChange: (state) => {
            if (state.selectedId !== lastId) {
                lastId = state.selectedId;
                cfg.onSelect(state.selectedId);
            }
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        lastId = widget.getState().selectedId;
    };
    const originalSetOptions = widget.setOptions.bind(widget);
    widget.setOptions = (options, opts = {}) => {
        originalSetOptions(options, opts);
        lastId = widget.getState().selectedId;
    };

    return widget;
}

/**
 * ToggleDropdown with offId (AGC / ATT): left-click toggles off ↔ last on.
 * @param {HTMLElement | null} root
 * @param {{ label: string, options: { id: string, label: string }[], offId: string, a11yKey?: string, onSelect: (id: string) => void }} cfg
 */
function initOffToggleDropdown(root, cfg) {
    if (!root) return null;
    let selectedId = root.dataset.selected || cfg.offId;
    // AGC AUTO-FAST/MID/SLOW (5/6) → AUTO (4)
    if (cfg.offId === "0" && (selectedId === "5" || selectedId === "6")) selectedId = "4";

    let lastId = selectedId;

    const widget = new ToggleDropdownButton(root, {
        label: cfg.label,
        options: cfg.options,
        selectedId,
        offId: cfg.offId,
        a11yKey: cfg.a11yKey,
        onChange: (state) => {
            if (state.selectedId !== lastId) {
                lastId = state.selectedId;
                cfg.onSelect(state.selectedId);
            }
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        if (partial.selectedId === "5" || partial.selectedId === "6") {
            partial = { ...partial, selectedId: "4" };
        }
        originalSetState(partial, opts);
        lastId = widget.getState().selectedId;
    };

    return widget;
}

/**
 * ToggleSlider (NB / Contour / APF / Man Notch).
 * @param {HTMLElement | null} root
 * @param {{
 *   label: string,
 *   min: number,
 *   max: number,
 *   step?: number,
 *   valueSuffix?: string,
 *   a11yKey?: string,
 *   onEnabled: (enabled: boolean) => void,
 *   onValue: (value: number) => void
 * }} cfg
 */
function initToggleSlider(root, cfg) {
    if (!root) return null;
    const enabled = root.dataset.enabled === "1" || root.dataset.enabled === "true";
    const min = Number(root.dataset.min ?? cfg.min);
    const max = Number(root.dataset.max ?? cfg.max);
    const step = Number(root.dataset.step ?? cfg.step ?? 1);
    const parsedValue = parseInt(root.dataset.value, 10);
    const value = Math.min(
        max,
        Math.max(min, Number.isNaN(parsedValue) ? min : parsedValue)
    );

    let lastEnabled = enabled;
    let lastValue = value;

    const postValue = debounce((v) => cfg.onValue(v), 150);

    const widget = new ToggleSliderButton(root, {
        label: cfg.label,
        min,
        max,
        step,
        valueSuffix: cfg.valueSuffix ?? "",
        value,
        enabled,
        a11yKey: cfg.a11yKey,
        onChange: (state) => {
            if (state.enabled !== lastEnabled) {
                lastEnabled = state.enabled;
                cfg.onEnabled(state.enabled);
            }
            if (state.value !== lastValue) {
                lastValue = state.value;
                postValue(state.value);
            }
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        const s = widget.getState();
        lastEnabled = s.enabled;
        lastValue = s.value;
    };

    return widget;
}

/**
 * @param {HTMLElement | null} root
 * @param {{ label: string, a11yKey?: string, onEnabled: (enabled: boolean) => void }} cfg
 */
function initSimpleToggle(root, cfg) {
    if (!root) return null;
    const enabled = root.dataset.enabled === "1" || root.dataset.enabled === "true";
    let lastEnabled = enabled;

    const widget = new ToggleButton(root, {
        label: cfg.label,
        enabled,
        a11yKey: cfg.a11yKey,
        onChange: (state) => {
            if (state.enabled !== lastEnabled) {
                lastEnabled = state.enabled;
                cfg.onEnabled(state.enabled);
            }
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        lastEnabled = widget.getState().enabled;
    };

    return widget;
}

function initBandButton(vfo) {
    return initMenuOnlyButton(document.getElementById(`bandButton${vfo}`), {
        label: "Band",
        options: BAND_OPTIONS,
        menuClass: "toggle-dd__menu--grid",
        a11yKey: `vfo.${vfo.toLowerCase()}.band`,
        onSelect: (id) => {
            if (window.radioControl?.setBand) window.radioControl.setBand(vfo, id);
            // Segment key used to refresh from the band-radio change
            // listener; drive it from the menu pick so it still updates before
            // the SignalR Band* round-trip.
            if (typeof window.populateSegmentSelect === "function" && window.bandPlanData) {
                window.populateSegmentSelect(vfo, id);
            }
        },
    });
}

function initSegmentButton(vfo) {
    return initMenuOnlyButton(document.getElementById(`segmentButton${vfo}`), {
        label: "Segment",
        options: [{ id: "", label: "--" }],
        allowEmptySelected: true,
        // Single-column menu (no toggle-dd__menu--grid).
        a11yKey: `vfo.${vfo.toLowerCase()}.segment`,
        onSelect: (id) => {
            if (typeof window.onSegmentChange === "function") window.onSegmentChange(vfo, id);
        },
    });
}

function initModeButton(vfo) {
    return initMenuOnlyButton(document.getElementById(`modeButton${vfo}`), {
        label: "Mode",
        options: MODE_OPTIONS,
        menuClass: "toggle-dd__menu--grid",
        a11yKey: `vfo.${vfo.toLowerCase()}.mode`,
        onSelect: (id) => {
            if (typeof window.setMode === "function") window.setMode(vfo, id);
        },
    });
}

function initAntennaButton(vfo) {
    return initMenuOnlyButton(document.getElementById(`antennaButton${vfo}`), {
        label: "ANT",
        options: ANTENNA_OPTIONS,
        a11yKey: `vfo.${vfo.toLowerCase()}.antenna`,
        onSelect: (id) => {
            if (window.radioControl?.setAntenna) window.radioControl.setAntenna(vfo, id);
        },
    });
}

function initRoofingButton(vfo) {
    const root = document.getElementById(`roofingButton${vfo}`);
    if (!root) return null;
    const options = parseOptionsFromRoot(root);
    if (!options.length) return null;
    // Prefer the server-selected code when it is in the fitted list.
    if (root.dataset.selected && !options.some((o) => o.id === root.dataset.selected)) {
        root.dataset.selected = options[0].id;
    }
    return initMenuOnlyButton(root, {
        label: "Roofing",
        options,
        // Single-column menu (no toggle-dd__menu--grid).
        a11yKey: `controls.roofing${vfo}`,
        onSelect: (id) => {
            if (window.radioControl?.setRoofingFilter) {
                window.radioControl.setRoofingFilter(vfo, id);
            }
        },
    });
}

function resolveRadioModel() {
    return (
        window._radioModel ||
        window.getConfiguredRadioModel?.() ||
        document.getElementById("vfoRow")?.dataset?.radioModel ||
        null
    );
}

function initIfWidthButton(vfo) {
    const root = document.getElementById(`ifWidthButton${vfo}`);
    if (!root) return null;
    const model = resolveRadioModel();
    const cfg = ifWidthKeyConfig(model, root.dataset.mode);
    const selectedId = root.dataset.selected || cfg?.defaultId || "0";
    const options = cfg?.options ?? [{ id: selectedId, label: "…" }];
    let lastId = selectedId;

    const widget = new CycleContextButton(root, {
        label: "IF Width",
        options,
        selectedId,
        clickAction: "select",
        clickSelectId: cfg?.defaultId ?? "0",
        extraLabels: cfg?.extraLabels,
        sliderAlias: cfg?.sliderAlias,
        offIds: cfg?.offIds,
        showLed: true,
        linkSliderToOptions: true,
        context: { type: "slider" },
        a11yKey: `controls.ifWidth${vfo}`,
        onChange: (state) => {
            if (state.selectedId !== lastId) {
                lastId = state.selectedId;
                if (window.radioControl?.setIfWidth) {
                    window.radioControl.setIfWidth(vfo, state.selectedId);
                }
            }
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        lastId = widget.getState().selectedId;
    };
    const originalSetOptions = widget.setOptions.bind(widget);
    widget.setOptions = (next, opts = {}) => {
        originalSetOptions(next, opts);
        lastId = widget.getState().selectedId;
    };

    rebuildIfWidthSelect(widget, model, root.dataset.mode);
    return widget;
}

const IF_SHIFT_MIN = -1000;
const IF_SHIFT_MAX = 1000;
const IF_SHIFT_STEP = 20;

function ifShiftLabel(hz) {
    if (hz === 0) return "0";
    return hz > 0 ? `+${hz} Hz` : `${hz} Hz`;
}

function ifShiftOptions() {
    const options = [];
    for (let hz = IF_SHIFT_MIN; hz <= IF_SHIFT_MAX; hz += IF_SHIFT_STEP) {
        options.push({ id: String(hz), label: ifShiftLabel(hz) });
    }
    return options;
}

function snapIfShiftHz(hz) {
    const n = Number(hz);
    if (!Number.isFinite(n)) return 0;
    const snapped = Math.round(n / IF_SHIFT_STEP) * IF_SHIFT_STEP;
    return Math.min(IF_SHIFT_MAX, Math.max(IF_SHIFT_MIN, snapped));
}

function initIfShiftButton(vfo) {
    const root = document.getElementById(`ifShiftButton${vfo}`);
    if (!root) return null;
    const options = ifShiftOptions();
    const selectedId = String(snapIfShiftHz(root.dataset.value));
    let lastId = selectedId;

    const postShift = debounce((hz) => {
        if (window.radioControl?.setIfShift) {
            window.radioControl.setIfShift(vfo, hz);
        }
    }, 150);

    const widget = new CycleContextButton(root, {
        label: "IF Shift",
        options,
        selectedId,
        clickAction: "select",
        clickSelectId: "0",
        offId: "0",
        showLed: true,
        linkSliderToOptions: true,
        context: { type: "slider" },
        a11yKey: `controls.ifShift${vfo}`,
        onChange: (state) => {
            const hz = snapIfShiftHz(state.selectedId);
            const panel = vfo === "B" ? window.filterScopePanelB : window.filterScopePanelA;
            if (panel) panel.setState({ ifShiftHz: hz });
            if (state.selectedId !== lastId) {
                lastId = state.selectedId;
                postShift(hz);
            }
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        lastId = widget.getState().selectedId;
    };
    const originalSetOptions = widget.setOptions.bind(widget);
    widget.setOptions = (next, opts = {}) => {
        originalSetOptions(next, opts);
        lastId = widget.getState().selectedId;
    };

    return widget;
}

window.snapIfShiftHz = snapIfShiftHz;

function initAgcButton(vfo) {
    return initOffToggleDropdown(document.getElementById(`agcButton${vfo}`), {
        label: "AGC",
        options: AGC_OPTIONS,
        offId: "0",
        a11yKey: `controls.agc${vfo}`,
        onSelect: (id) => {
            if (window.radioControl?.setAgc) window.radioControl.setAgc(vfo, id);
        },
    });
}

/**
 * IPO/AMP1/AMP2: peer states only — left-click cycles; right-click picks.
 * No on/off LED (IPO is not a boolean off).
 * @param {HTMLElement | null} root
 * @param {{ label: string, options: { id: string, label: string }[], a11yKey?: string, onSelect: (id: string) => void }} cfg
 */
function initCycleDropdown(root, cfg) {
    if (!root) return null;
    const selectedId = root.dataset.selected || cfg.options[0].id;
    let lastId = selectedId;

    const widget = new ToggleDropdownButton(root, {
        label: cfg.label,
        options: cfg.options,
        selectedId,
        clickAction: "cycle",
        a11yKey: cfg.a11yKey,
        onChange: (state) => {
            if (state.selectedId !== lastId) {
                lastId = state.selectedId;
                cfg.onSelect(state.selectedId);
            }
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        lastId = widget.getState().selectedId;
    };

    return widget;
}

function initIpoButton(vfo) {
    return initCycleDropdown(document.getElementById(`ipoButton${vfo}`), {
        label: "IPO",
        options: IPO_OPTIONS,
        a11yKey: `controls.ipo${vfo}`,
        onSelect: (id) => {
            if (window.radioControl?.setIpo) window.radioControl.setIpo(vfo, id);
        },
    });
}

function initAttButton(vfo) {
    return initOffToggleDropdown(document.getElementById(`attButton${vfo}`), {
        label: "ATT",
        options: ATT_OPTIONS,
        offId: "00",
        a11yKey: `controls.att${vfo}`,
        onSelect: (id) => {
            if (window.radioControl?.setAttenuator) window.radioControl.setAttenuator(vfo, id);
        },
    });
}

function initNbButton(vfo) {
    return initToggleSlider(document.getElementById(`nbButton${vfo}`), {
        label: "NB",
        min: 1,
        max: 20,
        a11yKey: `controls.nb${vfo}`,
        onEnabled: (enabled) => {
            if (window.radioControl?.setNoiseBlanker) {
                window.radioControl.setNoiseBlanker(vfo, enabled ? "1" : "0");
            }
        },
        onValue: (value) => {
            if (typeof window.setNbLevel === "function") window.setNbLevel(vfo, value);
        },
    });
}

function initAutoNotchButton(vfo) {
    return initSimpleToggle(document.getElementById(`autoNotchButton${vfo}`), {
        label: "Auto Notch",
        a11yKey: `controls.autoNotch${vfo}`,
        onEnabled: (enabled) => {
            if (window.radioControl?.setAutoNotch) {
                window.radioControl.setAutoNotch(vfo, enabled ? "1" : "0");
            }
        },
    });
}

function initManNotchButton(vfo) {
    return initToggleSlider(document.getElementById(`manNotchButton${vfo}`), {
        label: "Notch",
        min: 10,
        max: 3200,
        step: 10,
        valueSuffix: " Hz",
        a11yKey: `controls.manualNotch${vfo}`,
        onEnabled: (enabled) => {
            if (window.radioControl?.setManualNotch) {
                window.radioControl.setManualNotch(vfo, enabled ? "1" : "0");
            }
            const panel = window[`filterScopePanel${vfo}`];
            if (panel) panel.setState({ manualNotchOn: enabled });
        },
        onValue: (value) => {
            if (window.radioControl?.setManualNotchFreq) {
                window.radioControl.setManualNotchFreq(vfo, value);
            }
            const panel = window[`filterScopePanel${vfo}`];
            if (panel) panel.setState({ manualNotchFreqHz: value });
        },
    });
}

function initContourButton(vfo) {
    return initToggleSlider(document.getElementById(`contourButton${vfo}`), {
        label: "Contour",
        min: 100,
        max: 3200,
        step: 10,
        valueSuffix: " Hz",
        a11yKey: `controls.contour${vfo}`,
        onEnabled: (enabled) => {
            if (typeof window.setContourOn === "function") window.setContourOn(vfo, enabled);
        },
        onValue: (value) => {
            if (typeof window.setContourFreq === "function") window.setContourFreq(vfo, value);
        },
    });
}

function initApfButton(vfo) {
    return initToggleSlider(document.getElementById(`apfButton${vfo}`), {
        label: "APF",
        min: -250,
        max: 250,
        step: 10,
        valueSuffix: " Hz",
        a11yKey: `controls.apf${vfo}`,
        onEnabled: (enabled) => {
            if (typeof window.setApfOn === "function") window.setApfOn(vfo, enabled);
        },
        onValue: (value) => {
            if (typeof window.setApfFreq === "function") window.setApfFreq(vfo, value);
        },
    });
}

/**
 * QMB is a radio-global command menu (Store / Recall / V/M), not a
 * selected-state key. Shown once in operating-controls column 4 as a
 * Yaesu action key matching Tune / CW / DX Spots.
 * @returns {ActionMenuButton | null}
 */
function initQmbButton() {
    const root = document.getElementById("qmbButton");
    if (!root) return null;

    const widget = new ActionMenuButton(root, {
        label: "QMB",
        a11yKey: "controls.qmb",
        title: "Quick Memory Bank — click for Store, Recall and V/M",
        // Yaesu action-key face (same as Tune / CW / DX Spots). Horizontal
        // Store / Recall / V/M strip kept via menuClass.
        menuClass: "qmb-toolbar-key__menu",
        actions: [
            {
                id: "store",
                label: "Store",
                ariaLabel: "Store current VFO to Quick Memory Bank",
                title: "Store the current VFO frequency and mode into the radio's Quick Memory Bank",
            },
            {
                id: "recall",
                label: "Recall",
                ariaLabel: "Recall from Quick Memory Bank",
                title: "Recall from the Quick Memory Bank — the display switches to QMB; repeated presses step through the stored slots. Use V/M to return to VFO.",
            },
            {
                id: "vfo",
                label: "V/M",
                ariaLabel: "Return to VFO mode (V/M key)",
                title: "Return to VFO mode — leaves QMB (mirrors the radio's V/M key).",
            },
        ],
        onAction: (id) => {
            if (id === "store") window.qmbStore?.();
            else if (id === "recall") window.qmbRecall?.();
            else if (id === "vfo") window.qmbVfo?.();
        },
    });

    window.qmbButton = widget;
    return widget;
}

/**
 * Clarifier VFO key — cycle A ↔ B on click (same key face as Apps / Mem).
 * @returns {ToggleDropdownButton | null}
 */
function initClarVfoButton() {
    const root = document.getElementById("clarVfoButton");
    if (!root) return null;

    const widget = initCycleDropdown(root, {
        label: "VFO",
        options: [
            { id: "A", label: "A" },
            { id: "B", label: "B" },
        ],
        a11yKey: "controls.clarVfo",
        onSelect: (id) => window.selectClarVfo?.(id),
    });
    root.querySelector(".toggle-dd__btn")?.classList.add("toggle-dd__btn--toolbar");
    window.clarVfoButton = widget;
    return widget;
}

/**
 * Clarifier mode key — click opens OFF / RX / TX / RX+TX.
 * LED is on whenever clarifier is not OFF.
 * @returns {ToggleDropdownButton | null}
 */
function initClarModeButton() {
    const root = document.getElementById("clarModeButton");
    if (!root) return null;

    const selectedId = root.dataset.selected || "off";
    let lastId = selectedId;

    const widget = new ToggleDropdownButton(root, {
        label: "Clar",
        options: [
            { id: "off", label: "OFF" },
            { id: "rx", label: "RX" },
            { id: "tx", label: "TX" },
            { id: "rxtx", label: "RX+TX" },
        ],
        selectedId,
        offId: "off",
        clickAction: "openMenu",
        contextMenu: false,
        showLed: true,
        a11yKey: "controls.clarMode",
        onChange: (state) => {
            if (state.selectedId !== lastId) {
                lastId = state.selectedId;
                window.setClarifierMode?.(state.selectedId);
            }
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        lastId = widget.getState().selectedId;
    };

    root.querySelector(".toggle-dd__btn")?.classList.add("toggle-dd__btn--toolbar");
    window.clarModeButton = widget;
    return widget;
}

/**
 * Clarifier offset key — left-click resets the selected VFO to zero;
 * right-click opens the offset slider. The key has no on/off LED.
 */
function initClarOffsetButton() {
    const root = document.getElementById("clarOffsetButton");
    if (!root) return null;

    const value = Math.max(-9990, Math.min(9990, Number(root.dataset.value) || 0));
    let lastValue = value;
    const widget = new CycleContextButton(root, {
        label: "Offset",
        options: [{ id: "offset", label: "Offset" }],
        selectedId: "offset",
        offId: null,
        clickAction: "reset",
        resetValue: 0,
        showLed: false,
        context: { type: "slider", min: -9990, max: 9990, step: 10, value, valueSuffix: " Hz" },
        a11yKey: "controls.clarOffset",
        onChange: (state) => {
            if (state.value === undefined || state.value === lastValue) return;
            lastValue = state.value;
            window.setClarifierOffset?.(state.value);
        },
    });

    const originalSetState = widget.setState.bind(widget);
    widget.setState = (partial = {}, opts = {}) => {
        originalSetState(partial, opts);
        lastValue = widget.getState().value;
    };

    window.clarOffsetButton = widget;
    return widget;
}

/**
 * Top-toolbar Apps key: one Yaesu action menu listing only the apps the
 * operator enabled in Application Setup. Hidden entirely when none are on.
 * @returns {ActionMenuButton | null}
 */
function initAppsButton() {
    const root = document.getElementById("appsButton");
    const cfgEl = document.getElementById("appsLauncherConfig");
    if (!root || !cfgEl) return null;

    let apps = [];
    try {
        apps = JSON.parse(cfgEl.textContent || "[]");
    } catch {
        return null;
    }
    if (!Array.isArray(apps) || apps.length === 0) return null;

    const launchers = {
        wsjtx: () => window.launchWsjtx?.(),
        jtalert: () => window.launchJtalert?.(),
        log4om: () => window.launchLog4om?.(),
        gridtracker: () => window.launchGridtracker?.(),
        fldigi: () => window.launchFldigi?.(),
    };

    const widget = new ActionMenuButton(root, {
        label: "Apps",
        a11yKey: "controls.apps",
        title: "Launch enabled applications",
        menuClass: "apps-launcher__menu",
        actions: apps.map((app) => ({
            id: String(app.id),
            label: String(app.label),
            ariaLabel: `Launch ${app.label}`,
            title: `Launch ${app.label}`,
        })),
        onAction: (id) => launchers[id]?.(),
    });

    // Compact face to match Mem / Gauges in the top toolbar.
    root.querySelector(".toggle-dd__btn")?.classList.add("toggle-dd__btn--toolbar");

    window.appsButton = widget;
    return widget;
}

export function initVfoKeyButtons() {
    const result = {};
    result.qmb = initQmbButton();
    result.apps = initAppsButton();
    result.clarVfo = initClarVfoButton();
    result.clarMode = initClarModeButton();
    result.clarOffset = initClarOffsetButton();
    for (const vfo of ["A", "B"]) {
        const band = initBandButton(vfo);
        const segment = initSegmentButton(vfo);
        const mode = initModeButton(vfo);
        const antenna = initAntennaButton(vfo);
        const roofing = initRoofingButton(vfo);
        const ifWidth = initIfWidthButton(vfo);
        const ifShift = initIfShiftButton(vfo);
        const agc = initAgcButton(vfo);
        const ipo = initIpoButton(vfo);
        const att = initAttButton(vfo);
        const nb = initNbButton(vfo);
        const autoNotch = initAutoNotchButton(vfo);
        const manNotch = initManNotchButton(vfo);
        const contour = initContourButton(vfo);
        const apf = initApfButton(vfo);

        if (band) window[`bandButton${vfo}`] = band;
        if (segment) window[`segmentButton${vfo}`] = segment;
        if (mode) window[`modeButton${vfo}`] = mode;
        if (antenna) window[`antennaButton${vfo}`] = antenna;
        if (roofing) window[`roofingButton${vfo}`] = roofing;
        if (ifWidth) window[`ifWidthButton${vfo}`] = ifWidth;
        if (ifShift) window[`ifShiftButton${vfo}`] = ifShift;
        if (agc) window[`agcButton${vfo}`] = agc;
        if (ipo) window[`ipoButton${vfo}`] = ipo;
        if (att) window[`attButton${vfo}`] = att;
        if (nb) window[`nbButton${vfo}`] = nb;
        if (autoNotch) window[`autoNotchButton${vfo}`] = autoNotch;
        if (manNotch) window[`manNotchButton${vfo}`] = manNotch;
        if (contour) window[`contourButton${vfo}`] = contour;
        if (apf) window[`apfButton${vfo}`] = apf;

        result[vfo] = {
            band,
            segment,
            mode,
            antenna,
            roofing,
            ifWidth,
            ifShift,
            agc,
            ipo,
            att,
            nb,
            autoNotch,
            manNotch,
            contour,
            apf,
        };
    }
    return result;
}
