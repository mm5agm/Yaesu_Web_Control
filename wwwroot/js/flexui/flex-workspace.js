/**
 * Flex UI workspace — builds a Caplin FlexLayout dock from the panel
 * <template>s rendered by Pages/FlexUi.cshtml.
 *
 * "Layout" here means panel *position*: a single default arrangement that the
 * user may rearrange by dragging, persisted in localStorage. It is deliberately
 * NOT exposed as a choice of preset arrangements — multiple arrangements
 * (phone/tablet/desktop/ultrawide) are a future concern.
 *
 * What the toolbar exposes is **scale**: a UI size (x-small / small / medium /
 * large) that changes font and component size only. Changing scale never moves
 * a panel.
 *
 * The classic Index page is untouched; this module only runs at /flexui. It
 * owns layout persistence, panel templates -> React elements, tab show/hide
 * (so a closed tab can be reopened without a reload), scale, and toolbar
 * wiring. Panel *initialisation* lives in _FlexScripts.cshtml, which listens
 * for the mount-ready event fired below.
 */

const LAYOUT_URL = '/js/flexui/layouts/desktop.json';
const LAYOUT_VERSION = 'v4';
const ARRANGEMENT_KEY = `ywc.flexui.layout.${LAYOUT_VERSION}`;
const SCALE_KEY = 'ywc.flexui.scale';

/** Root font-size in px for each scale. rem-based Bootstrap/theme sizing scales. */
const SCALES = { xsmall: 12, small: 14, medium: 16, large: 18 };
const SCALE_ORDER = ['xsmall', 'small', 'medium', 'large'];

/** Component -> template element id. */
const TEMPLATE_BY_COMPONENT = {
    linearMeters: 'tpl-linear-meters',
    levels: 'tpl-levels',
    vfoActions: 'tpl-vfo-actions',
    buttons: 'tpl-buttons',
    remoteAudio: 'tpl-remote-audio',
    clarifier: 'tpl-clarifier',
    vfoA: 'tpl-vfo-a',
    vfoB: 'tpl-vfo-b',
    spectrumA: 'tpl-spectrum-a',
    spectrumB: 'tpl-spectrum-b',
    radioDisplay: 'tpl-radio-display',
};

/** Every panel the workspace can show. Order drives the Panels menu. */
const PANELS = [
    { id: 'linearMeters', name: 'Linear Meters', component: 'linearMeters' },
    { id: 'levels', name: 'Levels', component: 'levels' },
    { id: 'vfoActions', name: 'VFO Actions', component: 'vfoActions' },
    { id: 'buttons', name: 'Buttons', component: 'buttons' },
    { id: 'remoteAudio', name: 'Remote Audio', component: 'remoteAudio' },
    { id: 'clarifier', name: 'Clarifier', component: 'clarifier' },
    { id: 'radioDisplay', name: 'Radio Display', component: 'radioDisplay' },
    { id: 'spectrumA', name: 'Spectrum A', component: 'spectrumA' },
    { id: 'spectrumB', name: 'Spectrum B', component: 'spectrumB' },
    { id: 'vfoA', name: 'VFO A', component: 'vfoA' },
    { id: 'vfoB', name: 'VFO B', component: 'vfoB' },
];

let defaultLayoutCache = null;

/** @returns {'xsmall'|'small'|'medium'|'large'} */
export function detectScale() {
    const w = window.innerWidth || 1280;
    if (w >= 2000) return 'large';
    if (w >= 1100) return 'medium';
    if (w >= 800) return 'small';
    return 'xsmall';
}

function getActiveScale() {
    try {
        const stored = localStorage.getItem(SCALE_KEY);
        if (stored && SCALES[stored]) return stored;
    } catch { /* ignore */ }
    return detectScale();
}

function setActiveScale(scale) {
    try { localStorage.setItem(SCALE_KEY, scale); } catch { /* ignore */ }
}

/** Drop panels the host cannot currently show (no SDR, video off, ...). */
function filterLayoutJson(json, flags) {
    const drop = new Set();
    if (!flags?.remoteAudio) drop.add('remoteAudio');
    if (!flags?.spectrumA) drop.add('spectrumA');
    if (!flags?.spectrumB) drop.add('spectrumB');
    if (!flags?.vfoB) drop.add('vfoB');
    if (!flags?.radioDisplay) drop.add('radioDisplay');
    // Radio Scope panel was removed; scrub it from layouts saved by older builds.
    drop.add('radioScope');
    // Extras was split into Remote Audio and Clarifier; a layout saved by an
    // older build still carries the single 'extras' tab.
    drop.add('extras');

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
    return clone;
}

async function loadDefaultJson() {
    if (defaultLayoutCache) return structuredClone(defaultLayoutCache);
    const res = await fetch(`${LAYOUT_URL}?v=${encodeURIComponent(document.querySelector('meta[name="x-app-version"]')?.content || '')}`);
    if (!res.ok) throw new Error('Failed to load flex layout');
    defaultLayoutCache = await res.json();
    return structuredClone(defaultLayoutCache);
}

/**
 * One live DOM node per component, kept alive across tab close/reopen.
 *
 * Closing a tab unmounts the React panel, but destroying the DOM would throw
 * away every initialised gauge, canvas and listener (and re-init is far from
 * idempotent). Instead the node is parked in a hidden container still attached
 * to the document, so document.getElementById() keeps resolving it and live
 * SignalR updates keep flowing while the tab is closed. Reopening simply moves
 * the same node back into the new tab host.
 */
const panelNodes = new Map();

function parkingArea() {
    let area = document.getElementById('ywcFlexParking');
    if (!area) {
        area = document.createElement('div');
        area.id = 'ywcFlexParking';
        area.hidden = true;
        area.setAttribute('aria-hidden', 'true');
        area.style.display = 'none';
        document.body.appendChild(area);
    }
    return area;
}

/** Build a React element that mounts the panel's template content. */
function createTemplateElement(node) {
    const React = window.React;
    const component = node.getComponent();
    return React.createElement(TemplatePanel, {
        key: node.getId(),
        templateId: TEMPLATE_BY_COMPONENT[component] || 'tpl-empty',
        component,
        nodeId: node.getId(),
    });
}

function TemplatePanel(props) {
    const React = window.React;
    const { templateId, component, nodeId } = props;
    const ref = React.useRef(null);

    React.useEffect(() => {
        const host = ref.current;
        if (!host) return undefined;

        let node = panelNodes.get(component);
        if (!node) {
            const tpl = document.getElementById(templateId);
            node = document.createElement('div');
            node.className = 'ywc-panel-body';
            node.setAttribute('data-ywc-component', component);
            if (!tpl?.content) {
                node.textContent = `Missing template #${templateId}`;
            } else {
                node.appendChild(tpl.content.cloneNode(true));
            }
            panelNodes.set(component, node);
            host.appendChild(node);
            try {
                window.dispatchEvent(new CustomEvent('ywc-flex-template-mounted', {
                    detail: { component, nodeId },
                }));
            } catch { /* ignore */ }
        } else {
            host.appendChild(node);
            try {
                window.dispatchEvent(new CustomEvent('ywc-flex-panel-attached', {
                    detail: { component, nodeId },
                }));
            } catch { /* ignore */ }
        }

        const notify = () => dispatchPanelResize();
        requestAnimationFrame(notify);
        return () => {
            // Park, do not destroy — see panelNodes note above.
            try { parkingArea().appendChild(node); } catch { /* ignore */ }
        };
    }, [templateId, component, nodeId]);

    return React.createElement('div', {
        ref,
        className: 'ywc-flex-panel-host',
        'data-ywc-component': component,
        style: { height: '100%', width: '100%' },
    });
}

export function dispatchPanelResize() {
    window.dispatchEvent(new Event('resize'));
    window.dispatchEvent(new Event('ywc-flex-panel-resize'));
}

// ── VFO tab titles ──────────────────────────────────────────────────────────
// The VFO card header is gone on Flex UI, so the tab carries the state the
// header used to show: which VFO is the active (MAIN/SUB or RX) one, and which
// is transmitting. site.js already paints those into the (now visually-hidden)
// #receiverAHeading/#receiverBHeading and the .vfo-active class, so we mirror
// that state onto the FlexLayout tab name instead of duplicating the logic.
const _lastTabTitles = {};

function setTabTitle(id, name) {
    const model = window.ywcFlex?.model;
    if (!model?.getNodeById(id)) return;
    if (_lastTabTitles[id] === name) return;
    try {
        model.doAction(window.FlexLayout.Actions.renameTab(id, name));
        _lastTabTitles[id] = name;
    } catch { /* ignore */ }
}

function vfoTitleFor(vfo) {
    const heading = document.getElementById(`receiver${vfo}Heading`);
    const col = document.getElementById(`vfo${vfo}Col`);
    const isTx = !!heading?.querySelector('.bi-broadcast');
    let isActive = !!col?.classList.contains('vfo-active');
    if (!isActive) {
        // Single-receiver radios have no .vfo-active; read the RX selector.
        const rxBtn = document.getElementById(`rxVfo${vfo}`);
        isActive = !!rxBtn && (rxBtn.classList.contains('btn-success') || rxBtn.classList.contains('btn-danger'));
    }
    return `VFO ${vfo}${isActive ? ' ●' : ''}${isTx ? ' TX' : ''}`;
}

function updateVfoTitle(vfo) {
    setTabTitle(`vfo${vfo}`, vfoTitleFor(vfo));
}

function wireVfoTitles() {
    for (const vfo of ['A', 'B']) {
        updateVfoTitle(vfo);
        const col = document.getElementById(`vfo${vfo}Col`);
        const heading = document.getElementById(`receiver${vfo}Heading`);
        if (col && col.dataset.ywcTitleWired !== '1') {
            col.dataset.ywcTitleWired = '1';
            new MutationObserver(() => updateVfoTitle(vfo))
                .observe(col, { attributes: true, attributeFilter: ['class'] });
        }
        if (heading && heading.dataset.ywcTitleWired !== '1') {
            heading.dataset.ywcTitleWired = '1';
            new MutationObserver(() => updateVfoTitle(vfo))
                .observe(heading, { childList: true, subtree: true, characterData: true });
        }
    }
    const rxGroup = document.getElementById('rxTxSplitGroup');
    if (rxGroup && rxGroup.dataset.ywcTitleWired !== '1') {
        rxGroup.dataset.ywcTitleWired = '1';
        new MutationObserver(() => { updateVfoTitle('A'); updateVfoTitle('B'); })
            .observe(rxGroup, { subtree: true, attributes: true, childList: true });
    }
}

export function initFlexWorkspace(host, flags) {
    const FL = window.FlexLayout;
    const React = window.React;
    const ReactDOM = window.ReactDOM;
    if (!FL?.Model || !React || !ReactDOM?.createRoot) {
        host.textContent = 'FlexLayout library failed to load.';
        return null;
    }

    const state = {
        model: null,
        root: null,
        layoutRef: React.createRef(),
        flags: flags || {},
        scale: getActiveScale(),
    };

    function applyScale(scale) {
        document.documentElement.dataset.ywcFlexScale = scale;
        // rem is relative to the root, so this is what actually resizes the
        // Bootstrap/theme-based components. Layout positions are untouched.
        document.documentElement.style.fontSize = `${SCALES[scale] || 16}px`;
        // FlexLayout's own chrome is px-based; nudge the splitter with scale.
        const splitter = scale === 'xsmall' ? 12 : scale === 'small' ? 10 : scale === 'medium' ? 8 : 8;
        document.documentElement.style.setProperty('--flexlayout-splitter-size', `${splitter}px`);
        dispatchPanelResize();
    }

    function persist() {
        if (!state.model) return;
        try {
            localStorage.setItem(ARRANGEMENT_KEY, JSON.stringify(state.model.toJson()));
        } catch { /* quota */ }
    }

    function renderApp() {
        if (!state.root) state.root = ReactDOM.createRoot(host);
        const App = () => React.createElement(FL.Layout, {
            model: state.model,
            factory: createTemplateElement,
            ref: state.layoutRef,
            realtimeResize: true,
            popoutURL: '/popout.html',
            onModelChange: () => { persist(); dispatchPanelResize(); buildPanelsMenu(); },
        });
        state.root.render(React.createElement(App));
    }

    async function loadModel({ reset = false } = {}) {
        applyScale(state.scale);
        let json = null;
        if (!reset) {
            try {
                const raw = localStorage.getItem(ARRANGEMENT_KEY);
                if (raw) json = JSON.parse(raw);
            } catch {
                localStorage.removeItem(ARRANGEMENT_KEY);
            }
        }
        json = filterLayoutJson(json || await loadDefaultJson(), state.flags);

        json.global = json.global || {};
        json.global.tabEnableClose = true;
        json.global.tabSetEnableMaximize = true;
        json.global.tabEnablePopout = true;
        json.global.tabEnablePopoutFloatIcon = true;

        state.model = FL.Model.fromJson(json);
        state.model.addChangeListener(() => { persist(); dispatchPanelResize(); buildPanelsMenu(); });
        renderApp();
        syncScaleUi();
        queueMicrotask(dispatchPanelResize);
    }

    function availablePanels() {
        return PANELS.filter((p) => {
            if (p.component === 'spectrumA') return !!state.flags.spectrumA;
            if (p.component === 'spectrumB') return !!state.flags.spectrumB;
            if (p.component === 'vfoB') return state.flags.vfoB !== false;
            if (p.component === 'radioDisplay') return !!state.flags.radioDisplay;
            return true;
        });
    }

    function syncScaleUi() {
        const sel = document.getElementById('flexLayoutScale');
        if (sel && sel.value !== state.scale) sel.value = state.scale;
        buildPanelsMenu();
    }

    function buildPanelsMenu() {
        const menu = document.getElementById('ywcFlexPanelsMenu');
        if (!menu) return;
        menu.replaceChildren();
        for (const p of availablePanels()) {
            const open = !!state.model?.getNodeById(p.id);
            const li = document.createElement('li');
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'dropdown-item' + (open ? ' active' : '');
            btn.setAttribute('role', 'menuitemcheckbox');
            btn.setAttribute('aria-checked', open ? 'true' : 'false');
            btn.textContent = p.name;
            btn.addEventListener('click', () => {
                togglePanel(p.id);
                buildPanelsMenu();
            });
            li.appendChild(btn);
            menu.appendChild(li);
        }
    }

    function showPanel(id) {
        const meta = PANELS.find((p) => p.id === id);
        if (!meta || !state.model) return;
        if (state.model.getNodeById(id)) {
            state.model.doAction(FL.Actions.selectTab(id));
            return;
        }
        const json = { type: 'tab', id, name: meta.name, component: meta.component };
        const toNode = state.model.getActiveTabset?.() || state.model.getFirstTabSet?.();
        if (toNode) {
            try {
                state.model.doAction(FL.Actions.addNode(json, toNode.getId(), FL.DockLocation.CENTER, -1));
            } catch { /* ignore */ }
        }
        dispatchPanelResize();
    }

    function hidePanel(id) {
        if (!state.model?.getNodeById(id)) return;
        try { state.model.doAction(FL.Actions.deleteTab(id)); } catch { /* ignore */ }
        dispatchPanelResize();
    }

    function togglePanel(id) {
        if (state.model?.getNodeById(id)) hidePanel(id);
        else showPanel(id);
        buildPanelsMenu();
    }

    window.ywcFlex = {
        get model() { return state.model; },
        get api() { return state.model; },
        flags: state.flags,
        get scale() { return state.scale; },
        panels: PANELS,
        scales: SCALE_ORDER,
        setScale(scale) {
            if (!SCALES[scale]) return;
            state.scale = scale;
            setActiveScale(scale);
            applyScale(scale);
            syncScaleUi();
        },
        async resetLayout() {
            try { localStorage.removeItem(ARRANGEMENT_KEY); } catch { /* ignore */ }
            await loadModel({ reset: true });
        },
        showPanel,
        hidePanel,
        togglePanel,
    };

    wireToolbar();
    // VFO tab titles: set on ready, and re-applied whenever a VFO tab mounts
    // (a closed tab is recreated with its default name, so forget the cached
    // title first).
    window.addEventListener('ywc-flex-ready', wireVfoTitles);
    window.addEventListener('ywc-flex-template-mounted', (e) => {
        if (/^vfo[AB]$/.test(e.detail?.component || '')) wireVfoTitles();
    });
    window.addEventListener('ywc-flex-panel-attached', (e) => {
        const component = e.detail?.component;
        if (component === 'vfoA' || component === 'vfoB') {
            _lastTabTitles[component] = null;
            updateVfoTitle(component === 'vfoA' ? 'A' : 'B');
        }
    });
    loadModel().then(() => {
        // Wait until React has committed the initial panels before telling the
        // page its element-dependent init (meters, spectrum, VFO keys, ...)
        // can run. Waiting on a real panel host — not just one frame — avoids
        // the occasional race where site.js populated the segment key while
        // it was still the raw <div id="segmentButtonA">.
        let tries = 0;
        const waitForPanels = () => {
            const mounted = document.querySelector('.ywc-flex-panel-host[data-ywc-component="vfoA"]');
            if (mounted || tries++ > 60) {
                window.dispatchEvent(new Event('ywc-flex-ready'));
            } else {
                requestAnimationFrame(waitForPanels);
            }
        };
        requestAnimationFrame(waitForPanels);
    }).catch((err) => {
        console.error(err);
        host.textContent = 'Failed to load Flex layout.';
        window.dispatchEvent(new Event('ywc-flex-ready'));
    });

    return window.ywcFlex;
}

function wireToolbar() {
    document.getElementById('flexResetLayoutBtn')?.addEventListener('click', () => {
        window.ywcFlex?.resetLayout();
    });
    document.getElementById('flexLayoutScale')?.addEventListener('change', (e) => {
        window.ywcFlex?.setScale(e.target.value);
    });
}
