/**
 * Fit Flex VFO panels by scaling their natural size into the clip box.
 * Same mechanism as meters-fit: lay out at intrinsic px size, then uniform-scale.
 */
const MAX_SCALE = 4.0;
const MIN_W = 360;

/**
 * @param {HTMLElement} host  `.ywc-vfo` root
 * @returns {{ refit: () => void, dispose: () => void } | null}
 */
function createFitter(host) {
    const inner = host.querySelector('.ywc-vfo-inner');
    const body = host.querySelector('.ywc-vfo-body');
    if (!inner || !body) return null;

    const viewport = host.closest('.ywc-flex-panel-host') || host;

    let raf = 0;
    let lastKey = '';
    let applying = false;

    const measure = () => ({
        w: Math.max(1, Math.ceil(inner.scrollWidth), Math.ceil(body.scrollWidth)),
        h: Math.max(1, Math.ceil(inner.scrollHeight), Math.ceil(body.scrollHeight)),
    });

    const fit = () => {
        raf = 0;
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

            // Intrinsic width (no wrap-to-panel), then a few wider widths in case
            // undoing wraps shortens the block and raises the uniform scale.
            inner.style.width = 'max-content';
            inner.style.transform = 'none';
            inner.style.marginRight = '0';
            inner.style.marginBottom = '0';
            void inner.offsetHeight;

            const intrinsic = measure();
            const minW = Math.max(MIN_W, intrinsic.w);

            /** @type {{ scale: number, contentW: number, contentH: number }} */
            let best = {
                scale: Math.min(availW / intrinsic.w, availH / intrinsic.h, MAX_SCALE),
                contentW: intrinsic.w,
                contentH: intrinsic.h,
            };

            const maxLayoutW = Math.max(minW, availW);
            const steps = 6;
            for (let i = 1; i <= steps; i++) {
                const w = Math.round(minW + (maxLayoutW - minW) * (i / steps));
                if (w <= minW + 8) continue;
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
            lastKey = key;

            inner.style.width = `${best.contentW}px`;
            inner.style.height = `${best.contentH}px`;
            inner.style.transform = `scale(${best.scale})`;
            inner.style.marginRight = `${best.contentW * (best.scale - 1)}px`;
            inner.style.marginBottom = `${best.contentH * (best.scale - 1)}px`;

            host.style.setProperty('--ywc-vfo-scale', String(best.scale));
            host.dataset.vfoScale = best.scale.toFixed(3);
        } finally {
            applying = false;
        }
    };

    const schedule = () => {
        if (raf || applying) return;
        raf = requestAnimationFrame(fit);
    };

    const ro = new ResizeObserver(schedule);
    ro.observe(viewport);
    if (viewport !== host) ro.observe(host);

    const mo = new MutationObserver(() => {
        if (!applying) schedule();
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

/**
 * @param {Document} [doc]
 * @returns {{ refit: () => void, dispose: () => void } | null}
 */
export function initVfoFit(doc = document) {
    /** @type {Map<HTMLElement, { refit: () => void, dispose: () => void }>} */
    const byHost = new Map();

    const sync = () => {
        const hosts = ['vfoACol', 'vfoBCol']
            .map((id) => doc.getElementById(id))
            .filter((el) => el?.classList?.contains('ywc-vfo'));

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
        return byHost.size > 0;
    };

    if (!sync()) return null;

    return {
        refit() {
            sync();
            for (const f of byHost.values()) f.refit();
        },
        dispose() {
            for (const f of byHost.values()) f.dispose();
            byHost.clear();
        },
    };
}
