/*
 * theme.js — the ES-module API over theme-boot.js.
 * Shared by Yaesu Web Control and Icom Web Control.
 *
 * theme-boot.js has to be a classic, render-blocking script (see its
 * header), and it therefore cannot export anything. This module is the
 * normal way for the rest of the page to reach it: same storage key, same
 * theme list, one implementation.
 *
 * It also reads the theme's tokens back out of the cascade, which is what
 * anything drawing to a <canvas> needs — a meter face or a spectrum trace
 * is painted by JavaScript, and CSS cannot reach a single pixel of it.
 */

/** The default returned when theme-boot.js has not run (it always should). */
const FALLBACK = 'classic';

function boot() {
    return typeof window !== 'undefined' ? window.RadioWebControlTheme : undefined;
}

/** The theme in force right now, e.g. "classic" or "instrument". */
export function currentTheme() {
    const b = boot();
    return b ? b.current() : FALLBACK;
}

/** Every theme name the application knows how to apply. */
export function themeNames() {
    const b = boot();
    return b ? b.names.slice() : [FALLBACK];
}

/** This device's stored override, or null when it follows the server. */
export function themeOverride() {
    const b = boot();
    return b ? b.override() : null;
}

/** True when the given theme (default: the current one) is a dark theme. */
export function isDarkTheme(name) {
    const b = boot();
    return b ? b.isDark(name) : false;
}

/**
 * Apply a theme to this device and remember it. Pass null to forget the
 * override and follow the server's default again.
 * Returns the theme now in force.
 */
export function setTheme(name) {
    const b = boot();
    return b ? b.set(name) : FALLBACK;
}

/** Apply a theme WITHOUT storing it — for a live preview in a settings UI. */
export function previewTheme(name) {
    const b = boot();
    return b ? b.preview(name) : FALLBACK;
}

/**
 * Read one theme token as a colour string.
 *
 * This is the seam between the themed page and everything drawn into a
 * canvas. A gauge, a spectrum trace or a waterfall gradient is painted by
 * code, so it cannot inherit anything: the drawing code has to ask for the
 * colour. Asking here rather than hard-coding it is what stops the canvas
 * from being the one part of the screen that stays in the old palette.
 *
 * @param {string} name  token name, with or without the leading `--`,
 *                       e.g. "accent-main", "rwc-accent-main" or
 *                       "--rwc-accent-main"
 * @param {string} [fallback] returned when the token is not defined
 */
export function themeToken(name, fallback = '') {
    if (typeof window === 'undefined' || !name) { return fallback; }
    let prop = name.startsWith('--') ? name : `--${name}`;
    if (!prop.startsWith('--rwc-')) { prop = `--rwc-${prop.slice(2)}`; }
    const value = getComputedStyle(document.documentElement)
        .getPropertyValue(prop)
        .trim();
    return value || fallback;
}

/**
 * Read several tokens at once, as an object keyed by short name.
 * One getComputedStyle per call rather than one per token — worth having
 * when a draw loop wants its whole palette each frame.
 *
 *   const p = themePalette(['ground', 'accent-main', 'alarm']);
 *   ctx.fillStyle = p.ground;
 */
export function themePalette(names) {
    if (typeof window === 'undefined') { return {}; }
    const style = getComputedStyle(document.documentElement);
    const out = {};
    for (const n of names) {
        const short = n.replace(/^--/, '').replace(/^rwc-/, '');
        out[short] = style.getPropertyValue(`--rwc-${short}`).trim();
    }
    return out;
}

/**
 * Call back whenever the theme changes, and once immediately so the caller
 * does not need its own start-up path. Returns a function that stops it.
 *
 * Canvas code should use this: the drawing does not repaint on its own
 * when an attribute changes, so without it a gauge keeps the old palette
 * until the next frame that happened to be scheduled anyway.
 */
export function onThemeChange(handler) {
    if (typeof window === 'undefined' || typeof handler !== 'function') {
        return () => { };
    }
    const root = document.documentElement;
    let last = currentTheme();
    handler(last);

    const observer = new MutationObserver(() => {
        const now = currentTheme();
        if (now !== last) {
            last = now;
            handler(now);
        }
    });
    observer.observe(root, { attributes: true, attributeFilter: ['data-theme'] });
    return () => observer.disconnect();
}
