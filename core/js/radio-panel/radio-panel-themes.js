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

export const THEMES = { console: CONSOLE, night: NIGHT };
export const DEFAULT_THEME = 'console';

export function themeIds() { return Object.keys(THEMES); }

// The theme with every key present: unknown ids get the default, missing keys
// get the default's value.
export function resolveTheme(id) {
    const t = THEMES[id] ?? THEMES[DEFAULT_THEME];
    return {
        id: THEMES[id] ? id : DEFAULT_THEME,
        label: t.label,
        vars: { ...CONSOLE.vars, ...t.vars },
        waterfall: t.waterfall?.length >= 2 ? t.waterfall : CONSOLE.waterfall,
    };
}

// Write a theme's colours onto `root` as --rp-* custom properties.
export function applyTheme(root, id) {
    const t = resolveTheme(id);
    for (const [k, v] of Object.entries(t.vars)) root.style.setProperty('--rp-' + k, v);
    root.dataset.rpTheme = t.id;
    return t;
}
