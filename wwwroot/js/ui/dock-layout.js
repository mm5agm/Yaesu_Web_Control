import { TemplatePanel } from '/js/dock/template-panel.js?v=4';
import { GroupPopoutAction } from '/js/dock/group-popout-action.js?v=1';
import { installDomShim, dispatchPanelResize } from '/js/dock/dom-shim.js';

const LAYOUT_KEY = 'ywc.dockLayout.v4';

/** @typedef {import('dockview').DockviewApi} DockviewApi */

/**
 * @param {HTMLElement} host
 * @param {object} flags
 * @param {boolean} flags.remoteAudio
 * @param {boolean} flags.spectrumA
 * @param {boolean} flags.spectrumB
 * @param {boolean} flags.vfoB
 */
export function initDockWorkspace(host, flags) {
    const dv = window.dockview;
    if (!dv?.createDockview) {
        host.textContent = 'Dockview library failed to load.';
        return null;
    }

    installDomShim();

    const api = dv.createDockview(host, {
        theme: dv.themeAbyss,
        popoutUrl: '/popout.html',
        createComponent: (options) => new TemplatePanel(options.id),
        createRightHeaderActionComponent: () => new GroupPopoutAction(),
    });

    const panelDefs = buildPanelDefs(flags);
    let restored = false;
    try {
        const raw = localStorage.getItem(LAYOUT_KEY);
        if (raw) {
            api.fromJSON(JSON.parse(raw));
            restored = true;
        }
    } catch {
        localStorage.removeItem(LAYOUT_KEY);
    }

    if (!restored) {
        buildDefaultLayout(api, panelDefs);
    }

    api.onDidLayoutChange(() => {
        try {
            localStorage.setItem(LAYOUT_KEY, JSON.stringify(api.toJSON()));
        } catch { /* quota */ }
        dispatchPanelResize();
    });

    api.onDidOpenPopoutWindowFail(() => {
        alert('Allow popups for this site to pop panels out to another window.');
    });

    api.onDidAddPopoutGroup(() => dispatchPanelResize());
    api.onDidRemovePopoutGroup(() => dispatchPanelResize());

    const ro = new ResizeObserver(() => layoutDock(api, host));
    ro.observe(host);
    layoutDock(api, host);

    window.ywcDock = {
        api,
        flags,
        panelDefs,
        resetLayout() {
            localStorage.removeItem(LAYOUT_KEY);
            api.clear();
            buildDefaultLayout(api, panelDefs);
            dispatchPanelResize();
        },
        showPanel(id) {
            const existing = api.getPanel(id);
            if (existing) {
                existing.api.setActive();
                return;
            }
            const def = panelDefs.find(p => p.id === id);
            if (!def) return;
            const opts = { ...def.options };
            if (id === 'spectrumB') {
                opts.position = { referencePanel: 'spectrumA', direction: 'right' };
            } else if (id === 'vfoB') {
                opts.position = { referencePanel: 'vfoA', direction: 'right' };
            } else if (id === 'controls' && api.getPanel('meters')) {
                opts.position = { referencePanel: 'meters', direction: 'right' };
            } else if (id === 'clarifier' && api.getPanel('remoteAudio')) {
                opts.position = { referencePanel: 'remoteAudio', direction: 'within' };
            }
            api.addPanel(opts);
            dispatchPanelResize();
        },
        hidePanel(id) {
            const p = api.getPanel(id);
            if (p) api.removePanel(p);
        },
        togglePanel(id) {
            if (api.getPanel(id)) window.ywcDock.hidePanel(id);
            else window.ywcDock.showPanel(id);
        },
    };

    wireToolbar(api, panelDefs);
    return api;
}

function layoutDock(api, host) {
    const r = host.getBoundingClientRect();
    if (r.width > 0 && r.height > 0) api.layout(r.width, r.height);
}

/** @returns {Array<{id:string, options:object}>} */
function buildPanelDefs(flags) {
    const defs = [
        panelDef('meters', 'Meters', 'tpl-meters'),
        panelDef('controls', 'Controls', 'tpl-controls'),
    ];
    if (flags.remoteAudio) defs.push(panelDef('remoteAudio', 'Remote Audio', 'tpl-remote-audio'));
    if (flags.spectrumA) defs.push(panelDef('spectrumA', 'Spectrum A', 'tpl-spectrum-a'));
    if (flags.spectrumB) defs.push(panelDef('spectrumB', 'Spectrum B', 'tpl-spectrum-b'));
    defs.push(panelDef('vfoA', 'VFO A', 'tpl-vfo-a'));
    if (flags.vfoB) defs.push(panelDef('vfoB', 'VFO B', 'tpl-vfo-b'));
    defs.push(panelDef('clarifier', 'Clarifier', 'tpl-clarifier'));
    return defs;
}

function panelDef(id, title, templateId) {
    return {
        id,
        options: {
            id,
            title,
            component: 'template',
            params: { templateId },
        },
    };
}

function buildDefaultLayout(api, defs) {
    const map = new Map(defs.map((d) => [d.id, d]));
    const add = (id, position) => {
        if (!map.has(id)) return;
        const opts = { ...map.get(id).options };
        if (position) opts.position = position;
        api.addPanel(opts);
    };

    // Meters | Controls side by side across the top row.
    add('meters');
    add('controls', { referencePanel: 'meters', direction: 'right' });

    // Everything under that row must use the dock edge (`direction: 'below'`
    // with no reference) so it spans the full width of meters|controls,
    // not only one column.
    if (map.has('remoteAudio')) {
        add('remoteAudio', { direction: 'below' });
        add('vfoA', { referencePanel: 'remoteAudio', direction: 'above' });
    } else {
        add('vfoA', { direction: 'below' });
    }
    add('vfoB', { referencePanel: 'vfoA', direction: 'right' });

    if (map.has('remoteAudio') && map.has('clarifier')) {
        add('clarifier', { referencePanel: 'remoteAudio', direction: 'within' });
    } else if (map.has('clarifier')) {
        add('clarifier', {
            referencePanel: map.has('vfoB') ? 'vfoB' : 'vfoA',
            direction: 'below',
        });
    }

    let below = map.has('remoteAudio') ? 'remoteAudio' : (map.has('vfoB') ? 'vfoB' : 'vfoA');

    if (map.has('spectrumA')) {
        add('spectrumA', { referencePanel: below, direction: 'below' });
        below = 'spectrumA';
    }
    if (map.has('spectrumB')) {
        add('spectrumB', { referencePanel: 'spectrumA', direction: 'right' });
    }
}

function wireToolbar(api, defs) {
    document.getElementById('dockResetLayoutBtn')?.addEventListener('click', () => {
        window.ywcDock?.resetLayout();
    });

    document.getElementById('vfoBToggleBtn')?.addEventListener('click', () => {
        const visible = !!api.getPanel('vfoB');
        if (visible) {
            api.removePanel(api.getPanel('vfoB'));
            try { localStorage.setItem('vfoBVisible', 'false'); } catch { /* ignore */ }
            const btn = document.getElementById('vfoBToggleBtn');
            if (btn) btn.textContent = 'Show B';
        } else {
            const def = defs.find(d => d.id === 'vfoB');
            if (def) {
                api.addPanel({
                    ...def.options,
                    position: { referencePanel: 'vfoA', direction: 'right' },
                });
            }
            try { localStorage.setItem('vfoBVisible', 'true'); } catch { /* ignore */ }
            const btn = document.getElementById('vfoBToggleBtn');
            if (btn) btn.textContent = 'Hide B';
        }
        dispatchPanelResize();
    });

    const vfoBStored = (() => {
        try { return localStorage.getItem('vfoBVisible'); } catch { return null; }
    })();
    if (vfoBStored === 'false' && api.getPanel('vfoB')) {
        api.removePanel(api.getPanel('vfoB'));
        const btn = document.getElementById('vfoBToggleBtn');
        if (btn) btn.textContent = 'Show B';
    }
}

/** Minimal bootstrap shim so site.js message box works without Bootstrap on Dock. */
export function installBootstrapShim() {
    if (window.bootstrap) return;
    const modalInstances = new WeakMap();
    window.bootstrap = {
        Modal: class {
            constructor(el) {
                this._el = el;
                modalInstances.set(el, this);
            }
            static getInstance(el) {
                return modalInstances.get(el);
            }
            show() {
                if (this._el?.tagName === 'DIALOG') this._el.showModal();
                else this._el?.classList?.add('ywc-modal-open');
            }
            hide() {
                if (this._el?.tagName === 'DIALOG') this._el.close();
                else this._el?.classList?.remove('ywc-modal-open');
            }
        },
        Tooltip: {
            getInstance: () => null,
            getOrCreateInstance: () => ({ dispose() {} }),
        },
    };
}
