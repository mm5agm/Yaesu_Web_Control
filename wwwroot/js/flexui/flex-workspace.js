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
const LAYOUT_VERSION = 'v6';
const ARRANGEMENT_KEY = `ywc.flexui.layout.${LAYOUT_VERSION}`;
const SCALE_KEY = 'ywc.flexui.scale';

// Named arrangements live on the server (flex-layouts.json). localStorage is
// the offline working copy plus a queue of writes that could not reach the
// server. See the persistence section below.
const API_BASE = '/api/flexlayouts';
const DEFAULT_ID = '__default__';
const ACTIVE_KEY = 'ywc.flexui.activeId';
const PRESETS_KEY = 'ywc.flexui.presets';
const PENDING_KEY = 'ywc.flexui.pending';
const AUTO_SAVE_DEBOUNCE_MS = 600;

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
    radioScope: 'tpl-radio-scope',
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
    { id: 'radioScope', name: 'Radio Scope', component: 'radioScope' },
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

/**
 * Panels whose content is deliberately rendered only once their tab is
 * selected. Radio Display is the only one: mounting it runs
 * initRadioDisplayUi, which probes the capture device and (with Auto on)
 * starts the MJPEG stream, so it must not happen just because the page
 * loaded with that tab present but not selected.
 */
const LAZY_COMPONENT_IDS = new Set(['radioDisplay']);

/**
 * Per-tab render policy, applied to every tab in the layout.
 *
 * FlexLayout defaults `tabEnableRenderOnDemand` to true (render a tab's
 * component only once it is visible), but this workspace turns that off
 * globally (see applyGlobals) so that **every** panel present in a layout is
 * mounted at load. The page runs its element-dependent init exactly once, at
 * `ywc-flex-ready`, and nothing re-runs it when a tab is selected later — so
 * a panel that is merely a non-selected tab (VFO A and VFO B stacked in one
 * tabset, Linear Meters stacked with anything) would never be initialised,
 * leaving it blank for the rest of the session. Mounting everything up front
 * removes that whole class of bug. Radio Display opts back out; its own
 * first-mount handler initialises it when the operator opens the tab.
 */
function applyTabRenderPolicy(tabJson) {
    const id = tabJson?.id ?? tabJson?.component;
    if (LAZY_COMPONENT_IDS.has(id)) tabJson.enableRenderOnDemand = true;
    return tabJson;
}

function isKnownComponent(component) {
    return typeof component === 'string'
        && Object.prototype.hasOwnProperty.call(TEMPLATE_BY_COMPONENT, component);
}

/**
 * Drop panels the host cannot currently show (no SDR, video off, ...).
 *
 * A dropped tab is one of two kinds, and the difference matters for
 * persistence:
 *   - component still exists but the host cannot render it right now
 *     (flag-gated, e.g. video off). Retained in `retained` so a later drag
 *     cannot permanently erase it from a saved arrangement.
 *   - component is unknown/renamed/removed. Discarded outright: there is no
 *     template to render and nothing worth restoring.
 *
 * `retained` entries are `{ tab, anchors }`; `anchors` are the sibling tab ids
 * that shared the dropped tab's tabset, used to re-home it on save.
 */
function filterLayoutJson(json, flags, retained) {
    const drop = new Set();
    if (!flags?.remoteAudio) drop.add('remoteAudio');
    if (!flags?.spectrumA) drop.add('spectrumA');
    if (!flags?.spectrumB) drop.add('spectrumB');
    if (!flags?.vfoB) drop.add('vfoB');
    if (!flags?.radioDisplay) drop.add('radioDisplay');
    if (!flags?.radioScope) drop.add('radioScope');
    // Extras was split into Remote Audio and Clarifier; a layout saved by an
    // older build still carries the single 'extras' tab.
    drop.add('extras');

    const clone = structuredClone(json);
    const filterChildren = (node, anchors) => {
        if (!node) return null;
        if (node.type === 'tab') {
            const known = isKnownComponent(node.component);
            if (drop.has(node.id) || drop.has(node.component)) {
                if (known && retained) {
                    retained.push({
                        tab: node,
                        anchors: (anchors || []).filter((a) => a !== node.id),
                    });
                }
                return null;
            }
            if (!known) return null;
            return applyTabRenderPolicy(node);
        }
        if (Array.isArray(node.children)) {
            const childAnchors = node.type === 'tabset'
                ? node.children.filter((c) => c?.type === 'tab' && c.id).map((c) => c.id)
                : [];
            node.children = node.children
                .map((child) => filterChildren(child, childAnchors))
                .filter(Boolean);
        }
        if ((node.type === 'tabset' || node.type === 'border' || node.type === 'row')
            && Array.isArray(node.children) && node.children.length === 0) {
            return null;
        }
        return node;
    };

    clone.layout = filterChildren(clone.layout, []);
    if (!clone.layout) {
        clone.layout = {
            type: 'row',
            weight: 100,
            children: [{
                type: 'tabset',
                weight: 100,
                children: [applyTabRenderPolicy({ type: 'tab', id: 'vfoA', name: 'VFO A', component: 'vfoA' })],
            }],
        };
    }
    return clone;
}

function collectTabIds(node, out) {
    if (!node) return;
    if (node.type === 'tab') {
        if (node.id) out.add(node.id);
        return;
    }
    for (const child of node.children || []) collectTabIds(child, out);
}

function findTabsetForAnchors(node, anchors) {
    if (!node) return null;
    if (node.type === 'tabset') {
        const ids = (node.children || []).filter((c) => c?.type === 'tab').map((c) => c.id);
        if ((anchors || []).some((a) => ids.includes(a))) return node;
    }
    for (const child of node.children || []) {
        const hit = findTabsetForAnchors(child, anchors);
        if (hit) return hit;
    }
    return null;
}

function firstTabset(node) {
    if (!node) return null;
    if (node.type === 'tabset') return node;
    for (const child of node.children || []) {
        const hit = firstTabset(child);
        if (hit) return hit;
    }
    return null;
}

/**
 * Re-home tabs withheld from the live model because this host cannot show them
 * (no SDR, video off). Without this, any save while a panel is unavailable
 * would persist the reduced model and erase the panel for good.
 */
function restoreRetainedTabs(json, retained) {
    if (!retained?.length || !json?.layout) return json;
    const present = new Set();
    collectTabIds(json.layout, present);
    for (const record of retained) {
        const id = record?.tab?.id;
        if (!id || present.has(id)) continue;
        const host = findTabsetForAnchors(json.layout, record.anchors) || firstTabset(json.layout);
        if (!host) continue;
        host.children = host.children || [];
        host.children.push(structuredClone(record.tab));
        present.add(id);
    }
    return json;
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
        host.textContent = 'Dockable layout library failed to load.';
        return null;
    }

    const state = {
        model: null,
        root: null,
        layoutRef: React.createRef(),
        flags: flags || {},
        scale: getActiveScale(),
        droppedTabs: [],
        presets: [],
        activeId: DEFAULT_ID,
        serverActiveId: DEFAULT_ID,
        remoteAvailable: false,
        pending: new Map(),
        pendingActive: null,
        mountSeq: 0,
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

    // ── Persistence: server arrangements with an offline queue ────────────
    //
    // The server (flex-layouts.json via /api/flexlayouts) is the source of
    // truth for named layouts. localStorage holds:
    //   - the active arrangement's working copy (ARRANGEMENT_KEY), so edits
    //     survive a reload even while the Default (built-in) arrangement is
    //     selected and there is no server slot to write to;
    //   - a cache of preset metadata for offline display (PRESETS_KEY);
    //   - a queue of writes that could not reach the server (PENDING_KEY).
    //
    // Auto-save applies to a named preset. Edits made while Default is active
    // stay a local draft until the user saves them as a preset — the built-in
    // Default is never overwritten.

    let saveTimer = null;
    let flushing = false;

    function readJson(key) {
        try {
            const raw = localStorage.getItem(key);
            return raw ? JSON.parse(raw) : null;
        } catch { return null; }
    }

    function writeJson(key, value) {
        try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* quota */ }
    }

    function loadPending() {
        const doc = readJson(PENDING_KEY);
        state.pending = new Map();
        if (Array.isArray(doc?.entries)) {
            for (const entry of doc.entries) if (entry?.id) state.pending.set(entry.id, entry);
        }
        state.pendingActive = doc?.activeId ?? null;
    }

    function savePending() {
        writeJson(PENDING_KEY, {
            entries: [...state.pending.values()],
            activeId: state.pendingActive,
        });
    }

    function upsertPreset(summary) {
        if (!summary?.id) return;
        const i = state.presets.findIndex((p) => p.id === summary.id);
        if (i >= 0) state.presets[i] = { ...state.presets[i], ...summary };
        else state.presets.push(summary);
        writeJson(PRESETS_KEY, state.presets);
    }

    async function refreshPresets() {
        try {
            const res = await fetch(API_BASE, { headers: { Accept: 'application/json' } });
            if (!res.ok) throw new Error(String(res.status));
            const snap = await res.json();
            state.presets = Array.isArray(snap.layouts) ? snap.layouts : [];
            state.serverActiveId = snap.activeId || DEFAULT_ID;
            state.remoteAvailable = true;
            writeJson(PRESETS_KEY, state.presets);
            return true;
        } catch {
            state.remoteAvailable = false;
            const cached = readJson(PRESETS_KEY);
            if (Array.isArray(cached)) state.presets = cached;
            return false;
        }
    }

    async function fetchPresetJson(id) {
        try {
            const res = await fetch(`${API_BASE}/${encodeURIComponent(id)}`, {
                headers: { Accept: 'application/json' },
            });
            if (!res.ok) return null;
            const detail = await res.json();
            return detail?.layout ?? null;
        } catch {
            return null;
        }
    }

    function applyGlobals(json) {
        json.global = json.global || {};
        json.global.tabEnableClose = true;
        json.global.tabSetEnableMaximize = true;
        json.global.tabEnablePopout = true;
        json.global.tabEnablePopoutFloatIcon = true;
        // Mount every panel in the layout, not just the selected tab of each
        // tabset — see applyTabRenderPolicy. Per-tab overrides (Radio Display)
        // still win over this.
        json.global.tabEnableRenderOnDemand = false;
        return json;
    }

    function mountJson(rawJson) {
        state.droppedTabs = [];
        const json = applyGlobals(filterLayoutJson(rawJson, state.flags, state.droppedTabs));
        state.model = FL.Model.fromJson(json);
        state.mountSeq += 1;
        state.model.addChangeListener(() => { persist(); dispatchPanelResize(); buildPanelsMenu(); });
        renderApp();
        syncScaleUi();
        wireVfoTitles();
        queueMicrotask(dispatchPanelResize);
    }

    function renderApp() {
        if (!state.root) state.root = ReactDOM.createRoot(host);
        const App = () => React.createElement(FL.Layout, {
            key: state.mountSeq,
            model: state.model,
            factory: createTemplateElement,
            ref: state.layoutRef,
            realtimeResize: true,
            popoutURL: '/popout.html',
            onModelChange: () => { persist(); dispatchPanelResize(); buildPanelsMenu(); },
        });
        state.root.render(React.createElement(App));
    }

    function persist() {
        if (!state.model) return;
        const json = restoreRetainedTabs(state.model.toJson(), state.droppedTabs);
        writeJson(ARRANGEMENT_KEY, json);
        if (state.activeId === DEFAULT_ID) {
            buildLayoutsMenu();
            return;
        }
        const known = state.presets.find((p) => p.id === state.activeId);
        state.pending.set(state.activeId, {
            id: state.activeId,
            name: known?.name || 'Layout',
            layout: json,
            exists: true,
        });
        savePending();
        clearTimeout(saveTimer);
        saveTimer = setTimeout(() => { flushPending(); }, AUTO_SAVE_DEBOUNCE_MS);
    }

    async function flushPending() {
        if (flushing || (state.pending.size === 0 && state.pendingActive == null)) return;
        flushing = true;
        try {
            for (const [id, entry] of [...state.pending]) {
                if (!entry.exists) {
                    const res = await fetch(`${API_BASE}/${encodeURIComponent(id)}`, { method: 'DELETE' });
                    if (res.ok || res.status < 500) { state.pending.delete(id); savePending(); }
                    else throw new Error(`delete ${res.status}`);
                    continue;
                }

                let res = await fetch(`${API_BASE}/${encodeURIComponent(id)}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ name: entry.name, layout: entry.layout ?? null }),
                });
                if (res.status === 404) {
                    // Queued offline before it existed on the server.
                    res = await fetch(API_BASE, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ id, name: entry.name, layout: entry.layout }),
                    });
                }
                if (!res.ok) {
                    // A 4xx we cannot fix (invalid, forbidden, cap) would wedge
                    // the queue forever; drop it. 409 is retryable (stale token).
                    if (res.status >= 400 && res.status < 500 && res.status !== 409) {
                        state.pending.delete(id);
                        savePending();
                        continue;
                    }
                    throw new Error(`save ${res.status}`);
                }
                const body = await res.json().catch(() => null);
                if (body?.layout) upsertPreset(body.layout);
                state.pending.delete(id);
                savePending();
            }

            if (state.pendingActive != null) {
                const res = await fetch(`${API_BASE}/active`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ id: state.pendingActive }),
                });
                if (res.ok || (res.status >= 400 && res.status < 500)) {
                    state.pendingActive = null;
                    savePending();
                } else {
                    throw new Error(`active ${res.status}`);
                }
            }
            state.remoteAvailable = true;
        } catch {
            state.remoteAvailable = false;
        } finally {
            flushing = false;
            buildLayoutsMenu();
        }
    }

    function readDraft() {
        return readJson(ARRANGEMENT_KEY);
    }

    async function resolveActive() {
        // A queued active change is local intent and wins over the server's record.
        let id = state.pendingActive ?? readJson(ACTIVE_KEY) ?? state.serverActiveId ?? DEFAULT_ID;
        if (id !== DEFAULT_ID && !state.presets.some((p) => p.id === id)) id = DEFAULT_ID;
        state.activeId = id;
        writeJson(ACTIVE_KEY, id);
    }

    async function loadActiveJson() {
        if (state.activeId === DEFAULT_ID) {
            return readDraft() || await loadDefaultJson();
        }
        const queued = state.pending.get(state.activeId);
        if (queued?.layout) return queued.layout;
        const remote = await fetchPresetJson(state.activeId);
        if (remote) return remote;
        return readDraft() || await loadDefaultJson();
    }

    async function switchTo(id) {
        await flushPending();
        let json = null;
        if (id === DEFAULT_ID) {
            json = await loadDefaultJson();
        } else {
            const queued = state.pending.get(id);
            json = queued?.layout || await fetchPresetJson(id);
        }
        if (!json) {
            // Could not resolve the arrangement (offline, or deleted elsewhere).
            // Do not switch: never write one preset's layout over another.
            buildLayoutsMenu();
            return;
        }
        state.activeId = id;
        writeJson(ACTIVE_KEY, id);
        state.pendingActive = id;
        savePending();
        mountJson(json);
        await flushPending();
        buildLayoutsMenu();
    }

    async function createPreset(name, json) {
        const id = (window.crypto?.randomUUID?.()
            || `l${Date.now().toString(36)}${Math.floor(Math.random() * 1e6).toString(36)}`).replace(/-/g, '');
        const now = new Date().toISOString();
        state.pending.set(id, { id, name, layout: json, exists: true });
        state.pendingActive = id;
        state.activeId = id;
        writeJson(ACTIVE_KEY, id);
        upsertPreset({ id, name, createdAt: now, updatedAt: now });
        savePending();
        await flushPending();
        buildLayoutsMenu();
        return id;
    }

    function renamePreset(id, name) {
        const preset = state.presets.find((p) => p.id === id);
        if (preset) { preset.name = name; writeJson(PRESETS_KEY, state.presets); }
        const queued = state.pending.get(id);
        state.pending.set(id, { id, name, layout: queued?.layout ?? null, exists: true });
        savePending();
        flushPending();
        buildLayoutsMenu();
    }

    function deletePreset(id) {
        state.presets = state.presets.filter((p) => p.id !== id);
        writeJson(PRESETS_KEY, state.presets);
        state.pending.set(id, { id, name: '', layout: null, exists: false });
        savePending();
        if (state.activeId === id) switchTo(DEFAULT_ID);
        else flushPending();
        buildLayoutsMenu();
    }

    function saveCurrentAs(name) {
        const json = restoreRetainedTabs(state.model.toJson(), state.droppedTabs);
        return createPreset(name, json);
    }

    function availablePanels() {
        return PANELS.filter((p) => {
            if (p.component === 'spectrumA') return !!state.flags.spectrumA;
            if (p.component === 'spectrumB') return !!state.flags.spectrumB;
            if (p.component === 'vfoB') return state.flags.vfoB !== false;
            if (p.component === 'radioDisplay') return !!state.flags.radioDisplay;
            if (p.component === 'radioScope') return !!state.flags.radioScope;
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
        const json = applyTabRenderPolicy({ type: 'tab', id, name: meta.name, component: meta.component });
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

    // ── Layouts menu ──────────────────────────────────────────────────────

    function addMenuItem(menu, label, active, onClick) {
        const li = document.createElement('li');
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'dropdown-item' + (active ? ' active' : '');
        btn.setAttribute('role', 'menuitemradio');
        btn.setAttribute('aria-checked', active ? 'true' : 'false');
        btn.textContent = label;
        btn.addEventListener('click', onClick);
        li.appendChild(btn);
        menu.appendChild(li);
    }

    function addMenuDivider(menu) {
        const li = document.createElement('li');
        const hr = document.createElement('hr');
        hr.className = 'dropdown-divider';
        li.appendChild(hr);
        menu.appendChild(li);
    }

    function suggestedLayoutName() {
        return `Layout ${state.presets.length + 1}`;
    }

    function buildLayoutsMenu() {
        const menu = document.getElementById('ywcFlexLayoutMenu');
        if (menu) {
            menu.replaceChildren();
            addMenuItem(menu, 'Default', state.activeId === DEFAULT_ID, () => switchTo(DEFAULT_ID));
            for (const preset of state.presets) {
                addMenuItem(menu, preset.name || 'Layout', state.activeId === preset.id,
                    () => switchTo(preset.id));
            }
            if (!state.remoteAvailable && state.presets.length === 0) {
                const li = document.createElement('li');
                const span = document.createElement('span');
                span.className = 'dropdown-item disabled small';
                span.textContent = 'Saved layouts unavailable';
                li.appendChild(span);
                menu.appendChild(li);
            }
            addMenuDivider(menu);
            addMenuItem(menu, 'Save current as…', false, () => {
                const name = window.prompt('Name this layout', suggestedLayoutName());
                if (name && name.trim()) saveCurrentAs(name.trim());
            });
            addMenuItem(menu, 'Manage layouts…', false, () => {
                window.dispatchEvent(new CustomEvent('ywc-flex-manage-layouts'));
            });
        }

        const label = document.getElementById('ywcFlexLayoutName');
        if (label) {
            const preset = state.presets.find((p) => p.id === state.activeId);
            const saving = !state.remoteAvailable && state.pending.size > 0;
            const name = preset?.name || 'Default';
            label.textContent = saving ? `${name} (saving…)` : name;
        }
    }

    window.ywcFlex = {
        get model() { return state.model; },
        get api() { return state.model; },
        flags: state.flags,
        get scale() { return state.scale; },
        get activeId() { return state.activeId; },
        get presets() { return state.presets.slice(); },
        get remoteAvailable() { return state.remoteAvailable; },
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
            await switchTo(DEFAULT_ID);
            try { localStorage.removeItem(ARRANGEMENT_KEY); } catch { /* ignore */ }
            // Re-mount the pristine default so the discarded draft isn't
            // immediately re-persisted by an incidental model change.
            mountJson(await loadDefaultJson());
            buildLayoutsMenu();
        },
        switchTo,
        saveCurrentAs,
        renamePreset,
        deletePreset,
        async refreshLayouts() {
            await flushPending();
            await refreshPresets();
            buildLayoutsMenu();
        },
        buildLayoutsMenu,
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
    (async () => {
        applyScale(state.scale);
        await refreshPresets();
        loadPending();
        await resolveActive();
        await flushPending();
        mountJson(await loadActiveJson());
        buildLayoutsMenu();
    })().then(() => {
        // Wait until React has committed the initial panels *and run their
        // effects* before telling the page its element-dependent init
        // (meters, spectrum, VFO keys, ...) can run. The host wrapper is part
        // of the render output and exists before the effect clones the
        // template into it, so waiting on the host alone can fire ready while
        // the canvases/keys are still absent — MeterPanel, for one, caches a
        // gauge per canvas at construction and never retries. `.ywc-panel-body`
        // is appended by the effect, so it is only present once the template
        // content is really in the DOM.
        let tries = 0;
        const waitForPanels = () => {
            const hosts = document.querySelectorAll('.ywc-flex-panel-host');
            const mounted = hosts.length > 0
                && Array.from(hosts).every((h) => h.querySelector('.ywc-panel-body'));
            if (mounted || tries++ > 60) {
                window.dispatchEvent(new Event('ywc-flex-ready'));
            } else {
                requestAnimationFrame(waitForPanels);
            }
        };
        requestAnimationFrame(waitForPanels);
    }).catch((err) => {
        console.error(err);
        host.textContent = 'Failed to load the experimental layout.';
        window.dispatchEvent(new Event('ywc-flex-ready'));
    });

    // Reconnect handling: flush any queued writes and refresh the preset list
    // when the network returns, and retry periodically while something is queued.
    window.addEventListener('online', async () => {
        await flushPending();
        await refreshPresets();
        buildLayoutsMenu();
    });
    setInterval(() => {
        if (state.pending.size > 0 || state.pendingActive != null) flushPending();
    }, 30000);

    return window.ywcFlex;
}

function wireToolbar() {
    document.getElementById('flexResetLayoutBtn')?.addEventListener('click', () => {
        window.ywcFlex?.resetLayout();
    });
    document.getElementById('flexLayoutsBtn')?.addEventListener('click', () => {
        window.ywcFlex?.buildLayoutsMenu();
    });
    document.getElementById('flexLayoutScale')?.addEventListener('change', (e) => {
        window.ywcFlex?.setScale(e.target.value);
    });
}
