/**
 * Fit Flex VFO panels by scaling their natural size into the clip box.
 * Same mechanism as meters-fit: lay out at intrinsic px size, then uniform-scale.
 *
 * FlexLayout mounts templates asynchronously (and again on popout), so this
 * module keeps watching for VFO hosts rather than giving up on first miss.
 */
const MAX_SCALE = 4.0;
const MIN_W = 360;
const VFO_IDS = ['vfoACol', 'vfoBCol'];

/**
 * @param {HTMLElement} host  `.ywc-vfo` root
 * @returns {{ refit: () => void, dispose: () => void } | null}
 */
function createFitter(host) {
    const inner = host.querySelector('.ywc-vfo-inner');
    const body = host.querySelector('.ywc-vfo-body');
    if (!inner || !body) return null;

    const viewport =
        host.closest('.ywc-flex-panel-host')
        || host.closest('.flexlayout__tab')
        || host;

    let raf = 0;
    let applying = false;
    let pending = false;
    let lastKey = '';

    const measure = () => ({
        w: Math.max(1, Math.ceil(inner.scrollWidth), Math.ceil(body.scrollWidth)),
        h: Math.max(1, Math.ceil(inner.scrollHeight), Math.ceil(body.scrollHeight)),
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

            body.style.boxSizing = 'border-box';
            body.style.height = 'auto';
            body.style.flex = '0 0 auto';
            body.style.minHeight = '0';
            body.style.overflow = 'visible';

            // Intrinsic width (no wrap-to-panel), then a few candidate widths
            // so wrapping can raise the uniform scale in a tall/narrow panel.
            inner.style.width = 'max-content';
            inner.style.transform = 'none';
            inner.style.marginRight = '0';
            inner.style.marginBottom = '0';
            inner.style.marginLeft = '0';
            inner.style.marginTop = '0';
            void inner.offsetHeight;

            const intrinsic = measure();
            const minW = Math.max(MIN_W, Math.min(intrinsic.w, availW));

            /** @type {{ scale: number, contentW: number, contentH: number }} */
            let best = {
                scale: Math.min(availW / intrinsic.w, availH / intrinsic.h, MAX_SCALE),
                contentW: intrinsic.w,
                contentH: intrinsic.h,
            };

            const maxLayoutW = Math.max(minW, availW, intrinsic.w);
            const steps = 8;
            for (let i = 0; i <= steps; i++) {
                const w = Math.round(minW + (maxLayoutW - minW) * (i / steps));
                if (w < 32) continue;
                inner.style.width = `${w}px`;
                void inner.offsetHeight;
                const m = measure();
                const contentW = Math.max(w, m.w);
                const contentH = m.h;
                const scale = Math.min(availW / contentW, availH / contentH, MAX_SCALE);
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

            host.style.setProperty('--ywc-vfo-scale', String(best.scale));
            host.dataset.vfoScale = best.scale.toFixed(3);
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
        attributeFilter: ['hidden', 'class'],
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

function findVfoHost(id) {
    if (typeof window.ywcGetElementById === 'function') {
        const el = window.ywcGetElementById(id);
        if (el?.classList?.contains('ywc-vfo')) return el;
    }
    const el = document.getElementById(id);
    return el?.classList?.contains('ywc-vfo') ? el : null;
}

/**
 * @returns {{ refit: () => void, dispose: () => void }}
 */
export function initVfoFit() {
    /** @type {Map<HTMLElement, { refit: () => void, dispose: () => void }>} */
    const byHost = new Map();

    const sync = () => {
        const hosts = VFO_IDS.map(findVfoHost).filter(Boolean);
        const live = new Set(hosts);
        for (const [el, fitter] of [...byHost]) {
            if (!live.has(el) || !el.isConnected) {
                fitter.dispose();
                byHost.delete(el);
            }
        }
        for (const el of hosts) {
            if (byHost.has(el)) continue;
            const fitter = createFitter(el);
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
        // Attach/dispose only — per-host ResizeObserver handles size changes.
        // Refitting on every childList mutation (freq digits, button classes)
        // would jank the live VFO.
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
