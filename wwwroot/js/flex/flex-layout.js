import { createTemplateElement } from '/js/flex/template-panel.js?v=1';
import { installDomShim, dispatchPanelResize } from '/js/flex/dom-shim.js?v=1';
import { installBootstrapShim } from '/js/flex/bootstrap-shim.js?v=1';

export { installBootstrapShim };

const PRESET_KEY = 'ywc.flexLayout.preset';
const LAYOUT_VERSION = 'v1';
const LAYOUT_URLS = {
    desktop: '/js/flex/layouts/desktop.json',
    tablet: '/js/flex/layouts/tablet.json',
    phone: '/js/flex/layouts/phone.json',
};

/** @type {Record<string, object>} */
const defaultCache = {};

/**
 * @param {'desktop'|'tablet'|'phone'} preset
 */
function storageKey(preset) {
    return `ywc.flexLayout.${preset}.${LAYOUT_VERSION}`;
}

/**
 * @returns {'desktop'|'tablet'|'phone'}
 */
export function detectPreset() {
    const w = window.innerWidth || 1280;
    const fine = window.matchMedia?.('(pointer: fine)')?.matches ?? true;
    if (w >= 1280 || (fine && w >= 1100)) return 'desktop';
    if (w >= 768) return 'tablet';
    return 'phone';
}

/**
 * @returns {'desktop'|'tablet'|'phone'}
 */
export function getActivePreset() {
    try {
        const stored = localStorage.getItem(PRESET_KEY);
        if (stored === 'desktop' || stored === 'tablet' || stored === 'phone') return stored;
    } catch { /* ignore */ }
    return detectPreset();
}

/**
 * @param {'desktop'|'tablet'|'phone'} preset
 */
export function setActivePreset(preset) {
    try { localStorage.setItem(PRESET_KEY, preset); } catch { /* ignore */ }
}

/**
 * @param {object} json
 * @param {object} flags
 */
function filterLayoutJson(json, flags) {
    const drop = new Set();
    if (!flags?.remoteAudio) drop.add('remoteAudio');
    if (!flags?.spectrumA) drop.add('spectrumA');
    if (!flags?.spectrumB) drop.add('spectrumB');
    if (!flags?.vfoB) drop.add('vfoB');
    if (drop.size === 0) return structuredClone(json);

    const clone = structuredClone(json);
    const filterChildren = (node) => {
        if (!node) return null;
        if (node.type === 'tab') {
            return drop.has(node.id) || drop.has(node.component) ? null : node;
        }
        if (Array.isArray(node.children)) {
            node.children = node.children.map(filterChildren).filter(Boolean);
        }
        if ((node.type === 'tabset' || node.type === 'border' || node.type === 'row')
            && Array.isArray(node.children) && node.children.length === 0) {
            return null;
        }
        return node;
    };

    clone.layout = filterChildren(clone.layout);
    if (!clone.layout) {
        clone.layout = {
            type: 'row',
            weight: 100,
            children: [{
                type: 'tabset',
                weight: 100,
                children: [{ type: 'tab', id: 'vfoA', name: 'VFO A', component: 'vfoA' }],
            }],
        };
    }
    if (Array.isArray(clone.borders)) {
        clone.borders = clone.borders
            .map((b) => {
                b.children = (b.children || []).map(filterChildren).filter(Boolean);
                return b.children.length ? b : null;
            })
            .filter(Boolean);
    }
    return clone;
}

/**
 * @param {'desktop'|'tablet'|'phone'} preset
 */
async function loadDefaultJson(preset) {
    if (defaultCache[preset]) return structuredClone(defaultCache[preset]);
    const res = await fetch(LAYOUT_URLS[preset]);
    if (!res.ok) throw new Error(`Failed to load ${preset} layout`);
    const json = await res.json();
    defaultCache[preset] = json;
    return structuredClone(json);
}

/**
 * @param {HTMLElement} host
 * @param {object} flags
 */
export function initFlexWorkspace(host, flags) {
    const FL = window.FlexLayout;
    const React = window.React;
    const ReactDOM = window.ReactDOM;
    if (!FL?.Model || !React || !ReactDOM?.createRoot) {
        host.textContent = 'FlexLayout library failed to load.';
        return null;
    }

    installDomShim();

    /** @type {{ model: any, root: any, layoutRef: any, flags: object, preset: string }} */
    const state = {
        model: null,
        root: null,
        layoutRef: React.createRef(),
        flags: flags || {},
        preset: getActivePreset(),
    };

    const supportsPopout = () => state.preset === 'desktop';

    const factory = (node) => createTemplateElement(node);

    function applyPresetCss(preset) {
        document.documentElement.dataset.ywcFlexPreset = preset;
        const size = preset === 'phone' ? '12px' : preset === 'tablet' ? '10px' : '8px';
        document.documentElement.style.setProperty('--flexlayout-splitter-size', size);
    }

    function persist() {
        if (!state.model) return;
        try {
            localStorage.setItem(storageKey(state.preset), JSON.stringify(state.model.toJson()));
        } catch { /* quota */ }
    }

    function renderApp() {
        if (!state.root) state.root = ReactDOM.createRoot(host);
        const App = () => React.createElement(FL.Layout, {
            model: state.model,
            factory,
            ref: state.layoutRef,
            supportsPopout: supportsPopout(),
            popoutURL: '/popout.html',
            realtimeResize: true,
            onModelChange: () => {
                persist();
                dispatchPanelResize();
            },
            onAction: (action) => {
                // Allow all actions; persist after
                queueMicrotask(() => {
                    persist();
                    dispatchPanelResize();
                });
                return action;
            },
        });
        state.root.render(React.createElement(App));
    }

    async function loadModel(preset, { reset = false } = {}) {
        applyPresetCss(preset);
        let json = null;
        if (!reset) {
            try {
                const raw = localStorage.getItem(storageKey(preset));
                if (raw) json = JSON.parse(raw);
            } catch {
                localStorage.removeItem(storageKey(preset));
            }
        }
        if (!json) {
            json = filterLayoutJson(await loadDefaultJson(preset), state.flags);
        } else {
            // Still strip panels the host cannot show (e.g. spectrum on CAT-only).
            json = filterLayoutJson(json, state.flags);
        }

        // Force popout globals from preset (saved JSON may disagree after switch).
        json.global = json.global || {};
        if (preset === 'desktop') {
            json.global.tabEnablePopout = true;
            json.global.tabEnablePopoutIcon = true;
        } else {
            json.global.tabEnablePopout = false;
            json.global.tabEnablePopoutIcon = false;
        }
        json.global.tabEnableClose = false;
        json.global.tabSetEnableMaximize = true;

        state.preset = preset;
        state.model = FL.Model.fromJson(json);
        state.model.addChangeListener(() => {
            persist();
            dispatchPanelResize();
        });
        renderApp();
        syncPresetUi();
        queueMicrotask(() => dispatchPanelResize());
    }

    function syncPresetUi() {
        const sel = document.getElementById('flexLayoutPreset');
        if (sel && sel.value !== state.preset) sel.value = state.preset;
        document.querySelectorAll('[data-flex-preset]').forEach((btn) => {
            btn.classList.toggle('ywc-active', btn.getAttribute('data-flex-preset') === state.preset);
            btn.classList.toggle('active', btn.getAttribute('data-flex-preset') === state.preset);
        });
    }

    function tabJson(id) {
        const map = {
            meters: { type: 'tab', id: 'meters', name: 'Meters', component: 'meters', enableWindowReMount: true },
            controls: { type: 'tab', id: 'controls', name: 'Controls', component: 'controls' },
            remoteAudio: { type: 'tab', id: 'remoteAudio', name: 'Remote Audio', component: 'remoteAudio' },
            spectrumA: { type: 'tab', id: 'spectrumA', name: 'Spectrum A', component: 'spectrumA', enableWindowReMount: true },
            spectrumB: { type: 'tab', id: 'spectrumB', name: 'Spectrum B', component: 'spectrumB', enableWindowReMount: true },
            vfoA: { type: 'tab', id: 'vfoA', name: 'VFO A', component: 'vfoA' },
            vfoB: { type: 'tab', id: 'vfoB', name: 'VFO B', component: 'vfoB' },
            clarifier: { type: 'tab', id: 'clarifier', name: 'Clarifier', component: 'clarifier' },
        };
        return map[id] || null;
    }

    function showPanel(id) {
        if (!state.model || state.model.getNodeById(id)) {
            if (state.model?.getNodeById(id)) {
                state.model.doAction(FL.Actions.selectTab(id));
            }
            return;
        }
        const json = tabJson(id);
        if (!json) return;
        const layout = state.layoutRef?.current;
        if (layout?.addTabToActiveTabSet) {
            try {
                layout.addTabToActiveTabSet(json);
                dispatchPanelResize();
                return;
            } catch { /* fall through */ }
        }
        const toNode =
            state.model.getActiveTabset?.()
            || state.model.getFirstTabSet?.();
        if (!toNode) return;
        try {
            state.model.doAction(
                FL.Actions.addNode(json, toNode.getId(), FL.DockLocation.CENTER, -1),
            );
        } catch { /* ignore */ }
        dispatchPanelResize();
    }

    function hidePanel(id) {
        if (!state.model?.getNodeById(id)) return;
        try {
            state.model.doAction(FL.Actions.deleteTab(id));
        } catch { /* ignore */ }
        dispatchPanelResize();
    }

    window.ywcFlex = {
        get model() { return state.model; },
        get api() { return state.model; },
        get layoutRef() { return state.layoutRef; },
        flags: state.flags,
        get preset() { return state.preset; },
        async setPreset(preset) {
            if (!LAYOUT_URLS[preset]) return;
            setActivePreset(preset);
            await loadModel(preset);
        },
        async resetLayout() {
            try { localStorage.removeItem(storageKey(state.preset)); } catch { /* ignore */ }
            await loadModel(state.preset, { reset: true });
        },
        showPanel,
        hidePanel,
        togglePanel(id) {
            if (state.model?.getNodeById(id)) hidePanel(id);
            else showPanel(id);
        },
    };

    wireToolbar();
    // Fire-and-forget initial load
    loadModel(state.preset).catch((err) => {
        console.error(err);
        host.textContent = 'Failed to load Flex layout.';
    });

    return window.ywcFlex;
}

function wireToolbar() {
    document.getElementById('flexResetLayoutBtn')?.addEventListener('click', () => {
        window.ywcFlex?.resetLayout();
    });

    const presetSel = document.getElementById('flexLayoutPreset');
    presetSel?.addEventListener('change', () => {
        const v = presetSel.value;
        if (v === 'desktop' || v === 'tablet' || v === 'phone') {
            window.ywcFlex?.setPreset(v);
        }
    });

    document.querySelectorAll('[data-flex-preset]').forEach((btn) => {
        btn.addEventListener('click', () => {
            const v = btn.getAttribute('data-flex-preset');
            if (v === 'desktop' || v === 'tablet' || v === 'phone') {
                window.ywcFlex?.setPreset(v);
            }
        });
    });

    document.getElementById('vfoBToggleBtn')?.addEventListener('click', () => {
        const visible = !!window.ywcFlex?.model?.getNodeById('vfoB');
        if (visible) {
            window.ywcFlex.hidePanel('vfoB');
            try { localStorage.setItem('vfoBVisible', 'false'); } catch { /* ignore */ }
            const btn = document.getElementById('vfoBToggleBtn');
            if (btn) btn.textContent = 'Show B';
        } else {
            window.ywcFlex.showPanel('vfoB');
            try { localStorage.setItem('vfoBVisible', 'true'); } catch { /* ignore */ }
            const btn = document.getElementById('vfoBToggleBtn');
            if (btn) btn.textContent = 'Hide B';
        }
        dispatchPanelResize();
    });

    const vfoBStored = (() => {
        try { return localStorage.getItem('vfoBVisible'); } catch { return null; }
    })();
    if (vfoBStored === 'false') {
        // Delay until model exists
        const t = setInterval(() => {
            if (!window.ywcFlex?.model) return;
            clearInterval(t);
            if (window.ywcFlex.model.getNodeById('vfoB')) {
                window.ywcFlex.hidePanel('vfoB');
                const btn = document.getElementById('vfoBToggleBtn');
                if (btn) btn.textContent = 'Show B';
            }
        }, 50);
        setTimeout(() => clearInterval(t), 5000);
    }
}
