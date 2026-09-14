/**
 * Wire Yaesu-key widgets for Band, Mode, AGC, IPO, ATT, NB, Auto Notch, Man Notch.
 */
import {
    ToggleDropdownButton,
    ToggleSliderButton,
    ToggleButton,
} from "/js/ui/toggle-dropdown-button.js";

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
 * Menu-only key (Band / Mode): left-click opens menu; right-click disabled.
 * @param {HTMLElement | null} root
 * @param {{ label: string, options: { id: string, label: string }[], a11yKey?: string, onSelect: (id: string) => void }} cfg
 */
function initMenuOnlyButton(root, cfg) {
    if (!root) return null;
    const selectedId = root.dataset.selected || cfg.options[0].id;
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
 * ToggleSlider (NB / Man Notch).
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
    const value = Math.min(
        cfg.max,
        Math.max(cfg.min, parseInt(root.dataset.value, 10) || cfg.min)
    );

    let lastEnabled = enabled;
    let lastValue = value;

    const postValue = debounce((v) => cfg.onValue(v), 150);

    const widget = new ToggleSliderButton(root, {
        label: cfg.label,
        min: cfg.min,
        max: cfg.max,
        step: cfg.step ?? 1,
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
            // Segment dropdown used to refresh from the band-radio change
            // listener; drive it from the menu pick so it still updates before
            // the SignalR Band* round-trip.
            if (typeof window.populateSegmentSelect === "function" && window.bandPlanData) {
                window.populateSegmentSelect(vfo, id);
            }
        },
    });
}

function initModeButton(vfo) {
    return initMenuOnlyButton(document.getElementById(`modeButton${vfo}`), {
        label: "Mode",
        options: MODE_OPTIONS,
        a11yKey: `vfo.${vfo.toLowerCase()}.mode`,
        onSelect: (id) => {
            if (typeof window.setMode === "function") window.setMode(vfo, id);
        },
    });
}

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

export function initVfoKeyButtons() {
    const result = {};
    for (const vfo of ["A", "B"]) {
        const band = initBandButton(vfo);
        const mode = initModeButton(vfo);
        const agc = initAgcButton(vfo);
        const ipo = initIpoButton(vfo);
        const att = initAttButton(vfo);
        const nb = initNbButton(vfo);
        const autoNotch = initAutoNotchButton(vfo);
        const manNotch = initManNotchButton(vfo);

        if (band) window[`bandButton${vfo}`] = band;
        if (mode) window[`modeButton${vfo}`] = mode;
        if (agc) window[`agcButton${vfo}`] = agc;
        if (ipo) window[`ipoButton${vfo}`] = ipo;
        if (att) window[`attButton${vfo}`] = att;
        if (nb) window[`nbButton${vfo}`] = nb;
        if (autoNotch) window[`autoNotchButton${vfo}`] = autoNotch;
        if (manNotch) window[`manNotchButton${vfo}`] = manNotch;

        result[vfo] = { band, mode, agc, ipo, att, nb, autoNotch, manNotch };
    }
    return result;
}
