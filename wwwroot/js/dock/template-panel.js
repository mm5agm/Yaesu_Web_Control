/**
 * Mounts Dock-only Razor <template> content into a Dockview panel host.
 */
const TEMPLATE_BY_PANEL = {
    meters: 'tpl-meters',
    controls: 'tpl-controls',
    remoteAudio: 'tpl-remote-audio',
    spectrumA: 'tpl-spectrum-a',
    spectrumB: 'tpl-spectrum-b',
    vfoA: 'tpl-vfo-a',
    vfoB: 'tpl-vfo-b',
    clarifier: 'tpl-clarifier',
};

export class TemplatePanel {
    /**
     * @param {string} [panelId]  Dockview panel id (meters, vfoA, …)
     */
    constructor(panelId = '') {
        this._panelId = panelId;
        this.element = document.createElement('div');
        this.element.className = 'ywc-dock-panel-host';
    }

    init(params) {
        // createComponent() runs before panel params exist — templateId arrives here.
        const templateId =
            params?.params?.templateId
            ?? TEMPLATE_BY_PANEL[this._panelId]
            ?? 'tpl-empty';

        const tpl = document.getElementById(templateId);
        if (!tpl?.content) {
            this.element.textContent = `Missing template #${templateId}`;
            return;
        }
        this.element.appendChild(tpl.content.cloneNode(true));
    }

    dispose() {
        this.element.replaceChildren();
    }
}
