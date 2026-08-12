/**
 * Dockview group header action — pop the whole tab group into a new window.
 */
export class GroupPopoutAction {
    constructor() {
        this.element = document.createElement('div');
        this.element.className = 'ywc-group-popout-action';

        this._btn = document.createElement('button');
        this._btn.type = 'button';
        this._btn.className = 'ywc-group-popout-btn';
        this._btn.title = 'Pop out group to window';
        this._btn.setAttribute('aria-label', 'Pop out group to separate window');
        this._btn.textContent = '⧉';
        this.element.appendChild(this._btn);
    }

    init({ containerApi, api, group }) {
        this._containerApi = containerApi;
        this._group = group;

        const sync = () => {
            this._btn.hidden = api.location?.type === 'popout';
        };
        sync();

        this._btn.addEventListener('click', this._onClick = (e) => {
            e.stopPropagation();
            e.preventDefault();
            containerApi.addPopoutGroup(group, { popoutUrl: '/popout.html' }).catch(() => {
                alert('Popout blocked. Allow popups for this site.');
            });
        });

        this._locationDisposable = api.onDidLocationChange?.(() => sync());
    }

    dispose() {
        this._btn.removeEventListener('click', this._onClick);
        this._locationDisposable?.dispose?.();
        this._onClick = null;
        this._containerApi = null;
        this._group = null;
    }
}
