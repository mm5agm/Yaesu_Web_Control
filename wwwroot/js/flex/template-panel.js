/**
 * Clones Razor <template> content into a FlexLayout tab host via React.createElement.
 */
import { registerFlexPopoutWindow } from '/js/flex/dom-shim.js?v=3';

const TEMPLATE_BY_COMPONENT = {
    meters: 'tpl-meters',
    controls: 'tpl-controls',
    remoteAudio: 'tpl-remote-audio',
    spectrumA: 'tpl-spectrum-a',
    spectrumB: 'tpl-spectrum-b',
    vfoA: 'tpl-vfo-a',
    vfoB: 'tpl-vfo-b',
    clarifier: 'tpl-clarifier',
};

/**
 * @param {string} component
 * @returns {string}
 */
export function templateIdFor(component) {
    return TEMPLATE_BY_COMPONENT[component] || 'tpl-empty';
}

/**
 * React factory helper — returns a createElement tree that mounts the cloned template.
 * @param {import('flexlayout-react').TabNode} node
 */
export function createTemplateElement(node) {
    const React = window.React;
    const component = node.getComponent();
    const templateId = templateIdFor(component);

    return React.createElement(TemplatePanel, {
        key: node.getId(),
        templateId,
        component,
        nodeId: node.getId(),
        node,
    });
}

function TemplatePanel({ templateId, component, nodeId, node }) {
    const React = window.React;
    const ref = React.useRef(null);

    React.useEffect(() => {
        const host = ref.current;
        if (!host) return undefined;

        const doc = host.ownerDocument;
        const win = doc?.defaultView;
        if (win && win !== window) registerFlexPopoutWindow(win);

        const tpl =
            document.getElementById(templateId)
            || doc.getElementById?.(templateId);
        host.replaceChildren();
        if (!tpl?.content) {
            host.textContent = `Missing template #${templateId}`;
            return undefined;
        }
        host.appendChild(tpl.content.cloneNode(true));
        try {
            window.dispatchEvent(new CustomEvent('ywc-flex-template-mounted', {
                detail: { component },
            }));
        } catch { /* ignore */ }

        const notifyFit = () => {
            window.dispatchEvent(new Event('ywc-flex-panel-resize'));
            try { window.ywcMetersFit?.refit(); } catch { /* ignore */ }
            try { window.ywcVfoFit?.refit(); } catch { /* ignore */ }
            try { window.ywcWidgetFit?.refit(); } catch { /* ignore */ }
        };

        let listenerId = null;
        if (node?.setEventListener) {
            try {
                listenerId = node.setEventListener('resize', notifyFit);
            } catch { /* older FlexLayout builds */ }
        }

        // Canvas remount after popout / dock-back
        requestAnimationFrame(() => {
            window.dispatchEvent(new Event('resize'));
            notifyFit();
            if (typeof window.updateTxButton === 'function') window.updateTxButton();
            if (typeof window.updateSplitButton === 'function') window.updateSplitButton();
            if (typeof window.applyVfoActiveStyling === 'function') window.applyVfoActiveStyling();
        });

        return () => {
            if (listenerId != null && node?.removeEventListener) {
                try { node.removeEventListener(listenerId); } catch { /* ignore */ }
            }
            host.replaceChildren();
        };
    }, [templateId, component, nodeId, node]);

    return React.createElement('div', {
        ref,
        className: 'ywc-flex-panel-host',
        'data-ywc-component': component,
        style: { height: '100%', width: '100%' },
    });
}
