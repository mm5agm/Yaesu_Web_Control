/**
 * Shared Flex panel fitting: uniform-scale intrinsic content into the clip box.
 * FlexLayout mounts templates asynchronously (and again on popout), so callers
 * use createHostWatcher rather than giving up on first miss.
 */

/**
 * @param {string} id
 * @param {string} [hostClass]
 * @returns {HTMLElement | null}
 */
export function findHostById(id, hostClass) {
    const el = typeof window.ywcGetElementById === 'function'
        ? window.ywcGetElementById(id)
        : document.getElementById(id);
    if (!el) return null;
    if (hostClass && !el.classList.contains(hostClass)) return null;
    return el;
}

/**
 * @param {HTMLElement} host
 * @param {{
 *   innerSelector: string,
 *   bodySelector?: string,
 *   minW?: number,
 *   maxScale?: number,
 *   cssVar?: string,
 *   dataAttr?: string,
 * }} options
 * @returns {{ refit: () => void, dispose: () => void } | null}
 */
export function createUniformScaleFitter(host, options) {
    const inner = host.querySelector(options.innerSelector);
    if (!inner) return null;
    const body = options.bodySelector
        ? host.querySelector(options.bodySelector)
        : null;

    const minW = options.minW ?? 240;
    const maxScale = options.maxScale ?? 4.0;
    const cssVar = options.cssVar || '--ywc-panel-scale';
    const dataAttr = options.dataAttr || 'panelScale';

    const viewport =
        host.closest('.ywc-flex-panel-host')
        || host.closest('.flexlayout__tab')
        || host;

    let raf = 0;
    let applying = false;
    let pending = false;
    let lastKey = '';

    const measure = () => ({
        w: Math.max(
            1,
            Math.ceil(inner.scrollWidth),
            body ? Math.ceil(body.scrollWidth) : 0,
        ),
        h: Math.max(
            1,
            Math.ceil(inner.scrollHeight),
            body ? Math.ceil(body.scrollHeight) : 0,
        ),
    });

    const fit = () => {
        raf = 0;
        pending = false;
        if (!host.isConnected) return;

        const vpStyle = getComputedStyle(viewport);
        const vpPadX = (parseFloat(vpStyle.paddingLeft) || 0) + (parseFloat(vpStyle.paddingRight) || 0);
        const vpPadY = (parseFloat(vpStyle.paddingTop) || 0) + (parseFloat(vpStyle.paddingBottom) || 0);
        const availW = Math.max(1, viewport.clientWidth - vpPadX);
        const availH = Math.max(1, viewport.clientHeight - vpPadY);
        if (availW < 8 || availH < 8) return;

        applying = true;
        try {
            inner.style.boxSizing = 'border-box';
            inner.style.transformOrigin = 'top left';
            inner.style.overflow = 'visible';
            inner.style.minHeight = '0';
            inner.style.maxHeight = 'none';
            inner.style.height = 'auto';
            inner.style.flex = '0 0 auto';
            inner.style.alignSelf = 'flex-start';

            if (body) {
                body.style.boxSizing = 'border-box';
                body.style.height = 'auto';
                body.style.flex = '0 0 auto';
                body.style.minHeight = '0';
                body.style.overflow = 'visible';
            }

            inner.style.width = 'max-content';
            inner.style.transform = 'none';
            inner.style.marginRight = '0';
            inner.style.marginBottom = '0';
            inner.style.marginLeft = '0';
            inner.style.marginTop = '0';
            void inner.offsetHeight;

            const intrinsic = measure();
            const floorW = Math.max(minW, Math.min(intrinsic.w, availW));

            /** @type {{ scale: number, contentW: number, contentH: number }} */
            let best = {
                scale: Math.min(availW / intrinsic.w, availH / intrinsic.h, maxScale),
                contentW: intrinsic.w,
                contentH: intrinsic.h,
            };

            const maxLayoutW = Math.max(floorW, availW, intrinsic.w);
            const steps = 8;
            for (let i = 0; i <= steps; i++) {
                const w = Math.round(floorW + (maxLayoutW - floorW) * (i / steps));
                if (w < 32) continue;
                inner.style.width = `${w}px`;
                void inner.offsetHeight;
                const m = measure();
                const contentW = Math.max(w, m.w);
                const contentH = m.h;
                const scale = Math.min(availW / contentW, availH / contentH, maxScale);
                if (scale > best.scale + 1e-6) {
                    best = { scale, contentW, contentH };
                }
            }

            if (!Number.isFinite(best.scale) || best.scale <= 0) return;

            const key = `${best.contentW}x${best.contentH}@${availW}x${availH}:${best.scale.toFixed(4)}`;
            if (key === lastKey) return;
            lastKey = key;

            inner.style.width = `${best.contentW}px`;
            inner.style.height = `${best.contentH}px`;
            inner.style.transform = `scale(${best.scale})`;

            const visualW = best.contentW * best.scale;
            const visualH = best.contentH * best.scale;
            const padX = Math.max(0, (availW - visualW) / 2);
            const padY = Math.max(0, (availH - visualH) / 2);
            inner.style.marginLeft = `${padX}px`;
            inner.style.marginTop = `${padY}px`;
            inner.style.marginRight = `${best.contentW * (best.scale - 1) + padX}px`;
            inner.style.marginBottom = `${best.contentH * (best.scale - 1) + padY}px`;

            host.style.setProperty(cssVar, String(best.scale));
            host.dataset[dataAttr] = best.scale.toFixed(3);
        } finally {
            applying = false;
            if (pending) schedule();
        }
    };

    const schedule = () => {
        if (applying) {
            pending = true;
            return;
        }
        if (raf) return;
        raf = requestAnimationFrame(fit);
    };

    const ro = new ResizeObserver(schedule);
    ro.observe(viewport);
    if (viewport !== host) ro.observe(host);
    const tab = host.closest('.flexlayout__tab');
    if (tab && tab !== viewport) ro.observe(tab);

    const mo = new MutationObserver(() => {
        if (!applying) {
            lastKey = '';
            schedule();
        }
    });
    mo.observe(host, {
        attributes: true,
        attributeFilter: ['hidden', 'class', 'style'],
        subtree: true,
    });

    schedule();
    requestAnimationFrame(() => requestAnimationFrame(schedule));

    return {
        refit() {
            lastKey = '';
            schedule();
        },
        dispose() {
            if (raf) cancelAnimationFrame(raf);
            ro.disconnect();
            mo.disconnect();
        },
    };
}

/**
 * Keep fitters attached as FlexLayout clones / pops templates.
 * @param {{
 *   findHosts: () => HTMLElement[],
 *   createFitter: (host: HTMLElement) => ({ refit: () => void, dispose: () => void } | null),
 * }} spec
 * @returns {{ refit: () => void, dispose: () => void }}
 */
export function createHostWatcher(spec) {
    /** @type {Map<HTMLElement, { refit: () => void, dispose: () => void }>} */
    const byHost = new Map();

    const sync = () => {
        const hosts = spec.findHosts().filter(Boolean);
        const live = new Set(hosts);
        for (const [el, fitter] of [...byHost]) {
            if (!live.has(el) || !el.isConnected) {
                fitter.dispose();
                byHost.delete(el);
            }
        }
        for (const el of hosts) {
            if (byHost.has(el)) continue;
            const fitter = spec.createFitter(el);
            if (fitter) byHost.set(el, fitter);
        }
        return byHost.size;
    };

    const refitAll = () => {
        sync();
        for (const f of byHost.values()) f.refit();
    };

    const workspace = document.getElementById('ywcFlexHost') || document.body;
    const hostMo = new MutationObserver(() => {
        sync();
    });
    hostMo.observe(workspace, { childList: true, subtree: true });

    window.addEventListener('resize', refitAll);
    window.addEventListener('ywc-flex-panel-resize', refitAll);

    sync();
    requestAnimationFrame(refitAll);
    setTimeout(refitAll, 0);
    setTimeout(refitAll, 120);

    return {
        refit: refitAll,
        dispose() {
            window.removeEventListener('resize', refitAll);
            window.removeEventListener('ywc-flex-panel-resize', refitAll);
            hostMo.disconnect();
            for (const f of byHost.values()) f.dispose();
            byHost.clear();
        },
    };
}
