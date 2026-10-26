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
 *   - Dropdown -> toggles a sibling .dropdown-menu (apps launcher, Panels menu,
 *                 the nav's Views and Menu), with Bootstrap's keys: Down/Up to
 *                 open and move between items, Esc to close
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

    // Only the menus this shim owns (those with a [data-bs-toggle="dropdown"]
    // toggler) may be closed here. YWC's Yaesu key widgets also render a
    // .dropdown-menu but manage their own open/close; clearing those by class
    // alone made left-click menus (Apps, Band, Mode, ...) open and immediately
    // vanish because the click bubbled up to this handler.
    function menuFor(toggle) {
        if (!toggle) return null;
        const target = toggle.getAttribute?.('data-bs-target');
        if (target) return document.querySelector(target);
        return toggle.parentElement?.querySelector('.dropdown-menu') || null;
    }

    function closeMenus(except) {
        document.querySelectorAll('[data-bs-toggle="dropdown"]').forEach((toggle) => {
            const menu = menuFor(toggle);
            if (!menu || menu === except) return;
            menu.classList.remove('show');
            menu.style.display = '';
            toggle.setAttribute('aria-expanded', 'false');
        });
    }

    class Dropdown {
        constructor(el) { this._el = el; }
        static getOrCreateInstance(el) { return new Dropdown(el); }
        toggle() {
            const menu = menuFor(this._el);
            if (!menu) return;
            const willShow = !menu.classList.contains('show');
            closeMenus(willShow ? menu : undefined);
            if (willShow) {
                menu.classList.add('show');
                menu.style.display = 'block';
                this._el.setAttribute('aria-expanded', 'true');
            } else {
                this._el.setAttribute('aria-expanded', 'false');
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
        // Picking a link closes its menu, as Bootstrap's do.
        if (e.target.closest?.('.dropdown-menu a.dropdown-item[href]')) {
            closeMenus();
            return;
        }
        const dismiss = e.target.closest?.('[data-bs-dismiss="modal"]');
        if (dismiss) {
            const modalEl = dismiss.closest('.modal, dialog');
            if (modalEl) closeModal(modalEl);
            return;
        }
        // Click outside an open shim dropdown closes it.
        if (!e.target.closest?.('.dropdown-menu') && !e.target.closest?.('[data-bs-toggle="dropdown"]')) {
            closeMenus();
        }
    });

    // Keys, as Bootstrap's dropdowns: Down/Up on a toggle opens its menu at
    // the first/last item, Down/Up in the menu moves between items (wrapping),
    // Esc closes it and puts focus back on the toggle. Only this shim's menus.
    document.addEventListener('keydown', function (e) {
        if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp' && e.key !== 'Escape') return;
        let toggle = e.target.closest?.('[data-bs-toggle="dropdown"]');
        if (!toggle) {
            const inMenu = e.target.closest?.('.dropdown-menu');
            if (!inMenu) return;
            toggle = [...document.querySelectorAll('[data-bs-toggle="dropdown"]')].find(t => menuFor(t) === inMenu);
        }
        const menu = menuFor(toggle);
        if (!menu) return;
        const open = menu.classList.contains('show');
        if (e.key === 'Escape') {
            if (!open) return;
            closeMenus();
            toggle.focus();
            e.preventDefault();
            return;
        }
        const items = [...menu.querySelectorAll('.dropdown-item')].filter(i => !i.disabled && !i.classList.contains('disabled'));
        if (!items.length) return;
        e.preventDefault();
        if (!open) new Dropdown(toggle).toggle();
        const at = items.indexOf(document.activeElement);
        const next = e.key === 'ArrowDown'
            ? (at < 0 ? 0 : (at + 1) % items.length)
            : (at < 0 ? items.length - 1 : (at - 1 + items.length) % items.length);
        items[next].focus();
    });

    window.bootstrap = { Modal, Tooltip, Dropdown };
})();
