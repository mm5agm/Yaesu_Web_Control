/**
 * Fit Dock meters into the panel by scaling their natural size.
 * Packs visible meter cells into rows, then uniform-scales to fit the clip box.
 */
const MAX_SCALE = 2.0;
const CELL_H = 135;
const CELL_W = 220;
const HISTORY_W = 260;

/**
 * @param {Document} [doc]
 * @returns {{ refit: () => void, dispose: () => void } | null}
 */
export function initMetersFit(doc = document) {
    const host = doc.getElementById('metersBar');
    if (!host) return null;

    const inner = host.querySelector('.ywc-meters-inner');
    const row = host.querySelector('.ywc-meters-row');
    if (!inner || !row) return null;

    const viewport = host.closest('.ywc-dock-panel-host') || host;

    let raf = 0;
    let lastKey = '';

    const visibleCells = () =>
        [...row.querySelectorAll(':scope > .ywc-meter-cell')]
            .filter((el) => getComputedStyle(el).display !== 'none');

    const cellWidth = (el) =>
        el.classList.contains('ywc-smeter-history') ? HISTORY_W : CELL_W;

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

        const cells = visibleCells();
        const n = cells.length;
        if (!n) return;

        const widths = cells.map(cellWidth);

        const style = getComputedStyle(inner);
        const padX = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
        const padY = (parseFloat(style.paddingTop) || 0) + (parseFloat(style.paddingBottom) || 0);

        const vpStyle = getComputedStyle(viewport);
        const vpPadX = (parseFloat(vpStyle.paddingLeft) || 0) + (parseFloat(vpStyle.paddingRight) || 0);
        const vpPadY = (parseFloat(vpStyle.paddingTop) || 0) + (parseFloat(vpStyle.paddingBottom) || 0);
        const availW = Math.max(1, viewport.clientWidth - vpPadX);
        const availH = Math.max(1, viewport.clientHeight - vpPadY);
        if (availW < 8 || availH < 8) return;

        // Choose column count that maximises scale (prefer fewer rows on ties).
        let best = null;
        for (let cols = 1; cols <= n; cols++) {
            const rowsCount = Math.ceil(n / cols);
            const contentW = packWidth(widths, cols);
            const contentH = rowsCount * CELL_H;
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

        // Lay out at natural px size so flex wraps at the chosen column width,
        // then scale the whole block into the panel.
        row.style.boxSizing = 'border-box';
        row.style.width = `${best.contentW}px`;
        row.style.flexWrap = 'wrap';

        inner.style.boxSizing = 'content-box';
        inner.style.transformOrigin = 'top left';
        inner.style.width = `${best.contentW}px`;
        inner.style.height = `${best.contentH}px`;
        inner.style.transform = `scale(${best.scale})`;

        const boxW = best.contentW + padX;
        const boxH = best.contentH + padY;
        inner.style.marginRight = `${boxW * (best.scale - 1)}px`;
        inner.style.marginBottom = `${boxH * (best.scale - 1)}px`;

        host.style.setProperty('--ywc-meters-scale', String(best.scale));
        host.dataset.metersCols = String(best.cols);
        host.dataset.metersScale = best.scale.toFixed(3);
    };

    const schedule = () => {
        if (raf) return;
        raf = requestAnimationFrame(fit);
    };

    const ro = new ResizeObserver(schedule);
    ro.observe(viewport);
    if (viewport !== host) ro.observe(host);

    const mo = new MutationObserver(schedule);
    for (const el of host.querySelectorAll('.ywc-smeter-history')) {
        mo.observe(el, { attributes: true, attributeFilter: ['style', 'hidden', 'class'] });
    }

    schedule();
    // Gauges paint canvases just after first layout — refit once settled.
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
