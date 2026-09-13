/*
 * theme-boot.js — apply the operator's per-device theme before first paint.
 * Shared by Yaesu Web Control and Icom Web Control.
 *
 * WHY THIS IS A CLASSIC SCRIPT AND NOT A MODULE
 * ---------------------------------------------
 * <script type="module"> is deferred by specification. A deferred script
 * runs after the document has been parsed, which is after the browser has
 * painted — so a module here would show the server's theme first and then
 * swap, producing a white flash on every page load for an operator who has
 * chosen the dark theme on this device. This file is therefore a plain,
 * synchronous, render-blocking script in <head>, and it is kept as small as
 * it is for that reason.
 *
 * WHY THE OVERRIDE IS PER DEVICE
 * ------------------------------
 * The theme is stored in two places on purpose:
 *
 *   ApplicationSettings.Theme  — the operator's default, on the server,
 *                                which follows them to every browser.
 *   localStorage <prefix>.theme — this device's answer, if it differs.
 *
 * The shack PC in a dark room and the tablet on the bench want different
 * answers, and neither should overwrite the other. Both applications
 * already store ten-odd view preferences this way (spectrum span, waterfall
 * brightness, panel open/closed), so this follows the existing pattern
 * rather than inventing one.
 *
 * The server renders its answer onto <html data-theme="..."> before this
 * runs, so with no override there is nothing to do and no flash either way.
 *
 * WHAT data-bs-theme IS FOR
 * -------------------------
 * Bootstrap 5.3 has its own dark mode, switched by data-bs-theme on the
 * root. Some of what it changes cannot be reached from a CSS variable —
 * the chevron on a <select> and the tick on a checkbox are data-URI
 * background images chosen by selector. So a dark theme sets both
 * attributes: Bootstrap's dark substrate, with our palette over it.
 *
 * Usage (in <head>, before any stylesheet that reads the tokens):
 *   <script src="/js/theme/theme-boot.js" data-storage-prefix="ywc"></script>
 */
(function () {
    'use strict';

    // Every theme this file knows how to apply, and whether it is dark.
    // A name that is not in here is ignored rather than applied, so a
    // stale or hand-edited localStorage value can never leave the page in
    // a state no stylesheet styles.
    var THEMES = {
        classic: { dark: false },
        instrument: { dark: true }
    };

    var script = document.currentScript;
    var prefix = (script && script.dataset && script.dataset.storagePrefix) || 'rwc';
    var storageKey = prefix + '.theme';

    function read() {
        // Private browsing, blocked site data and a cleared profile all make
        // localStorage throw rather than return null. The theme is a
        // preference, not state the application needs, so a failure here
        // means "no override" and nothing more.
        try {
            return window.localStorage.getItem(storageKey);
        } catch (e) {
            return null;
        }
    }

    function write(name) {
        try {
            if (name) {
                window.localStorage.setItem(storageKey, name);
            } else {
                window.localStorage.removeItem(storageKey);
            }
            return true;
        } catch (e) {
            return false;
        }
    }

    function apply(name) {
        var theme = THEMES[name];
        if (!theme) { return false; }
        var root = document.documentElement;
        root.setAttribute('data-theme', name);
        if (theme.dark) {
            root.setAttribute('data-bs-theme', 'dark');
        } else {
            root.removeAttribute('data-bs-theme');
        }
        return true;
    }

    // The server's answer, already on the element when this runs.
    var serverDefault = document.documentElement.getAttribute('data-theme') || 'classic';

    // This device's answer, if it has one and it is a theme we know.
    var override = read();
    if (override && THEMES[override]) {
        apply(override);
    } else {
        // Still call apply, so data-bs-theme is correct even when the
        // server rendered only data-theme.
        apply(serverDefault);
    }

    // The API the rest of the page uses. theme.js (an ES module, so it
    // cannot be loaded this early) wraps this rather than reimplementing
    // it, which is why the storage key and the theme list exist in exactly
    // one place despite being needed at two very different moments.
    window.RadioWebControlTheme = {
        names: Object.keys(THEMES),
        storageKey: storageKey,
        serverDefault: serverDefault,

        /** The theme in force right now. */
        current: function () {
            return document.documentElement.getAttribute('data-theme') || 'classic';
        },

        /** This device's override, or null if it is following the server. */
        override: function () {
            var v = read();
            return (v && THEMES[v]) ? v : null;
        },

        isDark: function (name) {
            var t = THEMES[name || this.current()];
            return !!(t && t.dark);
        },

        /**
         * Apply a theme to this device and remember it.
         * Pass null to forget the override and fall back to the server's
         * default. Returns the theme now in force.
         */
        set: function (name) {
            if (name === null || name === undefined || name === '') {
                write(null);
                apply(serverDefault);
            } else if (THEMES[name]) {
                write(name);
                apply(name);
            }
            return this.current();
        },

        /** Preview a theme without storing it — for the Settings page. */
        preview: function (name) {
            apply(name);
            return this.current();
        }
    };
})();
