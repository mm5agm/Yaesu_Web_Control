/**
 * Bootstrap Modal/Tooltip shim so unchanged site.js keeps working without Bootstrap JS.
 */
export function installBootstrapShim() {
    if (window.bootstrap) return;
    const modalInstances = new WeakMap();
    window.bootstrap = {
        Modal: class {
            constructor(el) {
                this._el = el;
                modalInstances.set(el, this);
            }
            static getInstance(el) {
                return modalInstances.get(el);
            }
            show() {
                if (this._el?.tagName === 'DIALOG') this._el.showModal();
                else this._el?.classList?.add('ywc-modal-open');
            }
            hide() {
                if (this._el?.tagName === 'DIALOG') this._el.close();
                else this._el?.classList?.remove('ywc-modal-open');
            }
        },
        Tooltip: {
            getInstance: () => null,
            getOrCreateInstance: () => ({ dispose() {} }),
        },
    };
}
