/**
 * Fit Flex VFO panels by scaling their natural size into the clip box.
 */
import { createHostWatcher, createUniformScaleFitter, findHostById } from '/js/flex/panel-fit.js?v=1';

const VFO_IDS = ['vfoACol', 'vfoBCol'];

/**
 * @returns {{ refit: () => void, dispose: () => void }}
 */
export function initVfoFit() {
    return createHostWatcher({
        findHosts: () => VFO_IDS.map((id) => findHostById(id, 'ywc-vfo')),
        createFitter: (host) => createUniformScaleFitter(host, {
            innerSelector: '.ywc-vfo-inner',
            bodySelector: '.ywc-vfo-body',
            minW: 360,
            maxScale: 4,
            cssVar: '--ywc-vfo-scale',
            dataAttr: 'vfoScale',
        }),
    });
}
