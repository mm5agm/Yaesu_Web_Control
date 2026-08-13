/**
 * Fit Flex toolbar-style panels (controls, clarifier, remote audio, VFO ops)
 * the same way VFOs scale into their clip box.
 */
import { createHostWatcher, createUniformScaleFitter, findHostById } from '/js/flex/panel-fit.js?v=1';

const PANELS = [
    {
        id: 'controlsBar',
        hostClass: 'ywc-controls',
        innerSelector: '.ywc-controls-inner',
        minW: 280,
        cssVar: '--ywc-controls-scale',
        dataAttr: 'controlsScale',
    },
    {
        id: 'clarifierBar',
        hostClass: 'ywc-clarifier',
        innerSelector: '.ywc-clarifier-inner',
        minW: 240,
        cssVar: '--ywc-clarifier-scale',
        dataAttr: 'clarifierScale',
    },
    {
        id: 'remoteAudioBar',
        hostClass: 'ywc-remote-audio',
        innerSelector: '.ywc-remote-audio-inner',
        minW: 280,
        cssVar: '--ywc-audio-scale',
        dataAttr: 'audioScale',
    },
    {
        id: 'vfoOpsBar',
        hostClass: 'ywc-vfo-ops',
        innerSelector: '.ywc-vfo-ops-inner',
        minW: 72,
        cssVar: '--ywc-vfo-ops-scale',
        dataAttr: 'vfoOpsScale',
    },
];

/**
 * @returns {{ refit: () => void, dispose: () => void }}
 */
export function initWidgetFit() {
    return createHostWatcher({
        findHosts: () => PANELS.map((p) => findHostById(p.id, p.hostClass)),
        createFitter: (host) => {
            const spec = PANELS.find((p) => host.id === p.id);
            if (!spec) return null;
            return createUniformScaleFitter(host, {
                innerSelector: spec.innerSelector,
                minW: spec.minW,
                maxScale: 4,
                cssVar: spec.cssVar,
                dataAttr: spec.dataAttr,
            });
        },
    });
}
