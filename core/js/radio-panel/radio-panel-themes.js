// radio-panel-themes.js - colour themes for the radio-style front panel.
//
// Every colour the panel draws comes from here. The stylesheet
// (radio-panel-style.js) refers only to the --rp-* custom properties below, and
// the canvas parts - spectrum trace, waterfall, meter face - read the same
// theme object, so a theme is a complete description and nothing is left
// painted in the old colours when the operator switches.
//
// Adding a theme: copy `console`, change the values, give it a label. Every key
// must be present; applyTheme() falls back to `console` for any that are not,
// so a half-written theme still draws but shows where it is unfinished.

const CONSOLE = {
    label: 'Dark console',
    vars: {
        page: '#0c0d0f', text: '#e1e5ea', title: '#ffffff',
        panel: '#1c1e22', 'panel-border': '#34373d',
        legend: '#c3c9d0', 'legend-dim': '#9aa1a9', sub: '#a3aab2',
        accent: '#ffb02e', 'accent-soft': '#ffd58a', 'accent-bg': '#3a2c12',
        // The VFO the main dial tunes: A, and B in a hue that cannot pass for A.
        'vfo-a': '#ffb02e', 'vfo-b': '#5cc8ff',

        'key-bg': '#2a2d32', 'key-border': '#3c4048', 'key-shadow': '#111215', 'key-fg': '#e1e5ea',
        'led-off': '#3d3220', 'led-on': '#ffb02e',
        tx: '#ff4b3a', 'tx-fg': '#1a0503',
        'lcd-bg': '#111214', 'lcd-border': '#30333a', 'lcd-fg': '#ffcf73',
        'chipbtn-bg': '#1f2125', 'chipbtn-fg': '#c9cfd6',
        'lamp-off': '#2c2f34', 'lamp-on': '#49e08f',
        'bar-bg': '#15171a', 'bar-on': '#2f3238', 'well-border': '#2c2f35',
        well: '#0a0b0d', 'meter-well': '#0b0c0e',

        screen: '#061019', 'screen-line': '#12283a',
        freq: '#f2f8ff', 'freq-small': '#9fc6e0',
        'sub-label': '#8fb6cf', 'sub-freq': '#c9dcea', 'sub-freq-small': '#7fa3bb',
        highlight: '#5fd3ff', 'sbar-track': '#0e2333',
        'chip-border': '#2b5470', 'chip-fg': '#c6e6fa',
        'chip-dim-border': '#1d3a4f', 'chip-dim-fg': '#6f93ab',
        rx: '#3fb37f', 'rx-fg': '#86f2c0',
        'soft-bg': '#0c1d2b', 'soft-border': '#1f4058', 'soft-fg': '#d3eaf9',
        'soft-on-bg': '#13405f', 'soft-on-fg': '#ffffff',

        'spec-bg': '#040b12', 'spec-grid': '#10283a', 'spec-trace': '#7fdcff',
        'spec-fill': 'rgba(95,211,255,.16)', 'spec-label': '#6f93ab', axis: '#7fa3bb',
        passband: 'rgba(255,176,46,.18)', 'passband-edge': 'rgba(255,176,46,.6)',
        cursor: '#ff7a2e', 'spot-bg': '#ffd166', 'spot-fg': '#04121c',

        'meter-face': '#f1dca6', 'meter-ink': '#2a2116', 'meter-ink-2': '#5a4a2e',
        'meter-red': '#b3261e', needle: '#141414', 'meter-hub': '#24262a',

        'dial-a': '#30333a', 'dial-b': '#1a1c20', 'dial-rim': '#3a3e45',
        'dial-inner': '#26292e', 'dial-dimple': '#1d1f23',
        'knob-outer': '#15171a', 'knob-mid': '#2c2f35', 'knob-ring': '#41454d',
        'knob-cap': '#25282d', 'knob-mark': '#ffb02e',
    },
    waterfall: [[4, 16, 28], [10, 58, 107], [29, 143, 209], [127, 227, 255], [255, 224, 102]],
};

// Red on black, for operating in a dark shack without losing night vision.
const NIGHT = {
    label: 'Night (red)',
    vars: {
        page: '#000000', text: '#d8584a', title: '#ff6a55',
        panel: '#0b0606', 'panel-border': '#2a1210',
        legend: '#b84a3e', 'legend-dim': '#8a3a30', sub: '#9a4236',
        accent: '#ff4a36', 'accent-soft': '#ff8a78', 'accent-bg': '#2a0c08',
        'vfo-a': '#ff4a36', 'vfo-b': '#ffa898',

        'key-bg': '#160909', 'key-border': '#2e1411', 'key-shadow': '#000000', 'key-fg': '#d8584a',
        'led-off': '#2a0e0b', 'led-on': '#ff4a36',
        tx: '#ff2a1a', 'tx-fg': '#000000',
        'lcd-bg': '#0a0303', 'lcd-border': '#2a1210', 'lcd-fg': '#ff6a55',
        'chipbtn-bg': '#120707', 'chipbtn-fg': '#c04e40',
        'lamp-off': '#220b09', 'lamp-on': '#ff4a36',
        'bar-bg': '#0d0505', 'bar-on': '#2a0e0b', 'well-border': '#2a1210',
        well: '#050202', 'meter-well': '#050202',

        screen: '#070202', 'screen-line': '#2a0e0b',
        freq: '#ff6a55', 'freq-small': '#c04e40',
        'sub-label': '#a8463a', 'sub-freq': '#d8584a', 'sub-freq-small': '#9a4236',
        highlight: '#ff4a36', 'sbar-track': '#200907',
        'chip-border': '#4a1a14', 'chip-fg': '#e0604e',
        'chip-dim-border': '#2e100c', 'chip-dim-fg': '#8a3a30',
        rx: '#a83a2c', 'rx-fg': '#ff7a66',
        'soft-bg': '#100505', 'soft-border': '#3a140f', 'soft-fg': '#d8584a',
        'soft-on-bg': '#3a0f0a', 'soft-on-fg': '#ff8a78',

        'spec-bg': '#030000', 'spec-grid': '#230a08', 'spec-trace': '#ff5a44',
        'spec-fill': 'rgba(255,74,54,.14)', 'spec-label': '#8a3a30', axis: '#a8463a',
        passband: 'rgba(255,74,54,.16)', 'passband-edge': 'rgba(255,74,54,.55)',
        cursor: '#ff8a40', 'spot-bg': '#7a1e14', 'spot-fg': '#ffb0a0',

        'meter-face': '#1a0806', 'meter-ink': '#e0604e', 'meter-ink-2': '#a8463a',
        'meter-red': '#ff2a1a', needle: '#ffb0a0', 'meter-hub': '#0d0505',

        'dial-a': '#1e0b09', 'dial-b': '#0d0505', 'dial-rim': '#2e1411',
        'dial-inner': '#140707', 'dial-dimple': '#0a0303',
        'knob-outer': '#0a0303', 'knob-mid': '#1a0909', 'knob-ring': '#2e1411',
        'knob-cap': '#120606', 'knob-mark': '#ff4a36',
    },
    waterfall: [[0, 0, 0], [40, 4, 2], [110, 14, 8], [210, 40, 24], [255, 140, 90]],
};

// Brushed silver body, teal VFD and cream meter - mock-up B.
const SILVER = {
    label: 'Silver classic',
    vars: {
        page: '#8e9297', text: '#1e2226', title: '#0d0f11',
        panel: '#c4c8cd', 'panel-border': '#9ca0a5',
        legend: '#2a2e33', 'legend-dim': '#555a60', sub: '#41464c',
        accent: '#8a5a00', 'accent-soft': '#b07a1a', 'accent-bg': '#e6d3a4',
        'vfo-a': '#8a5a00', 'vfo-b': '#0b4f8a',

        'key-bg': '#2a2c30', 'key-border': '#15171a', 'key-shadow': '#7d8186', 'key-fg': '#f2f4f6',
        'led-off': '#4a3d22', 'led-on': '#ffb02e',
        tx: '#d0281c', 'tx-fg': '#ffffff',
        'lcd-bg': '#031210', 'lcd-border': '#0f2e27', 'lcd-fg': '#7cf7d4',
        'chipbtn-bg': '#d3d6da', 'chipbtn-fg': '#1e2226',
        'lamp-off': '#8a8e93', 'lamp-on': '#1fa36a',
        'bar-bg': '#b2b6bb', 'bar-on': '#9ca0a5', 'well-border': '#8e9297',
        well: '#22252a', 'meter-well': '#1b1c1f',

        screen: '#031210', 'screen-line': '#0f2e27',
        freq: '#8ffbe0', 'freq-small': '#4fc2a3',
        'sub-label': '#3d8f7a', 'sub-freq': '#7cf7d4', 'sub-freq-small': '#4fb79c',
        highlight: '#5fd9bb', 'sbar-track': '#0a241f',
        'chip-border': '#1f5a4c', 'chip-fg': '#8ffbe0',
        'chip-dim-border': '#123a31', 'chip-dim-fg': '#3d8f7a',
        rx: '#3d8f7a', 'rx-fg': '#8ffbe0',
        'soft-bg': '#0a1f1b', 'soft-border': '#1f5a4c', 'soft-fg': '#bff7e8',
        'soft-on-bg': '#173c34', 'soft-on-fg': '#ffffff',

        'spec-bg': '#020c0a', 'spec-grid': '#0f2e27', 'spec-trace': '#7cf7d4',
        'spec-fill': 'rgba(124,247,212,.14)', 'spec-label': '#3d8f7a', axis: '#4fb79c',
        passband: 'rgba(255,176,46,.16)', 'passband-edge': 'rgba(255,176,46,.55)',
        cursor: '#ff9a4a', 'spot-bg': '#f3e2b0', 'spot-fg': '#1b1c1f',

        'meter-face': '#f3e2b0', 'meter-ink': '#2a2116', 'meter-ink-2': '#5c4a26',
        'meter-red': '#b3261e', needle: '#141414', 'meter-hub': '#2a2c30',

        'dial-a': '#4a4e54', 'dial-b': '#2a2d31', 'dial-rim': '#5a5e64',
        'dial-inner': '#3a3d42', 'dial-dimple': '#24272b',
        'knob-outer': '#22252a', 'knob-mid': '#45484e', 'knob-ring': '#5a5e64',
        'knob-cap': '#2e3135', 'knob-mark': '#ffb02e',
    },
    waterfall: [[2, 12, 10], [8, 52, 44], [30, 140, 112], [124, 247, 212], [255, 224, 140]],
};

// Light, for a sunny room or a laptop by the window.
const DAYLIGHT = {
    label: 'Daylight',
    vars: {
        page: '#dfe3e8', text: '#1b2128', title: '#0a0e12',
        panel: '#f6f7f9', 'panel-border': '#c3c9d1',
        legend: '#2d343c', 'legend-dim': '#5b636d', sub: '#46505a',
        accent: '#c4520a', 'accent-soft': '#e07a30', 'accent-bg': '#fde6d2',
        'vfo-a': '#c4520a', 'vfo-b': '#1a5fb4',

        'key-bg': '#e6e9ed', 'key-border': '#b9c0c8', 'key-shadow': '#a9b0b8', 'key-fg': '#1b2128',
        'led-off': '#d6cbbd', 'led-on': '#e8660c',
        tx: '#d42a1c', 'tx-fg': '#ffffff',
        'lcd-bg': '#ffffff', 'lcd-border': '#c3c9d1', 'lcd-fg': '#a84308',
        'chipbtn-bg': '#ffffff', 'chipbtn-fg': '#2d343c',
        'lamp-off': '#c3c9d1', 'lamp-on': '#1a9a5a',
        'bar-bg': '#e9ecf0', 'bar-on': '#d3d8de', 'well-border': '#c3c9d1',
        well: '#eef0f3', 'meter-well': '#e6e9ed',

        screen: '#ffffff', 'screen-line': '#d6dce3',
        freq: '#0a1a2a', 'freq-small': '#3a5a78',
        'sub-label': '#3a5a78', 'sub-freq': '#1b3550', 'sub-freq-small': '#5a7896',
        highlight: '#0b6ea8', 'sbar-track': '#dfe7ee',
        'chip-border': '#7fa9c9', 'chip-fg': '#0b4a75',
        'chip-dim-border': '#c9d6e1', 'chip-dim-fg': '#7d93a6',
        rx: '#1a9a5a', 'rx-fg': '#11804a',
        'soft-bg': '#eef4f9', 'soft-border': '#b7cde0', 'soft-fg': '#163a5a',
        'soft-on-bg': '#0b6ea8', 'soft-on-fg': '#ffffff',

        'spec-bg': '#fbfcfd', 'spec-grid': '#e1e7ee', 'spec-trace': '#0b6ea8',
        'spec-fill': 'rgba(11,110,168,.14)', 'spec-label': '#5b6f82', axis: '#46505a',
        passband: 'rgba(232,102,12,.14)', 'passband-edge': 'rgba(232,102,12,.6)',
        cursor: '#d42a1c', 'spot-bg': '#0b4a75', 'spot-fg': '#ffffff',

        'meter-face': '#fbf6e8', 'meter-ink': '#1b2128', 'meter-ink-2': '#5b636d',
        'meter-red': '#c42218', needle: '#141414', 'meter-hub': '#46505a',

        'dial-a': '#eef0f3', 'dial-b': '#c9cfd6', 'dial-rim': '#a9b0b8',
        'dial-inner': '#dde1e6', 'dial-dimple': '#c3c9d1',
        'knob-outer': '#b9c0c8', 'knob-mid': '#e6e9ed', 'knob-ring': '#a9b0b8',
        'knob-cap': '#f6f7f9', 'knob-mark': '#e8660c',
    },
    waterfall: [[251, 252, 253], [190, 214, 234], [80, 150, 205], [20, 70, 150], [190, 30, 20]],
};

// Green phosphor on black, like an older radio's display.
const GREEN = {
    label: 'Green display',
    vars: {
        page: '#020604', text: '#9be8a8', title: '#c8ffd2',
        panel: '#0a120c', 'panel-border': '#1c3322',
        legend: '#7ccf8a', 'legend-dim': '#4f8a5a', sub: '#62a86e',
        accent: '#5cff7a', 'accent-soft': '#a0ffb0', 'accent-bg': '#0f2a15',
        'vfo-a': '#5cff7a', 'vfo-b': '#ffe066',

        'key-bg': '#111c13', 'key-border': '#24402a', 'key-shadow': '#000000', 'key-fg': '#9be8a8',
        'led-off': '#14301a', 'led-on': '#5cff7a',
        tx: '#ff5a3a', 'tx-fg': '#000000',
        'lcd-bg': '#030904', 'lcd-border': '#1c3322', 'lcd-fg': '#6dff88',
        'chipbtn-bg': '#0c160e', 'chipbtn-fg': '#8bd898',
        'lamp-off': '#14261a', 'lamp-on': '#5cff7a',
        'bar-bg': '#08100a', 'bar-on': '#14261a', 'well-border': '#1c3322',
        well: '#030704', 'meter-well': '#040905',

        screen: '#020a04', 'screen-line': '#103018',
        freq: '#8dff9f', 'freq-small': '#4fc464',
        'sub-label': '#4fa860', 'sub-freq': '#7be08c', 'sub-freq-small': '#4f9a5c',
        highlight: '#5cff7a', 'sbar-track': '#0a2410',
        'chip-border': '#1f5a2c', 'chip-fg': '#8dff9f',
        'chip-dim-border': '#143a1c', 'chip-dim-fg': '#4f8a5a',
        rx: '#2fa84a', 'rx-fg': '#c8ffd2',
        'soft-bg': '#071a0c', 'soft-border': '#1f5a2c', 'soft-fg': '#a0ffb0',
        'soft-on-bg': '#14461f', 'soft-on-fg': '#e0ffe6',

        'spec-bg': '#010603', 'spec-grid': '#0e2a14', 'spec-trace': '#6dff88',
        'spec-fill': 'rgba(92,255,122,.13)', 'spec-label': '#4f8a5a', axis: '#62a86e',
        passband: 'rgba(92,255,122,.12)', 'passband-edge': 'rgba(92,255,122,.5)',
        cursor: '#ffd23a', 'spot-bg': '#1f5a2c', 'spot-fg': '#e0ffe6',

        'meter-face': '#071a0c', 'meter-ink': '#8dff9f', 'meter-ink-2': '#4fa860',
        'meter-red': '#ff5a3a', needle: '#e0ffe6', 'meter-hub': '#14261a',

        'dial-a': '#1a261c', 'dial-b': '#0a120c', 'dial-rim': '#24402a',
        'dial-inner': '#121c14', 'dial-dimple': '#08100a',
        'knob-outer': '#08100a', 'knob-mid': '#16241a', 'knob-ring': '#24402a',
        'knob-cap': '#101a12', 'knob-mark': '#5cff7a',
    },
    waterfall: [[1, 6, 3], [6, 40, 14], [20, 110, 40], [92, 220, 120], [220, 255, 200]],
};

// Black, white and yellow: the most contrast and the least glare.
const CONTRAST = {
    label: 'High contrast',
    vars: {
        page: '#000000', text: '#ffffff', title: '#ffffff',
        panel: '#000000', 'panel-border': '#ffffff',
        legend: '#ffffff', 'legend-dim': '#d0d0d0', sub: '#e0e0e0',
        accent: '#ffd400', 'accent-soft': '#ffe866', 'accent-bg': '#332a00',
        'vfo-a': '#ffd400', 'vfo-b': '#00e5ff',

        'key-bg': '#000000', 'key-border': '#ffffff', 'key-shadow': '#000000', 'key-fg': '#ffffff',
        'led-off': '#333333', 'led-on': '#ffd400',
        tx: '#ff2020', 'tx-fg': '#ffffff',
        'lcd-bg': '#000000', 'lcd-border': '#ffffff', 'lcd-fg': '#ffd400',
        'chipbtn-bg': '#000000', 'chipbtn-fg': '#ffffff',
        'lamp-off': '#333333', 'lamp-on': '#00ff66',
        'bar-bg': '#000000', 'bar-on': '#444444', 'well-border': '#ffffff',
        well: '#000000', 'meter-well': '#000000',

        screen: '#000000', 'screen-line': '#ffffff',
        freq: '#ffffff', 'freq-small': '#ffd400',
        'sub-label': '#ffd400', 'sub-freq': '#ffffff', 'sub-freq-small': '#d0d0d0',
        highlight: '#00e5ff', 'sbar-track': '#222222',
        'chip-border': '#ffffff', 'chip-fg': '#ffffff',
        'chip-dim-border': '#666666', 'chip-dim-fg': '#a0a0a0',
        rx: '#00c853', 'rx-fg': '#00ff66',
        'soft-bg': '#000000', 'soft-border': '#ffffff', 'soft-fg': '#ffffff',
        'soft-on-bg': '#ffd400', 'soft-on-fg': '#000000',

        'spec-bg': '#000000', 'spec-grid': '#333333', 'spec-trace': '#ffff00',
        'spec-fill': 'rgba(255,255,0,.18)', 'spec-label': '#ffffff', axis: '#ffffff',
        passband: 'rgba(0,229,255,.18)', 'passband-edge': 'rgba(0,229,255,.9)',
        cursor: '#ff2020', 'spot-bg': '#ffffff', 'spot-fg': '#000000',

        'meter-face': '#000000', 'meter-ink': '#ffffff', 'meter-ink-2': '#d0d0d0',
        'meter-red': '#ff2020', needle: '#ffd400', 'meter-hub': '#ffffff',

        'dial-a': '#222222', 'dial-b': '#000000', 'dial-rim': '#ffffff',
        'dial-inner': '#111111', 'dial-dimple': '#444444',
        'knob-outer': '#000000', 'knob-mid': '#222222', 'knob-ring': '#ffffff',
        'knob-cap': '#111111', 'knob-mark': '#ffd400',
    },
    waterfall: [[0, 0, 0], [0, 0, 160], [0, 200, 255], [255, 255, 0], [255, 255, 255]],
};

export const THEMES = {
    console: CONSOLE, night: NIGHT, silver: SILVER, daylight: DAYLIGHT, green: GREEN, contrast: CONTRAST,
};
export const DEFAULT_THEME = 'console';

// Not a theme of its own: follows the computer's light/dark setting, drawing
// with Dark console or Daylight, and switches when the computer does.
export const SYSTEM_THEME = 'system';
const SYSTEM_DARK = 'console', SYSTEM_LIGHT = 'daylight';

// What the picker offers, in order: the themes, then "Match computer".
export function themeIds() { return [...Object.keys(THEMES), SYSTEM_THEME]; }

// The theme with every key present: unknown ids get the default, missing keys
// get the default's value. `system` draws as Dark console or Daylight by
// `prefersDark` but keeps `system` as its id, so the picker still shows it;
// `applied` is the theme actually drawn.
export function resolveTheme(id, prefersDark = true) {
    if (id === SYSTEM_THEME) {
        const t = resolveTheme(prefersDark ? SYSTEM_DARK : SYSTEM_LIGHT);
        return { ...t, id: SYSTEM_THEME, label: 'Match computer' };
    }
    const t = THEMES[id] ?? THEMES[DEFAULT_THEME];
    const realId = THEMES[id] ? id : DEFAULT_THEME;
    return {
        id: realId,
        applied: realId,
        label: t.label,
        vars: { ...CONSOLE.vars, ...t.vars },
        waterfall: t.waterfall?.length >= 2 ? t.waterfall : CONSOLE.waterfall,
    };
}

export function systemPrefersDark() {
    try { return !window.matchMedia('(prefers-color-scheme: light)').matches; } catch { return true; }
}

// Write a theme's colours onto `root` as --rp-* custom properties.
export function applyTheme(root, id) {
    const t = resolveTheme(id, systemPrefersDark());
    for (const [k, v] of Object.entries(t.vars)) root.style.setProperty('--rp-' + k, v);
    root.dataset.rpTheme = t.applied;
    return t;
}
