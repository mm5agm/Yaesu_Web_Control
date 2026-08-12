/**
 * dockview
 * @version 7.0.4
 * @link https://github.com/mathuo/dockview
 * @license MIT
 */
import { defineModule, DockviewCompositeDisposable, DockviewDisposable, findRelativeZIndexParent, LiveRegionModule, resolveMessages, registerModules, markDockviewPackageLoaded } from 'dockview-core';
export * from 'dockview-core';

/**
 * dockview-modules
 * @version 7.0.4
 * @link https://github.com/mathuo/dockview
 * @license MIT
 */

class TabGroupChipsService {
    constructor(host) {
        this._host = host;
    }
    attachToGroup(group) {
        return new DockviewCompositeDisposable(group.model.onDidCreateTabGroup((e) => {
            this._host.fireDidCreateTabGroup(e);
        }), group.model.onDidDestroyTabGroup((e) => {
            this._host.fireDidDestroyTabGroup(e);
        }), group.model.onDidAddPanelToTabGroup((e) => {
            this._host.fireDidAddPanelToTabGroup(e);
        }), group.model.onDidRemovePanelFromTabGroup((e) => {
            this._host.fireDidRemovePanelFromTabGroup(e);
        }), group.model.onDidTabGroupChange((e) => {
            this._host.fireDidTabGroupChange(e);
        }), group.model.onDidTabGroupCollapsedChange((e) => {
            this._host.fireDidTabGroupCollapsedChange(e);
        }));
    }
    dispose() {
        // No internal state to tear down — emitters live on the host.
    }
}
const TabGroupChipsModule = defineModule({
    name: 'TabGroupChips',
    serviceKey: 'tabGroupChipsService',
    create: (host) => new TabGroupChipsService(host),
    init: (host, service) => {
        // Self-attach to existing and future groups; tear down when groups
        // are removed. Component doesn't need to know about this wiring.
        const perGroupDisposables = new Map();
        return new DockviewCompositeDisposable(host.onDidAddGroup((group) => {
            perGroupDisposables.set(group, service.attachToGroup(group));
        }), host.onDidRemoveGroup((group) => {
            var _a;
            (_a = perGroupDisposables.get(group)) === null || _a === void 0 ? void 0 : _a.dispose();
            perGroupDisposables.delete(group);
        }), {
            dispose: () => {
                for (const d of perGroupDisposables.values()) {
                    d.dispose();
                }
                perGroupDisposables.clear();
            },
        });
    },
});

function popoverZIndexFor(target) {
    if (!(target instanceof HTMLElement)) {
        return undefined;
    }
    // Floating overlays live in the shell as siblings of the popover anchor
    // and the AriaLevelTracker sets their inline z-index. Without this, a
    // popover opened from inside a floating group would render behind it
    // because they share the shell stacking context.
    const relativeParent = findRelativeZIndexParent(target);
    return (relativeParent === null || relativeParent === void 0 ? void 0 : relativeParent.style.zIndex)
        ? `calc(${relativeParent.style.zIndex} * 2)`
        : undefined;
}
let _nextId = 0;
const nextContextMenuItemId = () => `dv-ctx-menu-item-${_nextId++}`;
function isItemConfig(item) {
    return typeof item === 'object';
}
function buildItem(label, close, action, disabled) {
    const el = document.createElement('div');
    el.className = 'dv-context-menu-item';
    el.setAttribute('role', 'menuitem');
    if (disabled) {
        el.classList.add('dv-context-menu-item--disabled');
        el.setAttribute('aria-disabled', 'true');
    }
    el.textContent = label;
    if (!disabled) {
        el.addEventListener('click', () => {
            action();
            close();
        });
    }
    return el;
}
function buildSeparator() {
    const el = document.createElement('div');
    el.className = 'dv-context-menu-separator';
    el.setAttribute('role', 'separator');
    return el;
}
function isCoarsePrimaryInput() {
    if (globalThis.window === undefined || !globalThis.matchMedia) {
        return false;
    }
    const coarse = globalThis.matchMedia('(pointer: coarse)').matches;
    const fine = globalThis.matchMedia('(pointer: fine)').matches;
    return coarse && !fine;
}
function buildRenameInput(tabGroup) {
    const wrapper = document.createElement('div');
    wrapper.className = 'dv-context-menu-rename';
    const input = document.createElement('input');
    input.className = 'dv-context-menu-rename-input';
    input.type = 'text';
    input.placeholder = 'Name This Group';
    input.value = tabGroup.label;
    input.addEventListener('input', () => {
        tabGroup.setLabel(input.value);
    });
    input.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape' && e.key !== 'Enter') {
            e.stopPropagation();
        }
    });
    input.addEventListener('click', (e) => {
        e.stopPropagation();
    });
    wrapper.appendChild(input);
    // Skip auto-focus on touch-primary devices: focusing the input pops the
    // on-screen keyboard, which fires `window resize`, which `PopupService`
    // listens to and uses to dismiss the popover — so the menu opens, the
    // keyboard appears, and the menu immediately closes before the user can
    // type. The user can still tap the input to focus it intentionally.
    if (!isCoarsePrimaryInput()) {
        requestAnimationFrame(() => {
            input.focus();
            input.select();
        });
    }
    return wrapper;
}
function buildColorPicker(tabGroup, palette) {
    const wrapper = document.createElement('div');
    wrapper.className = 'dv-context-menu-color-picker';
    if (!palette.enabled) {
        // Opt-out: render no swatches. Returning a wrapper rather than null
        // keeps the call site simple; the wrapper is empty and visually inert.
        return wrapper;
    }
    for (const entry of palette.entries()) {
        const swatch = document.createElement('div');
        swatch.className = 'dv-context-menu-color-swatch';
        // Use a CSS custom property rather than setting `backgroundColor`
        // directly: the IDL setter validates the value against a color
        // grammar and rejects `var(...)` references in some environments
        // (notably jsdom; some browsers have historically had similar
        // quirks). The matching SCSS rule reads the var at use time.
        swatch.style.setProperty('--dv-tab-group-color', entry.value);
        if (entry.label) {
            swatch.title = entry.label;
        }
        if (tabGroup.color === entry.id) {
            swatch.classList.add('dv-context-menu-color-swatch--selected');
        }
        swatch.addEventListener('click', () => {
            tabGroup.setColor(entry.id);
        });
        wrapper.appendChild(swatch);
    }
    return wrapper;
}
class ContextMenuController {
    constructor(accessor) {
        this.accessor = accessor;
    }
    show(panel, group, event) {
        var _a, _b;
        if (!this.accessor.options.getTabContextMenuItems) {
            return;
        }
        const items = this.accessor.options.getTabContextMenuItems({
            panel,
            group,
            api: this.accessor.api,
            event,
        });
        if (items.length === 0) {
            return;
        }
        event.preventDefault();
        const popupService = this.accessor.getPopupServiceForGroup(group);
        const close = () => popupService.close();
        const menuEl = document.createElement('div');
        menuEl.className = 'dv-context-menu';
        menuEl.setAttribute('role', 'menu');
        for (const item of items) {
            if (item === 'separator') {
                menuEl.appendChild(buildSeparator());
            }
            else if (item === 'close') {
                menuEl.appendChild(buildItem('Close', close, () => panel.api.close()));
            }
            else if (item === 'closeOthers') {
                menuEl.appendChild(buildItem('Close Others', close, () => {
                    group.panels
                        .filter((p) => p !== panel)
                        .forEach((p) => p.api.close());
                }));
            }
            else if (item === 'closeAll') {
                menuEl.appendChild(buildItem('Close All', close, () => {
                    [...group.panels].forEach((p) => p.api.close());
                }));
            }
            else if (isItemConfig(item) && item.element) {
                menuEl.appendChild(item.element);
            }
            else if (isItemConfig(item) && item.component) {
                const renderer = (_b = (_a = this.accessor.options).createContextMenuItemComponent) === null || _b === void 0 ? void 0 : _b.call(_a, {
                    id: nextContextMenuItemId(),
                    component: item.component,
                });
                if (renderer) {
                    renderer.init({
                        panel,
                        group,
                        api: this.accessor.api,
                        close,
                        componentProps: item.componentProps,
                    });
                    menuEl.appendChild(renderer.element);
                }
            }
            else if (isItemConfig(item) && item.label) {
                menuEl.appendChild(buildItem(item.label, close, () => { var _a; return (_a = item.action) === null || _a === void 0 ? void 0 : _a.call(item); }, item.disabled));
            }
        }
        popupService.openPopover(menuEl, {
            x: event.clientX,
            y: event.clientY,
            zIndex: popoverZIndexFor(event.target),
        });
    }
    showForChip(tabGroup, group, event) {
        if (!this.accessor.options.getTabGroupChipContextMenuItems) {
            return;
        }
        const items = this.accessor.options.getTabGroupChipContextMenuItems({
            tabGroup,
            group,
            api: this.accessor.api,
            event,
        });
        if (items.length === 0) {
            return;
        }
        event.preventDefault();
        const popupService = this.accessor.getPopupServiceForGroup(group);
        const close = () => popupService.close();
        const menuEl = document.createElement('div');
        menuEl.className = 'dv-context-menu';
        menuEl.setAttribute('role', 'menu');
        for (const item of items) {
            if (item === 'separator') {
                menuEl.appendChild(buildSeparator());
            }
            else if (item === 'rename') {
                menuEl.appendChild(buildRenameInput(tabGroup));
            }
            else if (item === 'colorPicker') {
                menuEl.appendChild(buildColorPicker(tabGroup, this.accessor.tabGroupColorPalette));
            }
            else if (isItemConfig(item) && item.element) {
                menuEl.appendChild(item.element);
            }
            else if (isItemConfig(item) && item.label) {
                menuEl.appendChild(buildItem(item.label, close, () => { var _a; return (_a = item.action) === null || _a === void 0 ? void 0 : _a.call(item); }, item.disabled));
            }
        }
        popupService.openPopover(menuEl, {
            x: event.clientX,
            y: event.clientY,
            zIndex: popoverZIndexFor(event.target),
        });
    }
}
const ContextMenuModule = defineModule({
    name: 'ContextMenu',
    serviceKey: 'contextMenuService',
    create: (host) => new ContextMenuController(host),
});

/** Cursor offset of the group drag ghost, matched to the long-shipped default. */
const GROUP_DRAG_GHOST_OFFSET_X = 30;
const GROUP_DRAG_GHOST_OFFSET_Y = -10;
/**
 * The narrow surface the {@link AdvancedDnDService} needs from the host
 * (the `DockviewComponent`).
 *
 * The `onWill*` emitters stay on the component so the public event shape is
 * unchanged whether or not this module is registered — the service is only the
 * dispatch point those fires are routed through. Engine policy (e.g. the
 * `disableDnd` guard) stays on the component, ahead of the dispatch.
 */
/**
 * Owns the dispatch of the advanced drag-and-drop hooks — `onWillDragPanel`,
 * `onWillDragGroup`, `onWillDrop` and `onWillShowOverlay`.
 *
 * At this stage the service is a thin dispatcher that forwards to the host's
 * emitters; the customisation surface it gates (custom drop overlays, custom
 * group drag ghosts, hook veto/transform behaviour) is currently inlined in
 * core and is extracted into this service in later phases. Routing the
 * dispatch through a module slot is what lets that customisation layer move
 * into a separately-distributed package in a future major version without
 * changing the public event surface.
 *
 * The service holds no drag state of its own — the gesture is driven by the
 * DnD backends, and the per-group subscriptions live on the component's group
 * lifecycle (so groups created mid-move are not missed).
 */
class AdvancedDnDService {
    constructor(host) {
        this.host = host;
    }
    dispatchWillDragPanel(event) {
        this.host.fireWillDragPanel(event);
    }
    dispatchWillDragGroup(event) {
        this.host.fireWillDragGroup(event);
    }
    dispatchWillDrop(event) {
        this.host.fireWillDrop(event);
    }
    dispatchWillShowOverlay(event) {
        this.host.fireWillShowOverlay(event);
    }
    buildGroupDragGhost(group) {
        const createGhost = this.host.options.createGroupDragGhostComponent;
        if (!createGhost) {
            return undefined;
        }
        const renderer = createGhost(group);
        renderer.init({ group, api: this.host.api });
        return {
            element: renderer.element,
            offsetX: GROUP_DRAG_GHOST_OFFSET_X,
            offsetY: GROUP_DRAG_GHOST_OFFSET_Y,
            dispose: renderer.dispose ? () => { var _a; return (_a = renderer.dispose) === null || _a === void 0 ? void 0 : _a.call(renderer); } : undefined,
        };
    }
    resolveOverlayModel(location, group) {
        var _a, _b;
        return (_b = (_a = this.host.options).dropOverlayModel) === null || _b === void 0 ? void 0 : _b.call(_a, { location, group });
    }
    showPreviewOverlay(group, position) {
        const target = group.model.contentDropTarget;
        target.showOverlay(position);
        return DockviewDisposable.from(() => target.clearOverlay());
    }
    dispose() {
        // no-op — see class doc: the service holds no state to tear down.
    }
}
const AdvancedDnDModule = defineModule({
    name: 'AdvancedDnD',
    serviceKey: 'advancedDnDService',
    create: (host) => new AdvancedDnDService(host),
});

/**
 * Marks (on the dockview root element) that a keyboard move is in progress, so
 * the default navigation listener stands down while the advanced docking module
 * drives the keys. A neutral DOM signal keeps the two listeners coordinated
 * without either service holding a reference to the other.
 */
const KEYBOARD_MOVE_ATTRIBUTE = 'data-dv-kbd-moving';
/**
 * Does `e` match a binding string like `'ctrl+]'` / `'shift+f6'`? Modifiers are
 * matched exactly (a binding without `shift` will not fire while Shift is held),
 * and the final part is compared to `KeyboardEvent.key`, lower-cased.
 */
function matchesBinding(e, binding) {
    const parts = binding.toLowerCase().split('+');
    const key = parts[parts.length - 1];
    const mods = parts.slice(0, -1);
    return (e.ctrlKey === mods.includes('ctrl') &&
        e.shiftKey === mods.includes('shift') &&
        e.altKey === mods.includes('alt') &&
        e.metaKey === (mods.includes('meta') || mods.includes('cmd')) &&
        e.key.toLowerCase() === key);
}
/**
 * Resolve the `keyboardNavigation` opt-in to its options object, or `undefined`
 * when keyboard support is off. Both keyboard modules read the same opt-in.
 */
function readKeyboardNavigation(options) {
    const opt = options.keyboardNavigation;
    if (!opt) {
        return undefined;
    }
    return opt === true ? {} : opt;
}

const DEFAULT_KEYMAP$1 = {
    nextTab: 'ctrl+]',
    prevTab: 'ctrl+[',
    focusNextGroup: 'f6',
    focusPrevGroup: 'shift+f6',
    focusTabs: 'ctrl+shift+\\',
};
/**
 * Keyboard navigation & focus management — operate the dock without a mouse.
 * Opt-in via `keyboardNavigation`, with a rebindable {@link DockviewKeybindings}
 * keymap.
 *
 * - **Switch tab** (`Ctrl+]` / `Ctrl+[`) — cycle the focused group's tabs.
 * - **Focus group** (`F6` / `Shift+F6`) — move focus to the next / previous
 *   group in sequence.
 * - **Focus the tab strip** (`Ctrl+Shift+\`) — jump focus from panel content to
 *   the active group's tab strip (the strip's roving-tabindex takes over).
 * - **Focus restore on close** — when removing a panel/group pulls focus out of
 *   the dock, focus returns to the neighbour the layout just activated instead
 *   of being stranded on `<body>`.
 * - **Floating `Esc`** — `Esc` inside a floating group returns focus to the
 *   control that had it before entering the float (polite: bubble phase,
 *   respects `defaultPrevented`, so panel content keeps `Esc`).
 * - **Floating Tab-containment** — Tab wraps within the floating group so focus
 *   doesn't leak to the grid behind it.
 *
 * Stands down while an advanced keyboard move is in progress (see
 * {@link KEYBOARD_MOVE_ATTRIBUTE}) so the docking module owns the keys then.
 */
class AccessibilityService extends DockviewCompositeDisposable {
    constructor(host) {
        super();
        this.host = host;
        this._focusWasInside = false;
        // Listen on the document (capture) rather than the dockview element:
        // edge groups live in the shell *outside* the gridview, and the shell
        // is created after this service, so a fixed element would miss them.
        const doc = host.rootElement.ownerDocument;
        const onKeyDown = (e) => this._onKeyDown(e);
        doc.addEventListener('keydown', onKeyDown, true);
        // Remember the last control focused in the main dock (outside any
        // float) so Esc inside a floating group can return focus to its
        // invoking control. Observe-only — never consumes.
        const onFocusIn = (e) => {
            const t = e.target;
            if (t instanceof HTMLElement &&
                this.host.rootElement.contains(t) &&
                !t.closest('[role="dialog"]')) {
                this._lastNonFloatFocus = t;
            }
        };
        doc.addEventListener('focusin', onFocusIn, true);
        // Esc-from-float restore runs in the BUBBLE phase and respects
        // defaultPrevented, so panel content that uses Esc keeps priority.
        const onEscape = (e) => this._onFloatingEscape(e);
        doc.addEventListener('keydown', onEscape, false);
        this.addDisposables({
            dispose: () => doc.removeEventListener('keydown', onKeyDown, true),
        }, {
            dispose: () => doc.removeEventListener('focusin', onFocusIn, true),
        }, {
            dispose: () => doc.removeEventListener('keydown', onEscape, false),
        }, 
        // When a close pulls focus out of the dock, return it to the
        // neighbour the component just activated rather than leaving it
        // stranded on <body>. Snapshot before the teardown (focus still on
        // the closing panel), restore after.
        host.onWillMutateLayout((e) => {
            if (e.kind === 'remove' && this._nav) {
                this._focusWasInside = this._isFocusInside();
            }
        }), host.onDidMutateLayout((e) => {
            if (e.kind === 'remove' &&
                this._nav &&
                this._focusWasInside &&
                !this._isFocusInside()) {
                this._restoreFocus();
            }
        }));
    }
    get _moveActive() {
        return this.host.rootElement.hasAttribute(KEYBOARD_MOVE_ATTRIBUTE);
    }
    _isFocusInside() {
        const active = this.host.rootElement.ownerDocument.activeElement;
        return active instanceof Node && this.host.rootElement.contains(active);
    }
    _onFloatingEscape(e) {
        if (!this._nav ||
            this._moveActive ||
            e.defaultPrevented ||
            e.key !== 'Escape') {
            return;
        }
        const target = e.target;
        if (!(target instanceof Element)) {
            return;
        }
        // Only when focus is inside one of *this* dock's floating groups.
        const float = target.closest('[role="dialog"]');
        if (!float || !this.host.rootElement.contains(float)) {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        this._returnFocusFromFloat();
    }
    /**
     * Keep Tab inside the floating group that holds focus: at the last tabbable
     * Tab wraps to the first, at the first Shift+Tab wraps to the last. Returns
     * true if it handled the event. No-op outside a float.
     */
    _trapFloatTab(e) {
        const target = e.target;
        if (!(target instanceof Element)) {
            return false;
        }
        const float = target.closest('[role="dialog"]');
        if (!float || !this.host.rootElement.contains(float)) {
            return false;
        }
        // Always manage Tab inside a float, never just at the boundary: focus
        // often sits on non-tabbable plumbing (the content container, which is
        // tabindex="-1"), and the browser's default Tab from there escapes to
        // the grid behind. Drive the cursor through the float's tabbables
        // ourselves and swallow the default so it can't leak out.
        e.preventDefault();
        const tabbables = this._tabbables(float);
        if (tabbables.length === 0) {
            return true;
        }
        const active = float.ownerDocument.activeElement;
        const index = active instanceof HTMLElement ? tabbables.indexOf(active) : -1;
        const n = tabbables.length;
        const next = index === -1
            ? e.shiftKey
                ? n - 1
                : 0
            : (index + (e.shiftKey ? -1 : 1) + n) % n;
        tabbables[next].focus();
        return true;
    }
    _tabbables(root) {
        const nodes = root.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]), ' +
            'select:not([disabled]), textarea:not([disabled]), [tabindex]');
        // tabIndex >= 0 keeps naturally-focusable controls and roving anchors
        // (the active tab) while dropping tabindex="-1" plumbing (content
        // containers, inactive tabs).
        return Array.from(nodes).filter((el) => el.tabIndex >= 0);
    }
    _returnFocusFromFloat() {
        var _a;
        const prev = this._lastNonFloatFocus;
        if (prev &&
            prev.isConnected &&
            this.host.rootElement.contains(prev) &&
            !prev.closest('[role="dialog"]')) {
            prev.focus();
            return;
        }
        // Invoking control is gone — fall back to a grid group's content.
        (_a = this.host.groups
            .find((g) => g.api.location.type === 'grid')) === null || _a === void 0 ? void 0 : _a.model.focusContent();
    }
    get _nav() {
        return readKeyboardNavigation(this.host.options);
    }
    get _keymap() {
        var _a;
        return Object.assign(Object.assign({}, DEFAULT_KEYMAP$1), (_a = this._nav) === null || _a === void 0 ? void 0 : _a.keymap);
    }
    _onKeyDown(e) {
        if (!this._nav) {
            return;
        }
        // Stand down while the docking module is driving a keyboard move.
        if (this._moveActive) {
            return;
        }
        // Only act on events originating inside *this* dockview.
        if (!(e.target instanceof Node) ||
            !this.host.rootElement.contains(e.target)) {
            return;
        }
        // Trap Tab within a floating group so focus doesn't leak to the grid
        // behind it (the float is non-modal, but its Tab order should be).
        if (e.key === 'Tab' && this._trapFloatTab(e)) {
            return;
        }
        const keymap = this._keymap;
        if (matchesBinding(e, keymap.nextTab)) {
            this._consume(e);
            this._switchTab(false);
        }
        else if (matchesBinding(e, keymap.prevTab)) {
            this._consume(e);
            this._switchTab(true);
        }
        else if (matchesBinding(e, keymap.focusNextGroup)) {
            this._consume(e);
            this._cycleGroup(false);
        }
        else if (matchesBinding(e, keymap.focusPrevGroup)) {
            this._consume(e);
            this._cycleGroup(true);
        }
        else if (matchesBinding(e, keymap.focusTabs)) {
            this._consume(e);
            this._focusTabs();
        }
    }
    // --- navigation (uses the public group API + the adjacentGroup primitive) ---
    _focusTabs() {
        var _a;
        // Jump from panel content to the active group's tab strip; the
        // tablist's own roving-tabindex handler takes over from there.
        (_a = this.host.activeGroup) === null || _a === void 0 ? void 0 : _a.model.focusActiveTab();
    }
    _switchTab(reverse) {
        const group = this.host.activeGroup;
        if (!group) {
            return;
        }
        if (reverse) {
            group.model.moveToPrevious();
        }
        else {
            group.model.moveToNext();
        }
        // Keep DOM focus inside the dock: switching hides the previously
        // focused content, which would otherwise drop focus to <body> and
        // leave the keymap unable to see the next key.
        group.model.focusContent();
    }
    _cycleGroup(reverse) {
        const current = this.host.activeGroup;
        const target = current
            ? this.host.adjacentGroup(current, reverse)
            : this.host.groups[0];
        this._focusGroup(target);
    }
    _focusGroup(target) {
        if (!target) {
            return;
        }
        target.api.setActive();
        target.model.focusContent();
    }
    /** Return DOM focus to the active group's content, keeping it in the dock. */
    _restoreFocus() {
        var _a;
        (_a = this.host.activeGroup) === null || _a === void 0 ? void 0 : _a.model.focusContent();
    }
    _consume(e) {
        e.preventDefault();
        e.stopPropagation();
    }
}
const AccessibilityModule = defineModule({
    name: 'Accessibility',
    serviceKey: 'accessibilityService',
    create: (host) => new AccessibilityService(host),
});

const EDGE_FROM_KEY = {
    ArrowLeft: 'left',
    ArrowRight: 'right',
    ArrowUp: 'top',
    ArrowDown: 'bottom',
};
const DEFAULT_KEYMAP = {
    focusGroupLeft: 'ctrl+shift+arrowleft',
    focusGroupRight: 'ctrl+shift+arrowright',
    focusGroupUp: 'ctrl+shift+arrowup',
    focusGroupDown: 'ctrl+shift+arrowdown',
    dock: 'ctrl+m',
};
/**
 * Advanced keyboard control — drive the layout itself without a mouse. Opt-in
 * via `keyboardNavigation` (shared with the default navigation module).
 *
 * - **Spatial group focus** (`Ctrl+Shift+Arrows`) — move focus to the visually
 *   adjacent group via the `adjacentGroupInDirection` geometry primitive.
 * - **Keyboard docking** (`Ctrl+M`) — arms a two-phase move of the active panel
 *   with a live drop preview + screen-reader narration:
 *     1. PICK TARGET — arrows cycle the groups (incl. the panel's own, so a tab
 *        can be split out); `Enter` selects one.
 *     2. PICK EDGE — arrows choose a split edge (left/right/top/bottom) or the
 *        centre (tab-into); `Enter` commits, `Escape` steps back.
 *   `Escape` from the target phase cancels.
 *
 * While a move is in progress it marks the root with
 * {@link KEYBOARD_MOVE_ATTRIBUTE} so the default navigation listener stands
 * down and the move keys aren't double-handled.
 */
class KeyboardDockingService extends DockviewCompositeDisposable {
    constructor(host) {
        super();
        this.host = host;
        this._move = null;
        // Capture phase, on the document — matches the navigation listener so
        // edge groups (outside the gridview) are covered.
        const doc = host.rootElement.ownerDocument;
        const onKeyDown = (e) => this._onKeyDown(e);
        doc.addEventListener('keydown', onKeyDown, true);
        this.addDisposables({ dispose: () => this._exit() }, {
            dispose: () => doc.removeEventListener('keydown', onKeyDown, true),
        });
    }
    get _enabled() {
        return !!readKeyboardNavigation(this.host.options);
    }
    get _keymap() {
        var _a, _b, _c, _d, _e, _f, _g;
        // The public `keyboardNavigation.keymap` carries the default-navigation
        // bindings; this module's bindings are read from the same object when
        // present (so a consumer can rebind them too).
        const overrides = ((_b = (_a = readKeyboardNavigation(this.host.options)) === null || _a === void 0 ? void 0 : _a.keymap) !== null && _b !== void 0 ? _b : {});
        return {
            focusGroupLeft: (_c = overrides.focusGroupLeft) !== null && _c !== void 0 ? _c : DEFAULT_KEYMAP.focusGroupLeft,
            focusGroupRight: (_d = overrides.focusGroupRight) !== null && _d !== void 0 ? _d : DEFAULT_KEYMAP.focusGroupRight,
            focusGroupUp: (_e = overrides.focusGroupUp) !== null && _e !== void 0 ? _e : DEFAULT_KEYMAP.focusGroupUp,
            focusGroupDown: (_f = overrides.focusGroupDown) !== null && _f !== void 0 ? _f : DEFAULT_KEYMAP.focusGroupDown,
            dock: (_g = overrides.dock) !== null && _g !== void 0 ? _g : DEFAULT_KEYMAP.dock,
        };
    }
    get _messages() {
        return resolveMessages(this.host.options.messages);
    }
    _onKeyDown(e) {
        if (!this._enabled) {
            return;
        }
        if (this._move) {
            this._onMoveKey(e);
            return;
        }
        // Only act on events originating inside *this* dockview.
        if (!(e.target instanceof Node) ||
            !this.host.rootElement.contains(e.target)) {
            return;
        }
        const keymap = this._keymap;
        if (matchesBinding(e, keymap.dock)) {
            this._enterMoveMode(e);
        }
        else if (matchesBinding(e, keymap.focusGroupLeft)) {
            this._consume(e);
            this._focusGroupInDirection('left');
        }
        else if (matchesBinding(e, keymap.focusGroupRight)) {
            this._consume(e);
            this._focusGroupInDirection('right');
        }
        else if (matchesBinding(e, keymap.focusGroupUp)) {
            this._consume(e);
            this._focusGroupInDirection('up');
        }
        else if (matchesBinding(e, keymap.focusGroupDown)) {
            this._consume(e);
            this._focusGroupInDirection('down');
        }
    }
    // --- spatial focus ---
    _focusGroupInDirection(direction) {
        const current = this.host.activeGroup;
        if (!current) {
            return;
        }
        // Geometry lives on the host as the shared `adjacentGroupInDirection`
        // primitive (also public on the api), so mouse and keyboard navigation
        // agree on what "the group to the left" is.
        this._focusGroup(this.host.adjacentGroupInDirection(current, direction));
    }
    _focusGroup(target) {
        if (!target) {
            return;
        }
        target.api.setActive();
        target.model.focusContent();
    }
    _restoreFocus() {
        var _a;
        (_a = this.host.activeGroup) === null || _a === void 0 ? void 0 : _a.model.focusContent();
    }
    // --- keyboard docking (move mode) ---
    _enterMoveMode(e) {
        const source = this.host.activePanel;
        const groups = this.host.groups;
        if (!source || groups.length === 0) {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        // Start on the panel's own group so a single group of tabs can still
        // split a tab out via the edge phase.
        const groupIndex = Math.max(0, groups.indexOf(source.group));
        this._move = {
            source,
            groups,
            groupIndex,
            phase: 'target',
            position: 'center',
        };
        // Signal the navigation module to stand down while the move runs.
        this.host.rootElement.setAttribute(KEYBOARD_MOVE_ATTRIBUTE, '');
        this._render();
    }
    _onMoveKey(e) {
        const move = this._move;
        if (e.key === 'Escape') {
            this._consume(e);
            if (move.phase === 'edge') {
                move.phase = 'target';
                move.position = 'center';
                this._render();
            }
            else {
                this._exit();
                this.host.announce(this._messages.moveCancelled());
                this._restoreFocus();
            }
            return;
        }
        if (e.key === 'Enter') {
            this._consume(e);
            if (move.phase === 'target') {
                move.phase = 'edge';
                move.position = 'center';
                this._render();
            }
            else {
                this._commit();
            }
            return;
        }
        if (move.phase === 'target') {
            const n = move.groups.length;
            if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
                this._consume(e);
                move.groupIndex = (move.groupIndex + 1) % n;
                this._render();
            }
            else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
                this._consume(e);
                move.groupIndex = (move.groupIndex - 1 + n) % n;
                this._render();
            }
            return;
        }
        // edge phase
        const edge = EDGE_FROM_KEY[e.key];
        if (edge) {
            this._consume(e);
            move.position = edge;
            this._render();
        }
        else if (e.key === ' ' || e.key === 'c' || e.key === 'C') {
            this._consume(e);
            move.position = 'center';
            this._render();
        }
    }
    _render() {
        var _a, _b;
        const move = this._move;
        const group = move.groups[move.groupIndex];
        this._clearPreview();
        this._preview = this.host.showDropPreview(group, move.position);
        const name = (_b = (_a = group.activePanel) === null || _a === void 0 ? void 0 : _a.title) !== null && _b !== void 0 ? _b : group.id;
        const m = this._messages;
        if (move.phase === 'target') {
            this.host.announce(m.movePickTarget(this._label(move.source), name, move.groupIndex + 1, move.groups.length));
        }
        else {
            this.host.announce(m.movePickEdge(move.position, name));
        }
    }
    _commit() {
        var _a, _b;
        const move = this._move;
        const group = move.groups[move.groupIndex];
        const position = move.position;
        const source = move.source;
        const name = (_b = (_a = group.activePanel) === null || _a === void 0 ? void 0 : _a.title) !== null && _b !== void 0 ? _b : group.id;
        const m = this._messages;
        this._exit();
        try {
            this.host.dockPanel(source, group, position);
            this.host.announce(m.moveCommitted(this._label(source), name, position));
        }
        catch (_c) {
            this.host.announce(m.moveNotAllowed());
        }
        // The move re-renders the grid; pull focus back into the dock so the
        // keymap keeps working without a click.
        this._restoreFocus();
    }
    _exit() {
        this._clearPreview();
        this._move = null;
        this.host.rootElement.removeAttribute(KEYBOARD_MOVE_ATTRIBUTE);
    }
    _clearPreview() {
        var _a;
        (_a = this._preview) === null || _a === void 0 ? void 0 : _a.dispose();
        this._preview = undefined;
    }
    _consume(e) {
        e.preventDefault();
        e.stopPropagation();
    }
    _label(panel) {
        var _a;
        return (_a = panel.title) !== null && _a !== void 0 ? _a : panel.id;
    }
}
defineModule({
    name: 'KeyboardDocking',
    serviceKey: 'keyboardDockingService',
    create: (host) => new KeyboardDockingService(host),
    dependsOn: [AdvancedDnDModule, LiveRegionModule],
});

/**
 * The set of modules provided by this package. `dockview` registers these via
 * `registerModules(Modules)` so they are available to every component.
 */
const Modules = [
    TabGroupChipsModule,
    ContextMenuModule,
    AdvancedDnDModule,
    AccessibilityModule,
];

/**
 * `dockview` is the batteries-included entry point: it re-exports the core API
 * and registers the separable feature modules so every component gets the full
 * feature set out of the box. Consumers who want only the core can depend on
 * `dockview-core` directly.
 */
registerModules(Modules);
// Mark the public package as loaded so `dockview-core` doesn't warn about
// direct usage. Purely drives that developer warning — no functional effect.
markDockviewPackageLoaded();
