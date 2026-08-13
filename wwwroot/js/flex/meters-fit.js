/**
 * Fit Flex meters into the panel by scaling their natural size.
 * Packs visible meter cells into rows, then uniform-scales to fit the clip box.
 */
import { createHostWatcher, findHostById } from '/js/flex/panel-fit.js?v=1';

const MAX_SCALE = 4.0;
const CELL_H = 108;
const CELL_W = 220;
const HISTORY_W = 260;
const HISTORY_H = 135;

/**
 * @param {HTMLElement} host
 * @returns {{ refit: () => void, dispose: () => void } | null}
 */
function createMetersFitter(host) {
    const inner = host.querySelector('.ywc-meters-inner');
    const row = host.querySelector('.ywc-meters-row');
    if (!inner || !row) return null;

    const viewport =
        host.closest('.ywc-flex-panel-host')
        || host.closest('.flexlayout__tab')
        || host;

    let raf = 0;
    let applying = false;
    let pending = false;
    let lastKey = '';

    const visibleCells = () =>
        [...row.querySelectorAll(':scope > .ywc-meter-cell')]
            .filter((el) => getComputedStyle(el).display !== 'none');

    const isHistory = (el) => el.classList.contains('ywc-smeter-history');

    const cellWidth = (el) => (isHistory(el) ? HISTORY_W : CELL_W);

    const cellHeight = (el) => (isHistory(el) ? HISTORY_H : CELL_H);

    /** Widest row width when packing cells left-to-right with `cols` per row. */
    const packWidth = (widths, cols) => {
        let maxRowW = 0;
        let cur = 0;
        let c = 0;
        for (const w of widths) {
            if (c === cols) {
                maxRowW = Math.max(maxRowW, cur);
                cur = 0;
                c = 0;
            }
            cur += w;
            c += 1;
        }
        return Math.max(maxRowW, cur);
    };

    const fit = () => {
        raf = 0;
        pending = false;
        if (!host.isConnected) return;

        const cells = visibleCells();
        const n = cells.length;
        if (!n) return;

        const widths = cells.map(cellWidth);
        const rowH = Math.max(...cells.map(cellHeight));

        const style = getComputedStyle(inner);
        const padX = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
        const padY = (parseFloat(style.paddingTop) || 0) + (parseFloat(style.paddingBottom) || 0);

        const vpStyle = getComputedStyle(viewport);
        const vpPadX = (parseFloat(vpStyle.paddingLeft) || 0) + (parseFloat(vpStyle.paddingRight) || 0);
        const vpPadY = (parseFloat(vpStyle.paddingTop) || 0) + (parseFloat(vpStyle.paddingBottom) || 0);
        const availW = Math.max(1, viewport.clientWidth - vpPadX);
        const availH = Math.max(1, viewport.clientHeight - vpPadY);
        if (availW < 8 || availH < 8) return;

        applying = true;
        try {
            let best = null;
            for (let cols = 1; cols <= n; cols++) {
                const rowsCount = Math.ceil(n / cols);
                const contentW = packWidth(widths, cols);
                const contentH = rowsCount * rowH;
                const scale = Math.min(
                    availW / (contentW + padX),
                    availH / (contentH + padY),
                    MAX_SCALE,
                );
                if (
                    !best
                    || scale > best.scale + 1e-6
                    || (Math.abs(scale - best.scale) <= 1e-6 && cols > best.cols)
                ) {
                    best = { scale, cols, contentW, contentH, rowsCount };
                }
            }
            if (!best) return;

            const key = `${n}:${best.cols}:${best.contentW}x${best.contentH}@${availW}x${availH}:${best.scale.toFixed(4)}`;
            if (key === lastKey) return;
            lastKey = key;

            row.style.boxSizing = 'border-box';
            row.style.width = `${best.contentW}px`;
            row.style.flexWrap = 'wrap';

            inner.style.boxSizing = 'content-box';
            inner.style.transformOrigin = 'top left';
            inner.style.overflow = 'visible';
            inner.style.flex = '0 0 auto';
            inner.style.alignSelf = 'flex-start';
            inner.style.width = `${best.contentW}px`;
            inner.style.height = `${best.contentH}px`;
            inner.style.transform = `scale(${best.scale})`;

            const boxW = best.contentW + padX;
            const boxH = best.contentH + padY;
            const visualW = boxW * best.scale;
            const visualH = boxH * best.scale;
            const padLeft = Math.max(0, (availW - visualW) / 2);
            const padTop = Math.max(0, (availH - visualH) / 2);
            inner.style.marginLeft = `${padLeft}px`;
            inner.style.marginTop = `${padTop}px`;
            inner.style.marginRight = `${boxW * (best.scale - 1) + padLeft}px`;
            inner.style.marginBottom = `${boxH * (best.scale - 1) + padTop}px`;

            host.style.setProperty('--ywc-meters-scale', String(best.scale));
            host.dataset.metersCols = String(best.cols);
            host.dataset.metersScale = best.scale.toFixed(3);
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
    for (const el of host.querySelectorAll('.ywc-smeter-history')) {
        mo.observe(el, { attributes: true, attributeFilter: ['style', 'hidden', 'class'] });
    }

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
 * @returns {{ refit: () => void, dispose: () => void }}
 */
export function initMetersFit() {
    return createHostWatcher({
        findHosts: () => {
            const el = findHostById('metersBar', 'ywc-meters');
            return el ? [el] : [];
        },
        createFitter: createMetersFitter,
    });
}
