function wheelStep(event, step) {
    if (!event.deltaY || !Number.isFinite(step) || step <= 0) return 0;
    return event.deltaY < 0 ? step : -step;
}

function nextOptionIndex(options, selectedId, direction) {
    if (!options.length || !direction) return -1;
    const current = options.findIndex((option) => String(option.id) === String(selectedId));
    const index = current < 0 ? (direction > 0 ? 0 : options.length - 1) : current + direction;
    return (index + options.length) % options.length;
}

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
            this.clickAction === "openMenu"
                ? `<span class="toggle-dd__menu-hint toggle-dd__menu-hint--side" aria-hidden="true"></span>`
                : this.contextMenuEnabled
                  ? `<span class="toggle-dd__menu-hint" aria-hidden="true"></span>`
                  : "";
        const ledHtml = this.showLed
            ? `<span class="toggle-dd__led" aria-hidden="true"></span>`
            : "";
        const a11yAttr = this.a11yKey ? ` data-a11y-key="${this.a11yKey}"` : "";
        const menuBtnClass =
            this.clickAction === "openMenu"
                ? "btn toggle-dd__btn toggle-dd__btn--menu toggle-dd__btn--open-menu"
                : "btn toggle-dd__btn toggle-dd__btn--menu";

        this.root.innerHTML = `
            <button type="button"
                    class="${menuBtnClass}"
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

        this.button.addEventListener("wheel", (event) => {
            if (this.disabled || !this.options.length) return;
            const direction = event.deltaY < 0 ? -1 : 1;
            const index = nextOptionIndex(this.options, this.selectedId, direction);
            if (index < 0) return;
            event.preventDefault();
            this.selectedId = String(this.options[index].id);
            if (this.offId != null) {
                this.enabled = this.selectedId !== this.offId;
                if (this.enabled) this._lastOnId = this.selectedId;
            }
            this._setMenuOpen(false);
            this._sync();
            this.onChange(this.getState());
        }, { passive: false });

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
     *   showLed?: boolean,
     *   clickOpensSlider?: boolean,
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
        this.showLed = config.showLed !== false;
        this.clickOpensSlider = Boolean(config.clickOpensSlider);
        this.a11yKey = config.a11yKey ?? null;
        this.onChange = config.onChange ?? (() => {});
        this.menuOpen = false;
        this._ignoreNextClick = false;
        this.disabled = false;

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
        if (!this.showLed) this.root.classList.add("toggle-dd--no-led");

        const a11yAttr = this.a11yKey ? ` data-a11y-key="${this.a11yKey}"` : "";
        const title = this.clickOpensSlider
            ? "Click for level"
            : "Right-click for level";
        const ledHtml = this.showLed
            ? `<span class="toggle-dd__led" aria-hidden="true"></span>`
            : "";
        const pressedAttr = this.showLed ? ` aria-pressed="false"` : "";

        this.root.innerHTML = `
            <button type="button"
                    class="btn toggle-dd__btn toggle-dd__btn--menu"
                    title="${title}"
                    aria-haspopup="dialog"
                    aria-expanded="false"${pressedAttr}${a11yAttr}>
                ${ledHtml}
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
            if (this.disabled) return;
            if (this._ignoreNextClick) {
                this._ignoreNextClick = false;
                return;
            }
            if (this.clickOpensSlider) {
                this._setMenuOpen(!this.menuOpen);
                return;
            }
            this._setMenuOpen(false);
            this.enabled = !this.enabled;
            this._sync();
            this.onChange(this.getState());
        });

        this.button.addEventListener("contextmenu", (event) => {
            event.preventDefault();
            if (this.disabled) return;
            if (this.clickOpensSlider) {
                // Left-click already opens the slider — swallow right-click.
                return;
            }
            this._ignoreNextClick = true;
            this._setMenuOpen(!this.menuOpen);
            window.setTimeout(() => {
                this._ignoreNextClick = false;
            }, 50);
        });

        this.button.addEventListener("wheel", (event) => {
            if (this.disabled) return;
            const delta = wheelStep(event, this.step);
            if (!delta) return;
            event.preventDefault();
            this.value = Math.min(this.max, Math.max(this.min, this.value + delta));
            this._sync();
            this.onChange(this.getState());
        }, { passive: false });

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
        if (this.showLed) {
            this.button.setAttribute("aria-pressed", this.enabled ? "true" : "false");
            this.button.setAttribute(
                "aria-label",
                `${this.label}: ${this.enabled ? "on" : "off"}, ${formatted}`
            );
            if (this.led) this.led.classList.toggle("is-on", this.enabled);
        } else {
            this.button.removeAttribute("aria-pressed");
            this.button.setAttribute("aria-label", `${this.label}: ${formatted}`);
        }
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
 * Yaesu key: left-click cycles discrete options (or jumps to a reset
 * option); right-click opens a slider or option-list context menu.
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
     *   clickAction?: "cycle" | "select" | "reset",
     *   resetValue?: number,
     *   clickSelectId?: string,
     *   extraLabels?: Record<string, string>,
     *   sliderAlias?: Record<string, string>,
     *   offIds?: string[],
     *   linkSliderToOptions?: boolean,
     *   showLed?: boolean,
     *   a11yKey?: string,
     *   context?:
     *     | { type: "slider", min?: number, max?: number, step?: number, value?: number, valueSuffix?: string }
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
        this.clickAction =
            config.clickAction === "select" || config.clickAction === "reset"
                ? config.clickAction
                : "cycle";
        this.clickSelectId =
            config.clickSelectId != null ? String(config.clickSelectId) : "0";
        this.resetValue = Number(config.resetValue ?? 0);
        this.extraLabels = { ...(config.extraLabels ?? {}) };
        this.sliderAlias = { ...(config.sliderAlias ?? {}) };
        this.offIds = config.offIds ? new Set(config.offIds.map(String)) : null;
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
            this.step = Math.max(0.001, Number(ctx.step ?? 1));
            this.valueSuffix = ctx.valueSuffix ?? "";
            if (this.linkSliderToOptions) {
                this.min = 0;
                this.max = Math.max(0, this.options.length - 1);
                const idx = this._sliderIndexFor(this.selectedId);
                this.value = idx >= 0 ? idx : 0;
            } else {
                this.min = Number(ctx.min ?? 1);
                this.max = Number(ctx.max ?? 15);
                this.value = Number(ctx.value ?? this.min);
            }
            this.contextOptions = [];
            this.contextId = null;
            this.menuClass = "";
        } else {
            this.step = 1;
            this.valueSuffix = "";
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

    setLabel(label) {
        this.label = String(label ?? "");
        if (this.options.length === 1 && this.options[0].id === this.selectedId) {
            this.options[0].label = this.label;
        }
        this._sync();
    }

    /**
     * Replace the option list (and, when linked, the slider range).
     * @param {{ id: string, label: string }[]} options
     * @param {{
     *   silent?: boolean,
     *   selectedId?: string,
     *   extraLabels?: Record<string, string>,
     *   sliderAlias?: Record<string, string>,
     *   offIds?: string[],
     *   clickSelectId?: string,
     * }} [opts]
     */
    setOptions(options, opts = {}) {
        this.options = (options ?? []).map((o) => ({
            id: String(o.id),
            label: String(o.label),
        }));
        if (opts.extraLabels) this.extraLabels = { ...opts.extraLabels };
        if (opts.sliderAlias) this.sliderAlias = { ...opts.sliderAlias };
        if (opts.offIds) this.offIds = new Set(opts.offIds.map(String));
        if (opts.clickSelectId != null) this.clickSelectId = String(opts.clickSelectId);
        if (opts.selectedId !== undefined) {
            this.selectedId = String(opts.selectedId);
        }
        if (this.linkSliderToOptions && this.contextType === "slider") {
            this._applySliderIndex();
            if (
                this._sliderIndexFor(this.selectedId) < 0 &&
                this.extraLabels[this.selectedId] == null
            ) {
                this.selectedId = this.options[0]?.id ?? "";
                this._applySliderIndex();
            }
        } else if (
            !this.options.some((o) => o.id === this.selectedId) &&
            this.extraLabels[this.selectedId] == null
        ) {
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
     * @param {{ selectedId?: string, value?: number, min?: number, max?: number, contextId?: string }} partial
     * @param {{ silent?: boolean }} [opts]
     */
    setState(partial = {}, opts = {}) {
        if (this.contextType === "slider") {
            if (partial.min !== undefined) {
                const n = Number(partial.min);
                if (!Number.isNaN(n)) this.min = n;
            }
            if (partial.max !== undefined) {
                const n = Number(partial.max);
                if (!Number.isNaN(n)) this.max = n;
            }
        }
        if (partial.selectedId !== undefined) {
            this.selectedId = String(partial.selectedId);
            if (this.linkSliderToOptions && this.contextType === "slider") {
                const idx = this._sliderIndexFor(this.selectedId);
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
        if (this.contextType === "slider") {
            this.value = Math.min(this.max, Math.max(this.min, this.value));
        }
        if (this.contextType === "menu" && partial.contextId !== undefined) {
            this.contextId = String(partial.contextId);
        }
        this._sync();
        if (!opts.silent) this.onChange(this.getState());
    }

    _optionLabel() {
        const id = String(this.selectedId);
        return (
            this.options.find((o) => o.id === id)?.label ??
            this.extraLabels[id] ??
            this.selectedId
        );
    }

    _clickSelectLabel() {
        const id = String(this.clickSelectId);
        return (
            this.extraLabels[id] ??
            this.options.find((o) => o.id === id)?.label ??
            this.clickSelectId
        );
    }

    _isOn() {
        const id = String(this.selectedId);
        if (this.offIds instanceof Set && this.offIds.size > 0) {
            return !this.offIds.has(id);
        }
        if (this.offId == null) return true;
        return id !== this.offId;
    }

    _sliderIndexFor(id) {
        const sid = String(id);
        let idx = this.options.findIndex((o) => o.id === sid);
        if (idx >= 0) return idx;
        const alias = this.sliderAlias?.[sid];
        if (alias != null) {
            idx = this.options.findIndex((o) => o.id === String(alias));
            if (idx >= 0) return idx;
        }
        return -1;
    }

    _applySliderIndex() {
        this.min = 0;
        this.max = Math.max(0, this.options.length - 1);
        const idx = this._sliderIndexFor(this.selectedId);
        this.value = idx >= 0 ? idx : 0;
        if (this.slider) {
            this.slider.min = String(this.min);
            this.slider.max = String(this.max);
        }
    }

    _contextLabel() {
        if (this.linkSliderToOptions) return this._optionLabel();
        if (this.contextType === "slider") return `${this.value}${this.valueSuffix}`;
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
                           step="${this.step}"
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
                    title="${this.clickAction === "reset" ? "Left-click to reset; right-click for level" : "Left-click to cycle; right-click for level"}"
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
            if (this.clickAction === "reset") {
                if (this.contextType === "slider") this.value = this.resetValue;
            } else if (this.clickAction === "select") {
                this.selectedId = this.clickSelectId;
                if (this.linkSliderToOptions && this.contextType === "slider") {
                    this._applySliderIndex();
                }
            } else {
                const idx = this.options.findIndex((o) => o.id === this.selectedId);
                const next = (idx < 0 ? 0 : idx + 1) % this.options.length;
                this.selectedId = this.options[next].id;
                if (this.linkSliderToOptions && this.contextType === "slider") {
                    this.value = next;
                }
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

        this.button.addEventListener("wheel", (event) => {
            if (this.disabled) return;
            const direction = event.deltaY < 0 ? -1 : 1;
            if (this.contextType === "slider") {
                const delta = wheelStep(event, this.step);
                if (!delta) return;
                event.preventDefault();
                this.value = Math.min(this.max, Math.max(this.min, this.value + delta));
                if (this.linkSliderToOptions && this.options[this.value]) {
                    this.selectedId = this.options[this.value].id;
                }
            } else {
                const index = nextOptionIndex(this.contextOptions, this.contextId, direction);
                if (index < 0) return;
                event.preventDefault();
                this.contextId = String(this.contextOptions[index].id);
            }
            this._sync();
            this.onChange(this.getState());
        }, { passive: false });

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
        const isOn = this._isOn();

        if (this.linkSliderToOptions) {
            this.labelEl.textContent = `${this.label} · ${optionLabel}`;
            this.button.setAttribute("aria-label", `${this.label}: ${optionLabel}`);
            const clickHint =
                this.clickAction === "select"
                    ? `left-click to reset to ${this._clickSelectLabel()}, right-click for slider`
                    : "left-click to cycle, right-click for slider";
            this.button.title = `${this.label}: ${clickHint}`;
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
            this.valueEl.textContent = this.linkSliderToOptions
                ? optionLabel
                : `${this.value}${this.valueSuffix}`;
            this.slider.min = String(this.min);
            this.slider.max = String(this.max);
            this.slider.step = String(this.step);
            this.slider.value = String(this.value);
            this.slider.setAttribute("aria-valuenow", String(this.value));
            this.slider.setAttribute(
                "aria-label",
                this.linkSliderToOptions
                    ? `${this.label} ${this.options[0]?.label ?? ""} to ${this.options[this.options.length - 1]?.label ?? ""}`
                    : `${this.label} level ${this.min}${this.valueSuffix} to ${this.max}${this.valueSuffix}`
            );
            if (this.endMinEl) {
                this.endMinEl.textContent = this.linkSliderToOptions
                    ? this.options[0]?.label ?? String(this.min)
                    : `${this.min}${this.valueSuffix}`;
            }
            if (this.endMaxEl) {
                this.endMaxEl.textContent = this.linkSliderToOptions
                    ? this.options[this.options.length - 1]?.label ?? String(this.max)
                    : `${this.max}${this.valueSuffix}`;
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

function escapeAttr(value) {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/"/g, "&quot;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

/**
 * Yaesu key whose face is a fixed label. Left-click opens a menu of
 * commands (not a selected state). Picking an item fires it every time,
 * including repeats of the same command — e.g. QMB Recall stepping slots.
 */
export class ActionMenuButton {
    /**
     * @param {HTMLElement} root
     * @param {{
     *   label?: string,
     *   actions?: { id: string, label: string, title?: string, ariaLabel?: string }[],
     *   a11yKey?: string,
     *   title?: string,
     *   menuClass?: string,
     *   variant?: "yaesu" | "toolbar",
     *   onAction?: (id: string) => void
     * }} [config]
     */
    constructor(root, config = {}) {
        this.root = root;
        this.label = config.label ?? "Function";
        this.actions = (config.actions ?? []).map((a) => ({
            id: String(a.id),
            label: String(a.label),
            title: a.title != null ? String(a.title) : "",
            ariaLabel: a.ariaLabel != null ? String(a.ariaLabel) : String(a.label),
        }));
        this.a11yKey = config.a11yKey ?? null;
        this.title = config.title ?? "Click for options";
        this.menuClass = config.menuClass ?? "";
        this.variant = config.variant === "toolbar" ? "toolbar" : "yaesu";
        this.onAction = config.onAction ?? (() => {});
        this.menuOpen = false;
        this._ariaOverride = null;

        this._onDocPointer = this._onDocPointer.bind(this);
        this._onKeydown = this._onKeydown.bind(this);

        this._render();
        this._bind();
        this._sync();
    }

    _render() {
        const isToolbar = this.variant === "toolbar";
        if (isToolbar) {
            this.root.classList.add("dropdown", "qmb-toolbar-key");
        } else {
            this.root.classList.add("toggle-dd", "toggle-dd--no-led", "dropdown");
        }
        const a11yAttr = this.a11yKey ? ` data-a11y-key="${escapeAttr(this.a11yKey)}"` : "";
        const itemClass = isToolbar
            ? "dropdown-item qmb-toolbar-key__item"
            : "dropdown-item toggle-dd__item";
        const items = this.actions
            .map((action) => {
                const titleAttr = action.title
                    ? ` title="${escapeAttr(action.title)}"`
                    : "";
                return `
                <li role="none">
                    <button type="button"
                            class="${itemClass}"
                            role="menuitem"
                            data-action-id="${escapeAttr(action.id)}"
                            aria-label="${escapeAttr(action.ariaLabel)}"${titleAttr}>
                        ${escapeAttr(action.label)}
                    </button>
                </li>`;
            })
            .join("");

        // Toolbar variant kept for callers that still opt in; default Yaesu
        // face matches the operating-bar action keys (no LED). Side menu hint
        // marks that left-click opens commands rather than firing one action.
        const buttonClass = isToolbar
            ? "btn toggle-dd__btn toggle-dd__btn--action"
            : "btn toggle-dd__btn toggle-dd__btn--action toggle-dd__btn--menu toggle-dd__btn--open-menu";
        const hintHtml = isToolbar
            ? ""
            : `<span class="toggle-dd__menu-hint toggle-dd__menu-hint--side" aria-hidden="true"></span>`;
        const menuClass = isToolbar
            ? `dropdown-menu qmb-toolbar-key__menu ${this.menuClass}`
            : `dropdown-menu toggle-dd__menu ${this.menuClass}`;

        this.root.innerHTML = `
            <button type="button"
                    class="${buttonClass}"
                    title="${escapeAttr(this.title)}"
                    aria-haspopup="menu"
                    aria-expanded="false"${a11yAttr}>
                <span class="toggle-dd__label"></span>
                ${hintHtml}
            </button>
            <ul class="${menuClass}" role="menu">${items}</ul>
        `;

        this.button = this.root.querySelector("button[aria-haspopup='menu']");
        this.labelEl = this.root.querySelector(".toggle-dd__label");
        this.menu = this.root.querySelector("[role='menu']");
    }

    _bind() {
        this.button.addEventListener("click", (event) => {
            event.preventDefault();
            this._setMenuOpen(!this.menuOpen);
        });

        this.button.addEventListener("contextmenu", (event) => {
            event.preventDefault();
        });

        this.menu.addEventListener("click", (event) => {
            const item = event.target.closest("[data-action-id]");
            if (!item) return;
            event.preventDefault();
            const id = item.getAttribute("data-action-id");
            this._setMenuOpen(false);
            this.onAction(id);
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
        this.labelEl.textContent = this.label;
        if (this._ariaOverride) {
            this.button.setAttribute("aria-label", this._ariaOverride);
            this.button.setAttribute("title", this._ariaOverride);
        } else {
            this.button.setAttribute("aria-label", this.title);
            this.button.setAttribute("title", this.title);
        }
    }
}
