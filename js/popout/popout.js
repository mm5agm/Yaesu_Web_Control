// Radio Web Control - pop-out windows
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// Moves a panel out of the main page into a window of its own, which is the
// only way a panel can reach a second monitor: nothing inside a page can leave
// the browser window it is in. The panel's page is the app's business; this
// opens it, remembers where it was put, and keeps the main page and the
// pop-out in step over a BroadcastChannel so the main page knows when the
// panel is out and when it comes back.
//
// Two halves, one per window:
//
//   PopoutHost  - in the main page. Opens the window, or brings it to the
//                 front if it is already open, and reports open/closed and
//                 Reattach to the page.
//   PopoutChild - in the pop-out page. Announces itself, answers the host's
//                 ping, saves its own size and position, and sends Reattach.
//
// The channel is per panel and per origin, so two main-page tabs in the same
// browser both see the one pop-out, and a tablet (another browser) sees
// nothing - that is right: a pop-out is a window on this computer's screen.
//
// Messages, all { type }:
//   host  -> 'ping'      who is there? (sent when the host starts)
//   child -> 'opened'    on load, and in reply to 'ping'
//   child -> 'closed'    on pagehide
//   child -> 'reattach'  the operator wants the panel back in the main page

const CHANNEL_PREFIX = 'rwc-popout-';
const WINDOW_PREFIX  = 'rwc-popout-';
const GEOM_PREFIX    = 'popoutGeom_';

export const MIN_WIDTH  = 320;
export const MIN_HEIGHT = 200;
// Bigger than any real monitor arrangement, small enough that a corrupt value
// cannot ask for a window the browser will refuse outright.
const MAX_EXTENT = 16384;

/**
 * Turn a saved geometry into one that is safe to hand to window.open.
 *
 * Pure, so it can be tested. Size is held to a sane minimum and maximum;
 * position is passed through when it is a real number and dropped when it is
 * not. Position is deliberately NOT clamped to the current screen: the
 * window's place on a second monitor is the whole point, and the screen this
 * page can see is only the one the main window is on. The browser does its
 * own clamping (Chrome and Edge pull a new window onto the opener's monitor
 * unless the page has the Window Management permission).
 *
 * @param {object|null|undefined} saved  { width, height, left, top } from storage
 * @param {{width:number,height:number}} def  default size
 * @returns {{width:number,height:number,left?:number,top?:number}}
 */
export function clampGeometry(saved, def) {
    const size = (v, d, min) => {
        const n = Number(v);
        if (!Number.isFinite(n) || n <= 0) return Math.max(min, Math.round(d));
        return Math.max(min, Math.min(MAX_EXTENT, Math.round(n)));
    };
    const s = (saved && typeof saved === 'object') ? saved : {};
    const out = {
        width:  size(s.width,  def.width,  MIN_WIDTH),
        height: size(s.height, def.height, MIN_HEIGHT),
    };
    const pos = v => {
        if (v === null || v === undefined || v === '') return undefined;
        const n = Number(v);
        return (Number.isFinite(n) && Math.abs(n) <= MAX_EXTENT) ? Math.round(n) : undefined;
    };
    const left = pos(s.left), top = pos(s.top);
    // Half a position is no position: left without top puts the window
    // somewhere nobody chose.
    if (left !== undefined && top !== undefined) { out.left = left; out.top = top; }
    return out;
}

/** The window.open features string for a geometry from clampGeometry. */
export function featuresFor(geom) {
    const parts = ['popup=yes', `width=${geom.width}`, `height=${geom.height}`];
    if (geom.left !== undefined && geom.top !== undefined) {
        parts.push(`left=${geom.left}`, `top=${geom.top}`);
    }
    return parts.join(',');
}

function readGeometry(name) {
    try { return JSON.parse(localStorage.getItem(GEOM_PREFIX + name) || 'null'); }
    catch { return null; }
}

function writeGeometry(name, geom) {
    try { localStorage.setItem(GEOM_PREFIX + name, JSON.stringify(geom)); }
    catch { /* private browsing or storage off: the next open uses the default */ }
}

function makeChannel(name) {
    if (typeof BroadcastChannel === 'undefined') return null;
    try { return new BroadcastChannel(CHANNEL_PREFIX + name); }
    catch { return null; }
}

// ── Second-monitor placement ─────────────────────────────────────────────────
//
// Chrome and Edge pull a new window onto the opener's monitor, whatever left
// and top say, unless the page holds the Window Management permission. With
// it, window.open honours a position on any screen. Firefox has no such API
// and places the window itself, so there it reports 'unsupported' and nothing
// is offered.

const SCREEN_OFFER_DISMISSED = 'popoutScreenOfferDismissed';

/** 'granted' | 'denied' | 'prompt' | 'unsupported' */
export async function screenPermissionState() {
    if (typeof window === 'undefined' || typeof window.getScreenDetails !== 'function') return 'unsupported';
    try {
        const st = await navigator.permissions.query({ name: 'window-management' });
        return st.state;
    } catch {
        return 'unsupported';
    }
}

/**
 * Offer, once, to let the browser reopen pop-outs on the monitor they were
 * left on. Shown only where it would make a difference: a browser that has
 * the permission to ask for, a computer with more than one screen, and an
 * operator who has not already said no. Asking needs a click, so this is a
 * button, never a prompt fired on load.
 *
 * @param {HTMLElement} container  the bar goes at the top of this element
 */
export async function offerScreenPermission(container) {
    if (!container) return;
    if (await screenPermissionState() !== 'prompt') return;
    if (!window.screen?.isExtended) return;
    try { if (localStorage.getItem(SCREEN_OFFER_DISMISSED)) return; } catch { /* ask anyway */ }

    const bar = document.createElement('div');
    bar.className = 'popout-screen-offer';
    bar.setAttribute('role', 'region');
    bar.setAttribute('aria-label', 'Reopen on this monitor');
    const text = document.createElement('span');
    text.textContent = 'To reopen this window on the monitor you leave it on, the browser needs your permission to place windows. ';
    const allow = document.createElement('button');
    allow.type = 'button';
    allow.className = 'btn btn-sm btn-primary ms-2';
    allow.textContent = 'Allow';
    const no = document.createElement('button');
    no.type = 'button';
    no.className = 'btn btn-sm btn-outline-secondary ms-1';
    no.textContent = 'Not now';
    bar.append(text, allow, no);
    container.prepend(bar);

    allow.addEventListener('click', async () => {
        // getScreenDetails() is what raises the browser's own question.
        try { await window.getScreenDetails(); } catch { /* refused: nothing more to do */ }
        bar.remove();
    });
    no.addEventListener('click', () => {
        try { localStorage.setItem(SCREEN_OFFER_DISMISSED, '1'); } catch { /* ignore */ }
        bar.remove();
    });
}

export class PopoutHost {
    /**
     * @param {object} opts
     * @param {string} opts.name   short id, e.g. 'cw-reader'; names the window, channel and storage
     * @param {string} opts.url    the pop-out page
     * @param {{width:number,height:number}} opts.defaultSize  page-area size the first time
     * @param {(open: boolean) => void} [opts.onChange]  the pop-out opened or closed
     * @param {() => void} [opts.onReattach]  the operator asked for the panel back
     */
    constructor({ name, url, defaultSize, onChange, onReattach }) {
        this._name       = name;
        this._url        = url;
        this._default    = defaultSize;
        this._onChange   = onChange   ?? (() => {});
        this._onReattach = onReattach ?? (() => {});
        this._open       = false;
        this._win        = null;
        this._channel    = null;
    }

    get isOpen() { return this._open; }

    /** Start listening, and ask whether a pop-out is already open (a main-page reload). */
    start() {
        this._channel = makeChannel(this._name);
        if (!this._channel) return this;
        this._channel.addEventListener('message', ev => {
            const t = ev.data?.type;
            if (t === 'opened')   this._setOpen(true);
            if (t === 'closed')   this._setOpen(false);
            if (t === 'reattach') { this._setOpen(false); this._onReattach(); }
        });
        this._channel.postMessage({ type: 'ping' });
        return this;
    }

    /**
     * Open the pop-out, or bring it to the front if it is open already.
     * Call from a click: browsers block window.open outside a user gesture.
     * @returns {boolean} false if the browser blocked the window
     */
    open() {
        if (this._open) { this.focus(); return true; }

        const geom = clampGeometry(readGeometry(this._name), this._default);
        // A fixed name, so a second click (or a second main-page tab) finds
        // the same window instead of stacking up copies of it.
        const w = window.open(this._url, WINDOW_PREFIX + this._name, featuresFor(geom));
        if (!w) return false;
        this._win = w;
        try { w.focus(); } catch { /* cross-window focus is best effort */ }
        // The child says 'opened' once it has loaded; marking it open now as
        // well stops a quick second click opening it twice.
        this._setOpen(true);
        return true;
    }

    /** Bring an open pop-out to the front. */
    focus() {
        let w = (this._win && !this._win.closed) ? this._win : null;
        if (!w) {
            // After a main-page reload the reference is gone, but the window
            // is still found by name. An empty URL looks it up without
            // reloading it - which would throw away what it is showing.
            try { w = window.open('', WINDOW_PREFIX + this._name); } catch { w = null; }
            if (!w) return;
            this._win = w;
            // Nothing by that name after all (a 'closed' that never arrived):
            // the lookup made a blank window, so give it the real page.
            try { if (w.location.href === 'about:blank') w.location.href = this._url; } catch { /* other origin: leave it */ }
        }
        try { w.focus(); } catch { /* best effort */ }
    }

    _setOpen(open) {
        if (this._open === open) return;
        this._open = open;
        if (!open) this._win = null;
        this._onChange(open);
    }
}

/**
 * The main page's half of a pop-out panel, wired to its dialog and buttons:
 * the usual case, so each panel does not write it out again.
 *
 * While the pop-out is open the panel's own open button brings that window
 * to the front instead of opening the dialog, and says so; the dialog stays
 * shut so the two are not both running. Reattach in the window, or closing
 * it, gives the button back, and Reattach reopens the dialog here.
 *
 * @param {object} opts
 * @param {string} opts.name, opts.url, opts.defaultSize  as for PopoutHost
 * @param {() => HTMLDialogElement|null} opts.dialog  the in-page panel
 * @param {HTMLElement|null} opts.openButton  the toolbar button that shows the panel
 * @param {HTMLElement|null} opts.popoutButton  the button in the dialog that pops it out
 * @param {() => void} opts.show  opens the in-page panel
 * @param {{text:string,title:string,aria:string}} opts.closedLabel  the open button normally
 * @param {{text:string,title:string,aria:string}} opts.openLabel  the open button while popped out
 * @param {string} [opts.onClass='btn-outline-info'], [opts.offClass='btn-outline-secondary']
 * @returns {{ host: PopoutHost, open: () => void }} open() is what the toolbar button calls
 */
export function attachPopout({
    name, url, defaultSize, dialog, openButton, popoutButton, show,
    closedLabel, openLabel, onClass = 'btn-outline-info', offClass = 'btn-outline-secondary',
}) {
    const label = l => {
        if (!openButton || !l) return;
        openButton.textContent = l.text;
        openButton.title = l.title;
        openButton.setAttribute('aria-label', l.aria);
    };
    const host = new PopoutHost({
        name, url, defaultSize,
        onChange: open => {
            if (open && dialog()?.open) dialog().close();
            label(open ? openLabel : closedLabel);
            openButton?.classList.toggle(onClass, open);
            openButton?.classList.toggle(offClass, !open);
        },
        onReattach: () => { if (!dialog()?.open) show(); },
    }).start();

    popoutButton?.addEventListener('click', () => {
        if (!host.open()) {
            alert('The browser blocked the pop-out window. Allow pop-ups for this address, then try again.');
            return;
        }
        // Focus was on the button inside the dialog that is about to close;
        // leave it somewhere that still exists.
        dialog()?.close();
        openButton?.focus();
    });

    return {
        host,
        open: () => { if (host.isOpen) host.focus(); else show(); },
    };
}

export class PopoutChild {
    /**
     * @param {object} opts
     * @param {string} opts.name         the same id the host uses
     * @param {string} [opts.fallbackUrl] where Reattach goes if this window cannot close itself
     */
    constructor({ name, fallbackUrl = '/' }) {
        this._name     = name;
        this._fallback = fallbackUrl;
        this._channel  = null;
        this._last     = '';
        this._saveTimer = null;
        this._watch    = null;
    }

    start() {
        this._channel = makeChannel(this._name);
        this._channel?.addEventListener('message', ev => {
            if (ev.data?.type === 'ping') this._post('opened');
        });
        this._post('opened');

        // There is no event for a window being moved, only for one being
        // resized, so position is sampled. Every two seconds is often enough:
        // what matters is where it was last left, and pagehide saves that too.
        window.addEventListener('resize', () => {
            clearTimeout(this._saveTimer);
            this._saveTimer = setTimeout(() => this._saveGeometry(), 300);
        });
        this._watch = setInterval(() => this._saveGeometry(), 2000);

        window.addEventListener('pagehide', () => {
            this._saveGeometry();
            this._post('closed');
        });
        // A reload fires pagehide too, so the host briefly thinks the window
        // went; the reloaded page's 'opened' puts that right.
        return this;
    }

    /** Hand the panel back to the main page and close this window. */
    reattach() {
        this._saveGeometry();
        this._post('reattach');
        window.close();
        // A window the operator opened by typing the address, rather than one
        // the main page opened, is not allowed to close itself. Go to the main
        // page instead, so Reattach still does what it says.
        setTimeout(() => { if (!window.closed) window.location.href = this._fallback; }, 300);
    }

    _post(type) {
        try { this._channel?.postMessage({ type }); } catch { /* channel closed during unload */ }
    }

    _saveGeometry() {
        // window.open's width and height are the page area, not the frame,
        // so save the inner size; saving the outer one would grow the window
        // by its title bar every time it was reopened. left and top are the
        // frame's place on the screen, which is what screenX/Y report.
        const g = {
            width:  window.innerWidth,
            height: window.innerHeight,
            left:   window.screenX,
            top:    window.screenY,
        };
        // A minimised window reports nothing useful; keep the last real one.
        if (!g.width || !g.height) return;
        const sig = JSON.stringify(g);
        if (sig === this._last) return;
        this._last = sig;
        writeGeometry(this._name, g);
    }
}
