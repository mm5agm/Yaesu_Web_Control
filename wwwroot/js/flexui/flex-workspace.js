/**
 * Flex UI workspace — YWC's configuration of the shared dock.
 *
 * The dock itself (FlexLayout model, persistence, pop-outs, scale, the Panels
 * and Layouts menus) is core/js/flex/flex-workspace.js, shared with Icom Web
 * Control and served at /js/flex/. This file says which panels YWC has, where
 * their templates are and where its layouts live; the values are exactly the
 * ones the dock used before it moved, so saved arrangements, scale and named
 * layouts all carry over.
 *
 * Panel *initialisation* lives in _FlexScripts.cshtml, which listens for the
 * mount-ready events the dock fires.
 */

import {
    initFlexWorkspace as initSharedWorkspace,
    detectScale,
    dispatchPanelResize,
} from '/js/flex/flex-workspace.js';

export { detectScale, dispatchPanelResize };

const YWC_FLEX_CONFIG = {
    layoutUrl: '/js/flexui/layouts/desktop.json',
    layoutVersion: 'v6',
    storagePrefix: 'ywc.flexui.',
    apiBase: '/api/flexlayouts',
    popoutUrl: '/popout.html',
    // Extras was split into Remote Audio and Clarifier; a layout saved by an
    // older build still carries the single 'extras' tab.
    retiredComponents: ['extras'],
    fallbackPanel: 'vfoA',
    panels: [
        { id: 'linearMeters', name: 'Linear Meters', component: 'linearMeters', template: 'tpl-linear-meters' },
        { id: 'levels', name: 'Levels', component: 'levels', template: 'tpl-levels' },
        { id: 'vfoActions', name: 'VFO Actions', component: 'vfoActions', template: 'tpl-vfo-actions' },
        { id: 'buttons', name: 'Buttons', component: 'buttons', template: 'tpl-buttons' },
        { id: 'remoteAudio', name: 'Remote Audio', component: 'remoteAudio', template: 'tpl-remote-audio', flag: 'remoteAudio', menu: 'always' },
        { id: 'clarifier', name: 'Clarifier', component: 'clarifier', template: 'tpl-clarifier' },
        // Mounting Radio Display probes the capture device and (with Auto on)
        // starts the stream, so it renders only once its tab is selected.
        { id: 'radioDisplay', name: 'Radio Display', component: 'radioDisplay', template: 'tpl-radio-display', flag: 'radioDisplay', lazy: true },
        { id: 'radioScope', name: 'Radio Scope', component: 'radioScope', template: 'tpl-radio-scope', flag: 'radioScope' },
        { id: 'spectrumA', name: 'Spectrum A', component: 'spectrumA', template: 'tpl-spectrum-a', flag: 'spectrumA' },
        { id: 'spectrumB', name: 'Spectrum B', component: 'spectrumB', template: 'tpl-spectrum-b', flag: 'spectrumB' },
        { id: 'vfoA', name: 'VFO A', component: 'vfoA', template: 'tpl-vfo-a' },
        { id: 'vfoB', name: 'VFO B', component: 'vfoB', template: 'tpl-vfo-b', flag: 'vfoB', menu: 'unlessFalse' },
    ],
};

export function initFlexWorkspace(host, flags) {
    return initSharedWorkspace(host, flags, YWC_FLEX_CONFIG);
}
