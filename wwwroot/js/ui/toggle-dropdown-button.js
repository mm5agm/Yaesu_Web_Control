/**
 * Yaesu key: left-click toggles, cycles, or opens the menu; right-click opens options.
 */
export class ToggleDropdownButton {
    /**
     * @param {HTMLElement} root
     * @param {{
     *   label?: string,
     *   options?: { id: string, label: string }[],
     *   enabled?: boolean,
     *   selectedId?: string,
     *   offId?: string | null,
     *   clickAction?: "toggle" | "openMenu" | "cycle",
     *   contextMenu?: boolean,
     *   showLed?: boolean,
     *   menuClass?: string,
     *   a11yKey?: string,
     *   onChange?: (state: { enabled: boolean, selectedId: string }) => void
     * }} [config]
     */
    constructor(root, config = {}) {
        this.root = root;
        this.label = config.label ?? "Function";
        this.options = config.options ?? [
            { id: "a", label: "Option A" },
            { id: "b", label: "Option B" },
            { id: "c", label: "Option C" },
        ];
        this.offId = config.offId != null ? String(config.offId) : null;
        this.clickAction =
            config.clickAction === "openMenu" || config.clickAction === "cycle"
                ? config.clickAction
                : "toggle";
        this.contextMenuEnabled = config.contextMenu !== false;
        // Cycle keys have no on/off semantics — hide the LED unless asked.
        this.showLed =
            config.showLed !== undefined
                ? Boolean(config.showLed)
                : this.clickAction !== "cycle";
        this.selectedId = config.selectedId ?? this.options[0].id;
        this.enabled =
            config.enabled !== undefined
                ? Boolean(config.enabled)
                : this.offId != null
                  ? String(this.selectedId) !== this.offId
                  : false;
        this._lastOnId =
            this.offId != null && String(this.selectedId) !== this.offId
                ? String(this.selectedId)
                : this.options.find((o) => String(o.id) !== this.offId)?.id ?? this.selectedId;
        this.onChange = config.onChange ?? (() => {});
        this.menuClass = config.menuClass ?? "";
        this.a11yKey = config.a11yKey ?? null;
        this.menuOpen = false;
        this._ignoreNextClick = false;
        this.disabled = false;
        this._ariaOverride = null;

        this._onDocPointer = this._onDocPointer.bind(this);
        this._onKeydown = this._onKeydown.bind(this);

        this._render();
        this._bind();
        this._sync();
    }

    getState() {
        return { enabled: this.enabled, selectedId: this.selectedId, disabled: Boolean(this.disabled) };
    }

    /**
     * Replace the option list (e.g. band segments that change with the band).
     * @param {{ id: string, label: string }[]} options
     * @param {{ silent?: boolean, selectedId?: string }} [opts]
     */
    setOptions(options, opts = {}) {
        this.options = (options ?? []).map((o) => ({
            id: String(o.id),
            label: String(o.label),
        }));
        if (this.menu) {
            this.menu.innerHTML = this.options
                .map(
                    (opt) => `
                <li role="none">
                    <button type="button"
                            class="dropdown-item toggle-dd__item"
                            role="menuitemradio"
                            data-option-id="${opt.id}"
                            aria-checked="false">
                        ${opt.label}
                    </button>
                </li>`
                )
                .join("");
        }
        if (opts.selectedId !== undefined) {
            this.selectedId = String(opts.selectedId);
        } else if (!this.options.some((o) => String(o.id) === String(this.selectedId))) {
            this.selectedId = this.options[0] ? String(this.options[0].id) : "";
        }
        if (this.offId != null) {
            this.enabled = this.selectedId !== this.offId;
            if (this.enabled) this._lastOnId = this.selectedId;
        }
        this._sync();
        if (!opts.silent) this.onChange(this.getState());
    }

    /**
     * @param {boolean} disabled
     */
    setDisabled(disabled) {
        this.disabled = Boolean(disabled);
        if (this.button) this.button.disabled = this.disabled;
        if (this.disabled) this._setMenuOpen(false);
    }

    /**
     * @param {{ enabled?: boolean, selectedId?: string }} partial
     * @param {{ silent?: boolean }} [opts]
     */
    setState(partial = {}, opts = {}) {
        if (partial.selectedId !== undefined) {
            this.selectedId = String(partial.selectedId);
            if (this.offId != null) {
                this.enabled = this.selectedId !== this.offId;
                if (this.enabled) this._lastOnId = this.selectedId;
            }
        }
        if (partial.enabled !== undefined) {
            this.enabled = Boolean(partial.enabled);
            if (this.offId != null) {
                if (this.enabled) {
                    if (this.selectedId === this.offId) {
                        this.selectedId = this._lastOnId;
                    } else {
                        this._lastOnId = this.selectedId;
                    }
                } else {
                    if (this.selectedId !== this.offId) this._lastOnId = this.selectedId;
                    this.selectedId = this.offId;
                }
            }
        }
        this._sync();
        if (!opts.silent) this.onChange(this.getState());
    }

    _selectedLabel() {
        return (
            this.options.find((o) => String(o.id) === String(this.selectedId))?.label ??
            this.selectedId
        );
    }

    _render() {
        this.root.classList.add("toggle-dd", "dropdown");
        if (!this.showLed) this.root.classList.add("toggle-dd--no-led");

        const title =
            this.clickAction === "openMenu"
                ? "Click for options"
                : this.clickAction === "cycle"
                  ? this.contextMenuEnabled
                      ? "Left-click to cycle; right-click for options"
                      : "Click to cycle"
                  : this.contextMenuEnabled
                    ? "Right-click for options"
                    : this.label;
        const hintHtml =
            this.clickAction === "openMenu" || this.contextMenuEnabled
                ? `<span class="toggle-dd__menu-hint" aria-hidden="true"></span>`
                : "";
        const ledHtml = this.showLed
            ? `<span class="toggle-dd__led" aria-hidden="true"></span>`
            : "";
        const a11yAttr = this.a11yKey ? ` data-a11y-key="${this.a11yKey}"` : "";

        this.root.innerHTML = `
            <button type="button"
                    class="btn toggle-dd__btn toggle-dd__btn--menu"
                    title="${title}"
                    aria-haspopup="menu"
                    aria-expanded="false"
                    aria-pressed="false"${a11yAttr}>
                ${ledHtml}
                <span class="toggle-dd__label"></span>
                ${hintHtml}
            </button>
            <ul class="dropdown-menu toggle-dd__menu ${this.menuClass}" role="menu"></ul>
        `;

        this.button = this.root.querySelector(".toggle-dd__btn");
        this.led = this.root.querySelector(".toggle-dd__led");
        this.labelEl = this.root.querySelector(".toggle-dd__label");
        this.menu = this.root.querySelector(".toggle-dd__menu");

        this.menu.innerHTML = this.options
            .map(
                (opt) => `
                <li role="none">
                    <button type="button"
                            class="dropdown-item toggle-dd__item"
                            role="menuitemradio"
                            data-option-id="${opt.id}"
                            aria-checked="false">
                        ${opt.label}
                    </button>
                </li>`
            )
            .join("");
    }

    _bind() {
        this.button.addEventListener("click", (event) => {
            event.preventDefault();
            if (this.disabled) return;
            if (this._ignoreNextClick) {
                this._ignoreNextClick = false;
                return;
            }
            if (this.clickAction === "openMenu") {
                this._setMenuOpen(!this.menuOpen);
                return;
            }
            this._setMenuOpen(false);
            if (this.clickAction === "cycle") {
                const idx = this.options.findIndex((o) => String(o.id) === String(this.selectedId));
                const next = (idx < 0 ? 0 : idx + 1) % this.options.length;
                this.selectedId = String(this.options[next].id);
                if (this.offId != null) {
                    this.enabled = this.selectedId !== this.offId;
                    if (this.enabled) this._lastOnId = this.selectedId;
                }
            } else if (this.offId != null) {
                if (this.selectedId === this.offId) {
                    this.selectedId = this._lastOnId;
                    this.enabled = true;
                } else {
                    this._lastOnId = this.selectedId;
                    this.selectedId = this.offId;
                    this.enabled = false;
                }
            } else {
                this.enabled = !this.enabled;
            }
            this._sync();
            this.onChange(this.getState());
        });

        this.button.addEventListener("contextmenu", (event) => {
            event.preventDefault();
            if (this.disabled || !this.contextMenuEnabled) return;
            this._ignoreNextClick = true;
            this._setMenuOpen(!this.menuOpen);
            window.setTimeout(() => {
                this._ignoreNextClick = false;
            }, 50);
        });

        this.menu.addEventListener("click", (event) => {
            const item = event.target.closest("[data-option-id]");
            if (!item) {
                return;
            }
            event.preventDefault();
            this.selectedId = item.getAttribute("data-option-id");
            if (this.offId != null) {
                this.enabled = this.selectedId !== this.offId;
                if (this.enabled) this._lastOnId = this.selectedId;
            }
            this._setMenuOpen(false);
            this._sync();
            this.onChange(this.getState());
        });
    }

    _setMenuOpen(open) {
        this.menuOpen = open;
        this.menu.classList.toggle("show", open);
        this.button.setAttribute("aria-expanded", open ? "true" : "false");

        if (open) {
            document.addEventListener("pointerdown", this._onDocPointer, true);
            document.addEventListener("keydown", this._onKeydown);
        } else {
            document.removeEventListener("pointerdown", this._onDocPointer, true);
            document.removeEventListener("keydown", this._onKeydown);
        }
    }

    _onDocPointer(event) {
        if (!this.root.contains(event.target)) {
            this._setMenuOpen(false);
        }
    }

    _onKeydown(event) {
        if (event.key === "Escape") {
            this._setMenuOpen(false);
            this.button.focus();
        }
    }

    _sync() {
        const selectedLabel = this._selectedLabel();
        this.labelEl.textContent = `${this.label} · ${selectedLabel}`;
        if (this._ariaOverride) {
            this.button.setAttribute("aria-label", this._ariaOverride);
            this.button.setAttribute("title", this._ariaOverride);
        } else {
            this.button.setAttribute("aria-label", `${this.label}: ${selectedLabel}`);
        }
        if (this.showLed) {
            this.button.setAttribute("aria-pressed", this.enabled ? "true" : "false");
            if (this.led) this.led.classList.toggle("is-on", this.enabled);
        } else {
            this.button.removeAttribute("aria-pressed");
        }

        for (const item of this.menu.querySelectorAll("[data-option-id]")) {
            const isSelected = item.getAttribute("data-option-id") === String(this.selectedId);
            item.classList.toggle("active", isSelected);
            item.setAttribute("aria-checked", isSelected ? "true" : "false");
        }
    }
}

/**
 * Same Yaesu key: left-click toggles; right-click opens a slider.
 */
export class ToggleSliderButton {
    /**
     * @param {HTMLElement} root
     * @param {{
     *   label?: string,
     *   min?: number,
     *   max?: number,
     *   step?: number,
     *   value?: number,
     *   valueSuffix?: string,
     *   enabled?: boolean,
     *   a11yKey?: string,
     *   onChange?: (state: { enabled: boolean, value: number }) => void
     * }} [config]
     */
    constructor(root, config = {}) {
        this.root = root;
        this.label = config.label ?? "Level";
        this.min = Number(config.min ?? 1);
        this.max = Number(config.max ?? 15);
        this.step = Number(config.step ?? 1);
        this.valueSuffix = config.valueSuffix ?? "";
        this.value = Number(config.value ?? this.min);
        this.enabled = Boolean(config.enabled);
        this.a11yKey = config.a11yKey ?? null;
        this.onChange = config.onChange ?? (() => {});
        this.menuOpen = false;
        this._ignoreNextClick = false;

        this._onDocPointer = this._onDocPointer.bind(this);
        this._onKeydown = this._onKeydown.bind(this);

        this._render();
        this._bind();
        this._sync();
    }

    getState() {
        return { enabled: this.enabled, value: this.value };
    }

    /**
     * @param {{ enabled?: boolean, value?: number, min?: number, max?: number }} partial
     * @param {{ silent?: boolean }} [opts]
     */
    setState(partial = {}, opts = {}) {
        if (partial.min !== undefined) {
            const n = Number(partial.min);
            if (!Number.isNaN(n)) this.min = n;
        }
        if (partial.max !== undefined) {
            const n = Number(partial.max);
            if (!Number.isNaN(n)) this.max = n;
        }
        if (partial.enabled !== undefined) this.enabled = Boolean(partial.enabled);
        if (partial.value !== undefined) {
            const n = Number(partial.value);
            if (!Number.isNaN(n)) this.value = n;
        }
        // Always clamp to the current range (bounds may have narrowed).
        this.value = Math.min(this.max, Math.max(this.min, this.value));
        this._sync();
        if (!opts.silent) this.onChange(this.getState());
    }

    _formatValue(n) {
        return `${n}${this.valueSuffix}`;
    }

    _render() {
        this.root.classList.add("toggle-dd", "dropdown");
        const a11yAttr = this.a11yKey ? ` data-a11y-key="${this.a11yKey}"` : "";
        this.root.innerHTML = `
            <button type="button"
                    class="btn toggle-dd__btn toggle-dd__btn--menu"
                    title="Right-click for level"
                    aria-haspopup="dialog"
                    aria-expanded="false"
                    aria-pressed="false"${a11yAttr}>
                <span class="toggle-dd__led" aria-hidden="true"></span>
                <span class="toggle-dd__label"></span>
                <span class="toggle-dd__menu-hint" aria-hidden="true"></span>
            </button>
            <div class="dropdown-menu toggle-dd__menu toggle-dd__menu--slider">
                <div class="toggle-dd__slider-value" aria-live="polite"></div>
                <div class="toggle-dd__slider-row">
                    <span class="toggle-dd__slider-end"></span>
                    <input type="range"
                           class="toggle-dd__slider"
                           min="${this.min}"
                           max="${this.max}"
                           step="${this.step}"
                           value="${this.value}">
                    <span class="toggle-dd__slider-end"></span>
                </div>
            </div>
        `;

        this.button = this.root.querySelector(".toggle-dd__btn");
        this.led = this.root.querySelector(".toggle-dd__led");
        this.labelEl = this.root.querySelector(".toggle-dd__label");
        this.menu = this.root.querySelector(".toggle-dd__menu");
        this.slider = this.root.querySelector(".toggle-dd__slider");
        this.valueEl = this.root.querySelector(".toggle-dd__slider-value");
        const ends = this.root.querySelectorAll(".toggle-dd__slider-end");
        this.endMinEl = ends[0];
        this.endMaxEl = ends[1];
    }

    _bind() {
        this.button.addEventListener("click", (event) => {
            event.preventDefault();
            if (this._ignoreNextClick) {
                this._ignoreNextClick = false;
                return;
            }
            this._setMenuOpen(false);
            this.enabled = !this.enabled;
            this._sync();
            this.onChange(this.getState());
        });

        this.button.addEventListener("contextmenu", (event) => {
            event.preventDefault();
            this._ignoreNextClick = true;
            this._setMenuOpen(!this.menuOpen);
            window.setTimeout(() => {
                this._ignoreNextClick = false;
            }, 50);
        });

        this.slider.addEventListener("input", () => {
            this.value = Number(this.slider.value);
            this._sync();
            this.onChange(this.getState());
        });

        this.slider.addEventListener("click", (event) => {
            event.stopPropagation();
        });
    }

    _setMenuOpen(open) {
        this.menuOpen = open;
        this.menu.classList.toggle("show", open);
        this.button.setAttribute("aria-expanded", open ? "true" : "false");

        if (open) {
            document.addEventListener("pointerdown", this._onDocPointer, true);
            document.addEventListener("keydown", this._onKeydown);
            window.setTimeout(() => this.slider.focus(), 0);
        } else {
            document.removeEventListener("pointerdown", this._onDocPointer, true);
            document.removeEventListener("keydown", this._onKeydown);
        }
    }

    _onDocPointer(event) {
        if (!this.root.contains(event.target)) {
            this._setMenuOpen(false);
        }
    }

    _onKeydown(event) {
        if (event.key === "Escape") {
            this._setMenuOpen(false);
            this.button.focus();
        }
    }

    _sync() {
        const formatted = this._formatValue(this.value);
        this.labelEl.textContent = `${this.label} · ${formatted}`;
        this.valueEl.textContent = formatted;
        this.slider.min = String(this.min);
        this.slider.max = String(this.max);
        this.slider.step = String(this.step);
        this.slider.value = String(this.value);
        this.slider.setAttribute("aria-valuenow", String(this.value));
        this.slider.setAttribute(
            "aria-label",
            `${this.label} ${this._formatValue(this.min)} to ${this._formatValue(this.max)}`
        );
        if (this.endMinEl) this.endMinEl.textContent = this._formatValue(this.min);
        if (this.endMaxEl) this.endMaxEl.textContent = this._formatValue(this.max);
        this.button.setAttribute("aria-pressed", this.enabled ? "true" : "false");
        this.button.setAttribute("aria-label", `${this.label}: ${this.enabled ? "on" : "off"}, ${formatted}`);
        this.led.classList.toggle("is-on", this.enabled);
    }
}

/**
 * Yaesu key: left-click toggles on/off. No right-click menu.
 */
export class ToggleButton {
    /**
     * @param {HTMLElement} root
     * @param {{
     *   label?: string,
     *   enabled?: boolean,
     *   a11yKey?: string,
     *   onChange?: (state: { enabled: boolean }) => void
     * }} [config]
     */
    constructor(root, config = {}) {
        this.root = root;
        this.label = config.label ?? "Toggle";
        this.enabled = Boolean(config.enabled);
        this.a11yKey = config.a11yKey ?? null;
        this.onChange = config.onChange ?? (() => {});

        this._render();
        this._bind();
        this._sync();
    }

    getState() {
        return { enabled: this.enabled };
    }

    /**
     * @param {{ enabled?: boolean }} partial
     * @param {{ silent?: boolean }} [opts]
     */
    setState(partial = {}, opts = {}) {
        if (partial.enabled !== undefined) this.enabled = Boolean(partial.enabled);
        this._sync();
        if (!opts.silent) this.onChange(this.getState());
    }

    _render() {
        this.root.classList.add("toggle-dd");
        const a11yAttr = this.a11yKey ? ` data-a11y-key="${this.a11yKey}"` : "";
        this.root.innerHTML = `
            <button type="button"
                    class="btn toggle-dd__btn"
                    aria-pressed="false"${a11yAttr}>
                <span class="toggle-dd__led" aria-hidden="true"></span>
                <span class="toggle-dd__label"></span>
            </button>
        `;

        this.button = this.root.querySelector(".toggle-dd__btn");
        this.led = this.root.querySelector(".toggle-dd__led");
        this.labelEl = this.root.querySelector(".toggle-dd__label");
    }

    _bind() {
        this.button.addEventListener("click", (event) => {
            event.preventDefault();
            this.enabled = !this.enabled;
            this._sync();
            this.onChange(this.getState());
        });

        this.button.addEventListener("contextmenu", (event) => {
            event.preventDefault();
        });
    }

    _sync() {
        this.labelEl.textContent = this.label;
        this.button.setAttribute("aria-pressed", this.enabled ? "true" : "false");
        this.button.setAttribute("aria-label", `${this.label}: ${this.enabled ? "on" : "off"}`);
        this.led.classList.toggle("is-on", this.enabled);
    }
}

/**
 * Yaesu key: left-click cycles discrete options; right-click opens a
 * slider or option-list context menu.
 *
 * With linkSliderToOptions, the slider indexes the same option list
 * (e.g. IF Width): cycle and slider stay in sync; face shows label · option.
 */
export class CycleContextButton {
    /**
     * @param {HTMLElement} root
     * @param {{
     *   label?: string,
     *   options?: { id: string, label: string }[],
     *   selectedId?: string,
     *   offId?: string | null,
     *   linkSliderToOptions?: boolean,
     *   showLed?: boolean,
     *   a11yKey?: string,
     *   context?:
     *     | { type: "slider", min?: number, max?: number, value?: number }
     *     | { type: "menu", options: { id: string, label: string }[], selectedId?: string, menuClass?: string },
     *   onChange?: (state: { selectedId: string, value?: number, contextId?: string }) => void
     * }} [config]
     */
    constructor(root, config = {}) {
        this.root = root;
        this.label = config.label ?? "Function";
        this.options = (config.options ?? [
            { id: "0", label: "OFF" },
            { id: "1", label: "ON" },
        ]).map((o) => ({ id: String(o.id), label: String(o.label) }));
        this.linkSliderToOptions = Boolean(config.linkSliderToOptions);
        this.offId =
            config.offId !== undefined
                ? config.offId == null
                    ? null
                    : String(config.offId)
                : this.linkSliderToOptions
                  ? null
                  : "0";
        this.showLed =
            config.showLed !== undefined ? Boolean(config.showLed) : !this.linkSliderToOptions;
        this.a11yKey = config.a11yKey ?? null;
        this.selectedId = String(config.selectedId ?? this.options[0]?.id ?? "");
        this.onChange = config.onChange ?? (() => {});
        this.menuOpen = false;
        this._ignoreNextClick = false;
        this.disabled = false;

        const ctx = config.context ?? { type: "slider", min: 1, max: 15, value: 1 };
        this.contextType = ctx.type === "menu" ? "menu" : "slider";

        if (this.contextType === "slider") {
            if (this.linkSliderToOptions) {
                this.min = 0;
                this.max = Math.max(0, this.options.length - 1);
                const idx = this.options.findIndex((o) => o.id === this.selectedId);
                this.value = idx >= 0 ? idx : 0;
                if (idx < 0 && this.options[0]) this.selectedId = this.options[0].id;
            } else {
                this.min = Number(ctx.min ?? 1);
                this.max = Number(ctx.max ?? 15);
                this.value = Number(ctx.value ?? this.min);
            }
            this.contextOptions = [];
            this.contextId = null;
            this.menuClass = "";
        } else {
            this.min = 1;
            this.max = 15;
            this.value = null;
            this.contextOptions = ctx.options ?? [];
            this.contextId = ctx.selectedId ?? this.contextOptions[0]?.id ?? null;
            this.menuClass = ctx.menuClass ?? "";
        }

        this._onDocPointer = this._onDocPointer.bind(this);
        this._onKeydown = this._onKeydown.bind(this);

        this._render();
        this._bind();
        this._sync();
    }

    getState() {
        if (this.contextType === "slider") {
            return {
                selectedId: this.selectedId,
                value: this.value,
                disabled: Boolean(this.disabled),
            };
        }
        return {
            selectedId: this.selectedId,
            contextId: this.contextId,
            disabled: Boolean(this.disabled),
        };
    }

    /**
     * Replace the option list (and, when linked, the slider range).
     * @param {{ id: string, label: string }[]} options
     * @param {{ silent?: boolean, selectedId?: string }} [opts]
     */
    setOptions(options, opts = {}) {
        this.options = (options ?? []).map((o) => ({
            id: String(o.id),
            label: String(o.label),
        }));
        if (opts.selectedId !== undefined) {
            this.selectedId = String(opts.selectedId);
        }
        if (this.linkSliderToOptions && this.contextType === "slider") {
            this.min = 0;
            this.max = Math.max(0, this.options.length - 1);
            let idx = this.options.findIndex((o) => o.id === this.selectedId);
            if (idx < 0) idx = 0;
            this.selectedId = this.options[idx]?.id ?? "";
            this.value = idx;
            if (this.slider) {
                this.slider.min = String(this.min);
                this.slider.max = String(this.max);
            }
        } else if (!this.options.some((o) => o.id === this.selectedId)) {
            this.selectedId = this.options[0]?.id ?? "";
        }
        this._sync();
        if (!opts.silent) this.onChange(this.getState());
    }

    /**
     * @param {boolean} disabled
     */
    setDisabled(disabled) {
        this.disabled = Boolean(disabled);
        if (this.button) this.button.disabled = this.disabled;
        if (this.disabled) this._setMenuOpen(false);
    }

    /**
     * @param {{ selectedId?: string, value?: number, contextId?: string }} partial
     * @param {{ silent?: boolean }} [opts]
     */
    setState(partial = {}, opts = {}) {
        if (partial.selectedId !== undefined) {
            this.selectedId = String(partial.selectedId);
            if (this.linkSliderToOptions && this.contextType === "slider") {
                const idx = this.options.findIndex((o) => o.id === this.selectedId);
                if (idx >= 0) this.value = idx;
            }
        }
        if (this.contextType === "slider" && partial.value !== undefined) {
            const n = Number(partial.value);
            if (!Number.isNaN(n)) {
                this.value = Math.min(this.max, Math.max(this.min, n));
                if (this.linkSliderToOptions && this.options[this.value]) {
                    this.selectedId = this.options[this.value].id;
                }
            }
        }
        if (this.contextType === "menu" && partial.contextId !== undefined) {
            this.contextId = String(partial.contextId);
        }
        this._sync();
        if (!opts.silent) this.onChange(this.getState());
    }

    _optionLabel() {
        return (
            this.options.find((o) => String(o.id) === String(this.selectedId))?.label ??
            this.selectedId
        );
    }

    _contextLabel() {
        if (this.linkSliderToOptions) return this._optionLabel();
        if (this.contextType === "slider") return String(this.value);
        return this.contextOptions.find((o) => o.id === this.contextId)?.label ?? this.contextId;
    }

    _render() {
        this.root.classList.add("toggle-dd", "dropdown");
        if (!this.showLed) this.root.classList.add("toggle-dd--no-led");

        const popup = this.contextType === "slider" ? "dialog" : "menu";
        const a11yAttr = this.a11yKey ? ` data-a11y-key="${this.a11yKey}"` : "";
        let menuHtml;
        if (this.contextType === "slider") {
            menuHtml = `
            <div class="dropdown-menu toggle-dd__menu toggle-dd__menu--slider">
                <div class="toggle-dd__slider-value" aria-live="polite"></div>
                <div class="toggle-dd__slider-row">
                    <span class="toggle-dd__slider-end"></span>
                    <input type="range"
                           class="toggle-dd__slider"
                           min="${this.min}"
                           max="${this.max}"
                           step="1"
                           value="${this.value}">
                    <span class="toggle-dd__slider-end"></span>
                </div>
            </div>`;
        } else {
            const items = this.contextOptions
                .map(
                    (opt) => `
                <li role="none">
                    <button type="button"
                            class="dropdown-item toggle-dd__item"
                            role="menuitemradio"
                            data-option-id="${opt.id}"
                            aria-checked="false">
                        ${opt.label}
                    </button>
                </li>`
                )
                .join("");
            menuHtml = `<ul class="dropdown-menu toggle-dd__menu ${this.menuClass}" role="menu">${items}</ul>`;
        }

        this.root.innerHTML = `
            <button type="button"
                    class="btn toggle-dd__btn toggle-dd__btn--menu"
                    title="Left-click to cycle; right-click for level"
                    aria-haspopup="${popup}"
                    aria-expanded="false"
                    aria-pressed="false"${a11yAttr}>
                ${this.showLed ? `<span class="toggle-dd__led" aria-hidden="true"></span>` : ""}
                <span class="toggle-dd__label"></span>
                <span class="toggle-dd__menu-hint" aria-hidden="true"></span>
            </button>
            ${menuHtml}
        `;

        this.button = this.root.querySelector(".toggle-dd__btn");
        this.led = this.root.querySelector(".toggle-dd__led");
        this.labelEl = this.root.querySelector(".toggle-dd__label");
        this.menu = this.root.querySelector(".toggle-dd__menu");
        this.slider = this.root.querySelector(".toggle-dd__slider");
        this.valueEl = this.root.querySelector(".toggle-dd__slider-value");
        if (this.contextType === "slider") {
            const ends = this.root.querySelectorAll(".toggle-dd__slider-end");
            this.endMinEl = ends[0];
            this.endMaxEl = ends[1];
        }
    }

    _bind() {
        this.button.addEventListener("click", (event) => {
            event.preventDefault();
            if (this.disabled) return;
            if (this._ignoreNextClick) {
                this._ignoreNextClick = false;
                return;
            }
            if (!this.options.length) return;
            this._setMenuOpen(false);
            const idx = this.options.findIndex((o) => o.id === this.selectedId);
            const next = (idx < 0 ? 0 : idx + 1) % this.options.length;
            this.selectedId = this.options[next].id;
            if (this.linkSliderToOptions && this.contextType === "slider") {
                this.value = next;
            }
            this._sync();
            this.onChange(this.getState());
        });

        this.button.addEventListener("contextmenu", (event) => {
            event.preventDefault();
            if (this.disabled) return;
            this._ignoreNextClick = true;
            this._setMenuOpen(!this.menuOpen);
            window.setTimeout(() => {
                this._ignoreNextClick = false;
            }, 50);
        });

        if (this.contextType === "slider") {
            this.slider.addEventListener("input", () => {
                this.value = Number(this.slider.value);
                if (this.linkSliderToOptions && this.options[this.value]) {
                    this.selectedId = this.options[this.value].id;
                }
                this._sync();
                this.onChange(this.getState());
            });
            this.slider.addEventListener("click", (event) => {
                event.stopPropagation();
            });
        } else {
            this.menu.addEventListener("click", (event) => {
                const item = event.target.closest("[data-option-id]");
                if (!item) return;
                event.preventDefault();
                this.contextId = item.getAttribute("data-option-id");
                this._setMenuOpen(false);
                this._sync();
                this.onChange(this.getState());
            });
        }
    }

    _setMenuOpen(open) {
        this.menuOpen = open;
        this.menu.classList.toggle("show", open);
        this.button.setAttribute("aria-expanded", open ? "true" : "false");

        if (open) {
            document.addEventListener("pointerdown", this._onDocPointer, true);
            document.addEventListener("keydown", this._onKeydown);
            if (this.contextType === "slider" && this.slider) {
                window.setTimeout(() => this.slider.focus(), 0);
            }
        } else {
            document.removeEventListener("pointerdown", this._onDocPointer, true);
            document.removeEventListener("keydown", this._onKeydown);
        }
    }

    _onDocPointer(event) {
        if (!this.root.contains(event.target)) {
            this._setMenuOpen(false);
        }
    }

    _onKeydown(event) {
        if (event.key === "Escape") {
            this._setMenuOpen(false);
            this.button.focus();
        }
    }

    _sync() {
        const optionLabel = this._optionLabel();
        const contextLabel = this._contextLabel();
        const isOn = this.offId == null ? true : this.selectedId !== this.offId;

        if (this.linkSliderToOptions) {
            this.labelEl.textContent = `${this.label} · ${optionLabel}`;
            this.button.setAttribute("aria-label", `${this.label}: ${optionLabel}`);
            this.button.title = `${this.label}: left-click to cycle, right-click for slider`;
        } else {
            // Off faceplate keeps the key name (DNR/NR), like NB — not the option "OFF".
            const faceLabel = isOn ? optionLabel : this.label;
            this.labelEl.textContent = `${faceLabel} · ${contextLabel}`;
            this.button.setAttribute(
                "aria-label",
                `${this.label}: ${isOn ? optionLabel : "off"}, level ${contextLabel}`
            );
            this.button.title = `${this.label}: left-click to cycle, right-click for level`;
        }

        if (this.showLed && this.led) {
            this.button.setAttribute("aria-pressed", isOn ? "true" : "false");
            this.led.classList.toggle("is-on", isOn);
        } else {
            this.button.removeAttribute("aria-pressed");
        }

        if (this.contextType === "slider") {
            this.valueEl.textContent = this.linkSliderToOptions ? optionLabel : String(this.value);
            this.slider.min = String(this.min);
            this.slider.max = String(this.max);
            this.slider.value = String(this.value);
            this.slider.setAttribute("aria-valuenow", String(this.value));
            this.slider.setAttribute(
                "aria-label",
                this.linkSliderToOptions
                    ? `${this.label} ${this.options[0]?.label ?? ""} to ${this.options[this.options.length - 1]?.label ?? ""}`
                    : `${this.label} level ${this.min} to ${this.max}`
            );
            if (this.endMinEl) {
                this.endMinEl.textContent = this.linkSliderToOptions
                    ? this.options[0]?.label ?? String(this.min)
                    : String(this.min);
            }
            if (this.endMaxEl) {
                this.endMaxEl.textContent = this.linkSliderToOptions
                    ? this.options[this.options.length - 1]?.label ?? String(this.max)
                    : String(this.max);
            }
        } else {
            for (const item of this.menu.querySelectorAll("[data-option-id]")) {
                const isSelected = item.getAttribute("data-option-id") === this.contextId;
                item.classList.toggle("active", isSelected);
                item.setAttribute("aria-checked", isSelected ? "true" : "false");
            }
        }
    }
}
