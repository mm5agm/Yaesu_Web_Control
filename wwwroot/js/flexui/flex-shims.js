/**
 * Minimal Bootstrap-shim for the Flex UI page.
 *
 * /flexui deliberately does not ship Bootstrap's JS bundle, but the markup
 * duplicated from Index.cshtml and (unchanged) site.js still expect a few
 * Bootstrap components to exist. This classic script installs just enough of
 * them before site.js runs:
 *
 *   - Modal    -> drives <dialog> elements and Bootstrap .modal divs
 *   - Tooltip  -> inert (tooltips are a no-op on the flex page)
 *   - Dropdown -> toggles a sibling .dropdown-menu (apps launcher, Panels menu)
 *
 * It is loaded in _FlexLayout.cshtml *before* site.js.
 */
(function () {
    if (window.bootstrap) return;

    const modalInstances = new WeakMap();
    const openModals = new Set();

    function openModal(el) {
        if (!el) return;
        if (el.tagName === 'DIALOG') { try { el.showModal(); } catch { el.setAttribute('open', ''); } return; }
        el.classList.add('show');
        el.style.display = 'block';
        el.removeAttribute('aria-hidden');
        document.body.classList.add('modal-open');
        openModals.add(el);
    }

    function closeModal(el) {
        if (!el) return;
        if (el.tagName === 'DIALOG') { try { el.close(); } catch { el.removeAttribute('open'); } return; }
        el.classList.remove('show');
        el.style.display = 'none';
        el.setAttribute('aria-hidden', 'true');
        document.body.classList.remove('modal-open');
        openModals.delete(el);
    }

    class Modal {
        constructor(el) {
            this._el = el;
            modalInstances.set(el, this);
        }
        static getInstance(el) { return modalInstances.get(el); }
        static getOrCreateInstance(el) { return modalInstances.get(el) || new Modal(el); }
        show() { openModal(this._el); }
        hide() { closeModal(this._el); }
        toggle() { this._el?.classList?.contains('show') ? this.hide() : this.show(); }
        dispose() { modalInstances.delete(this._el); }
    }

    const Tooltip = {
        getInstance: () => null,
        getOrCreateInstance: () => ({ dispose() { /* no-op */ } }),
        dispose: () => {},
    };

    class Dropdown {
        constructor(el) { this._el = el; }
        static getOrCreateInstance(el) { return new Dropdown(el); }
        toggle() {
            const menu = this._el?.parentElement?.querySelector('.dropdown-menu')
                || document.querySelector(this._el?.getAttribute('data-bs-target'));
            if (!menu) return;
            const willShow = !menu.classList.contains('show');
            document.querySelectorAll('.dropdown-menu.show').forEach((m) => {
                m.classList.remove('show');
                m.style.display = '';
            });
            if (willShow) {
                menu.classList.add('show');
                menu.style.display = 'block';
            }
        }
    }

    // Delegated wiring so cloned flex DOM is covered too.
    document.addEventListener('click', function (e) {
        const toggle = e.target.closest?.('[data-bs-toggle="dropdown"]');
        if (toggle) {
            e.preventDefault();
            e.stopPropagation();
            new Dropdown(toggle).toggle();
            return;
        }
        const dismiss = e.target.closest?.('[data-bs-dismiss="modal"]');
        if (dismiss) {
            const modalEl = dismiss.closest('.modal, dialog');
            if (modalEl) closeModal(modalEl);
            return;
        }
        // Click outside an open dropdown closes it.
        if (!e.target.closest?.('.dropdown-menu') && !e.target.closest?.('[data-bs-toggle="dropdown"]')) {
            document.querySelectorAll('.dropdown-menu.show').forEach((m) => {
                m.classList.remove('show');
                m.style.display = '';
            });
        }
    });

    window.bootstrap = { Modal, Tooltip, Dropdown };
})();
