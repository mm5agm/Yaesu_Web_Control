// panel-windows.js - the radio panel's windows: CW, RTTY, Memories, DX and the rest.
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// A window is a group of panels opened from a launcher key. Every panel can be
// closed (x) or torn off into a window of its own, and a torn-off panel can be
// put back ("INTO <key>"). The launcher key reopens its group and brings back
// anything closed in it; torn-off panels stay where the operator put them.
// Windows are dragged by their title bar and resized from the corner, and
// where each one was, and which were open, is remembered across reloads.
//
// The windows live inside the panel's board, so they scale with it, and every
// position is in board pixels (the 1600-wide design), not screen pixels.
//
// This knows nothing about radios. The app says what the groups and panels are
// and fills each panel's body: slot(groupId, panelId) is that body, and it is
// the same element for the panel's whole life - it is moved between windows,
// never rebuilt - so whatever the app put in it carries on running. onShow
// tells the app when a panel becomes visible or hidden, so it can start and
// stop polling.
//
// A panel marked popout gets a ⇱ key as well, for a browser window of its own
// that can go on another screen - which a torn-off window, being on the board,
// never can. The app opens that window (onPopout) and uses closePanel and
// showPanel to take the panel off the board while it is out, and put it back.

const SAVE_KEY = 'windows';

function h(tag, cls, text) {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (text != null) el.textContent = text;
    return el;
}

/**
 * @param {object} o
 * @param {HTMLElement} o.board      the positioned board the windows sit on
 * @param {HTMLElement} o.launcher   where the launcher keys go
 * @param {Array<{id:string, key?:string, title:string, pos:number[],
 *          panels:Array<{id:string, title:string, w?:number, dpos?:number[], popout?:any}>}>} o.groups
 *        pos is [left, top, width, height] in board pixels. A group with no
 *        key gets no launcher key; the app opens it with open().
 * @param {{ get(k:string, d?:string):string|null, set(k:string, v:string):void }} o.store
 * @param {string} o.prefix
 * @param {(groupId:string, panelId:string, shown:boolean) => void} [o.onShow]
 * @param {(groupId:string, panelId:string) => void} [o.onPopout]  a panel's ⇱ key;
 *        only panels with popout set get one
 */
export function mountPanelWindows({ board, launcher, groups, store, prefix, onShow, onPopout }) {
    const defs = Object.fromEntries(groups.map(g => [g.id, g]));
    const saved = load();
    // open: group window showing; shut: panels closed inside it;
    // det: panels torn off into their own windows; geom: where each window is.
    const st = {
        open: {}, shut: {}, det: {}, geom: saved.geom ?? {},
    };
    for (const g of groups) {
        const ids = g.panels.map(p => p.id);
        st.open[g.id] = !!saved.open?.[g.id];
        st.shut[g.id] = (saved.shut?.[g.id] ?? []).filter(id => ids.includes(id));
        st.det[g.id] = (saved.det?.[g.id] ?? []).filter(id => ids.includes(id));
    }

    let z = 10;
    const shownNow = {};          // `${gid}/${pid}` -> boolean, last reported
    const panels = {};            // `${gid}/${pid}` -> { box, body, detachBtn, closeBtn }
    const wins = {};              // window key -> { frame, title, sub, join, body }
    const subtitles = {};
    const keys = {};

    // ---- launcher keys -------------------------------------------------
    const hint = h('span', 'rp-pop-hint', '');
    const head = h('div', 'rp-pop-head');
    head.append(h('span', 'rp-legend', 'Windows'), hint);
    const grid = h('div', 'rp-pop-keys');
    for (const g of groups) {
        if (!g.key) continue;
        const b = h('button', 'rp-skey');
        b.type = 'button';
        b.dataset.group = g.id;
        const led = h('span', 'rp-led'), sub = h('span', 'rp-sub', '');
        b.append(led, h('span', 'rp-skey-label', g.key), sub);
        b.addEventListener('click', () => open(g.id));
        grid.append(b);
        keys[g.id] = { b, led, sub };
    }
    if (grid.childElementCount) launcher.append(head, grid);

    // ---- panels: built once, moved between windows ---------------------
    for (const g of groups) {
        for (const p of g.panels) {
            const box = h('div', 'rp-wpanel');
            box.dataset.panel = p.id;
            const ph = h('div', 'rp-wpanel-head');
            const detachBtn = h('button', 'rp-wbtn', '⧉');
            detachBtn.type = 'button';
            detachBtn.title = 'Tear this panel off into its own window';
            detachBtn.addEventListener('click', () => detach(g.id, p.id));
            const closeBtn = h('button', 'rp-wbtn rp-wx', '×');
            closeBtn.type = 'button';
            closeBtn.title = 'Close this panel';
            closeBtn.addEventListener('click', () => closePanel(g.id, p.id));
            ph.append(h('span', 'rp-wpanel-title', p.title));
            if (p.popout && onPopout) {
                const popBtn = h('button', 'rp-wbtn', '⇱');
                popBtn.type = 'button';
                popBtn.title = 'Open this panel in a browser window of its own - it can go on another screen';
                popBtn.addEventListener('click', () => onPopout(g.id, p.id));
                ph.append(popBtn);
            }
            ph.append(detachBtn, closeBtn);
            const body = h('div', 'rp-wpanel-body');
            box.append(ph, body);
            panels[g.id + '/' + p.id] = { box, body, detachBtn, closeBtn, def: p };
        }
    }

    // ---- window frames -------------------------------------------------
    function frameFor(wkey, defaultPos) {
        if (wins[wkey]) return wins[wkey];
        const frame = h('div', 'rp-win');
        frame.dataset.win = wkey;
        const bar = h('div', 'rp-win-bar');
        const title = h('span', 'rp-win-title', '');
        const sub = h('span', 'rp-win-sub', '');
        const join = h('button', 'rp-win-join', '');
        join.type = 'button';
        join.title = 'Put this panel back in its group window';
        const x = h('button', 'rp-win-x', '×');
        x.type = 'button';
        x.title = 'Close this window';
        bar.append(title, sub, join, x);
        const body = h('div', 'rp-win-body');
        const grip = h('div', 'rp-win-grip');
        frame.append(bar, body, grip);
        const w = { frame, title, sub, join, x, body };
        place(frame, st.geom[wkey] ?? defaultPos);
        frame.addEventListener('pointerdown', () => raise(frame), true);
        dragWith(bar, frame, wkey, 'move');
        dragWith(grip, frame, wkey, 'size');
        wins[wkey] = w;
        return w;
    }

    // Every window stays wholly on the board: the board clips, so a corner
    // pushed off it would take the resize grip with it.
    function place(frame, [l, t, w, hh]) {
        const bw = board.offsetWidth || 1600, bh = board.offsetHeight || 900;
        w = Math.min(Math.max(200, w), bw); hh = Math.min(Math.max(120, hh), bh);
        l = Math.max(0, Math.min(l, bw - w)); t = Math.max(0, Math.min(t, bh - hh));
        frame.style.left = Math.round(l) + 'px'; frame.style.top = Math.round(t) + 'px';
        frame.style.width = Math.round(w) + 'px'; frame.style.height = Math.round(hh) + 'px';
    }
    const geomOf = frame => ['left', 'top', 'width', 'height'].map(k => parseFloat(frame.style[k]) || 0);

    function raise(frame) {
        if (Number(frame.style.zIndex) !== z) frame.style.zIndex = String(++z);
    }

    // Board pixels per screen pixel: the board is scaled with a CSS transform.
    const ratio = () => board.offsetWidth / (board.getBoundingClientRect().width || 1);

    function dragWith(handle, frame, wkey, how) {
        handle.addEventListener('pointerdown', e => {
            if (e.button !== 0 || e.target.closest('button')) return;
            e.preventDefault();
            const k = ratio();
            const [l0, t0, w0, h0] = geomOf(frame);
            const x0 = e.clientX, y0 = e.clientY;
            const bw = board.offsetWidth, bh = board.offsetHeight;
            const move = ev => {
                const dx = (ev.clientX - x0) * k, dy = (ev.clientY - y0) * k;
                if (how === 'move') place(frame, [l0 + dx, t0 + dy, w0, h0]);
                else place(frame, [l0, t0, Math.min(w0 + dx, bw - l0), Math.min(h0 + dy, bh - t0)]);
            };
            // On the window, not the handle: a fast pull leaves a 16 px grip behind.
            const up = () => {
                window.removeEventListener('pointermove', move);
                window.removeEventListener('pointerup', up);
                window.removeEventListener('pointercancel', up);
                st.geom[wkey] = geomOf(frame);
                save();
            };
            window.addEventListener('pointermove', move);
            window.addEventListener('pointerup', up);
            window.addEventListener('pointercancel', up);
        });
    }

    // ---- state changes -------------------------------------------------
    const all = gid => defs[gid].panels.map(p => p.id);
    const visible = gid => all(gid).filter(p => !st.shut[gid].includes(p) && !st.det[gid].includes(p));

    function open(gid) {
        if (!defs[gid]) return;
        const wasOpen = st.open[gid] && visible(gid).length;
        st.open[gid] = true;
        st.shut[gid] = [];
        render();
        if (wasOpen || wins[gid]) raise(wins[gid].frame);
    }
    function close(gid) {
        if (!defs[gid]) return;
        st.open[gid] = false;
        render();
    }
    function closePanel(gid, pid) {
        if (st.det[gid].includes(pid)) {
            st.det[gid] = st.det[gid].filter(p => p !== pid);
            st.shut[gid] = [...st.shut[gid], pid];
        } else if (visible(gid).filter(p => p !== pid).length) {
            st.shut[gid] = [...st.shut[gid], pid];
        } else {
            st.open[gid] = false;
            st.shut[gid] = [];
        }
        render();
    }
    function showPanel(gid, pid) {
        if (!defs[gid]) return;
        st.shut[gid] = st.shut[gid].filter(p => p !== pid);
        if (!st.det[gid].includes(pid)) st.open[gid] = true;
        render();
        const w = wins[st.det[gid].includes(pid) ? gid + '/' + pid : gid];
        if (w) raise(w.frame);
    }
    function detach(gid, pid) {
        st.det[gid] = [...st.det[gid], pid];
        if (!visible(gid).length) st.open[gid] = false;
        render();
        const w = wins[gid + '/' + pid];
        if (w) raise(w.frame);
    }
    function join(gid, pid) {
        st.det[gid] = st.det[gid].filter(p => p !== pid);
        st.open[gid] = true;
        render();
        raise(wins[gid].frame);
    }

    // ---- render: put every panel where the state says ------------------
    function render() {
        const wanted = new Set();
        for (const g of groups) {
            const many = g.panels.length > 1;
            const shown = g.panels.filter(p => visible(g.id).includes(p.id));
            if (st.open[g.id] && shown.length) {
                const w = frameFor(g.id, g.pos);
                wanted.add(g.id);
                w.title.textContent = g.title;
                w.sub.textContent = subtitles[g.id] ?? '';
                w.join.hidden = true;
                w.x.onclick = () => close(g.id);
                const fixedAll = shown.every(p => p.w);
                const boxes = shown.map((p, i) => {
                    const pe = panels[g.id + '/' + p.id];
                    const grow = shown.length === 1 || !p.w || (fixedAll && i === shown.length - 1);
                    pe.box.style.flex = grow ? '1 1 0' : 'none';
                    pe.box.style.width = grow ? '' : p.w + 'px';
                    pe.detachBtn.hidden = !many;
                    return pe.box;
                });
                syncChildren(w.body, boxes);
            }
            for (const pid of st.det[g.id]) {
                const p = g.panels.find(x => x.id === pid);
                const wkey = g.id + '/' + pid;
                const dpos = p.dpos ?? [g.pos[0] + 60, g.pos[1] + 60, p.w ? p.w + 40 : 520, g.pos[3]];
                const w = frameFor(wkey, dpos);
                wanted.add(wkey);
                w.title.textContent = (g.key || g.title) + ' · ' + p.title;
                w.sub.textContent = '';
                w.join.hidden = false;
                w.join.textContent = '◂ INTO ' + (g.key || g.title);
                w.join.onclick = () => join(g.id, pid);
                w.x.onclick = () => closePanel(g.id, pid);
                const pe = panels[wkey];
                pe.box.style.flex = '1 1 0';
                pe.box.style.width = '';
                pe.detachBtn.hidden = true;
                syncChildren(w.body, [pe.box]);
            }
        }
        for (const [wkey, w] of Object.entries(wins)) {
            if (wanted.has(wkey)) { if (!w.frame.isConnected) { board.append(w.frame); place(w.frame, geomOf(w.frame)); raise(w.frame); } }
            else if (w.frame.isConnected) { w.body.textContent = ''; w.frame.remove(); }
        }
        // Panels in no window are kept out of the document, not destroyed.
        for (const pe of Object.values(panels)) {
            if (!pe.box.parentElement?.isConnected) pe.box.remove();
        }
        for (const g of groups) {
            const total = g.panels.length;
            const showing = (st.open[g.id] ? visible(g.id).length : 0) + st.det[g.id].length;
            for (const p of g.panels) {
                const key = g.id + '/' + p.id;
                const on = (st.open[g.id] && visible(g.id).includes(p.id)) || st.det[g.id].includes(p.id);
                if (!!shownNow[key] !== on) { shownNow[key] = on; onShow?.(g.id, p.id, on); }
            }
            const k = keys[g.id];
            if (k) {
                k.led.classList.toggle('on', showing > 0);
                k.b.classList.toggle('on', showing > 0);
                k.sub.textContent = !showing ? '⧉' : showing < total ? showing + ' of ' + total : 'OPEN';
            }
        }
        save();
    }

    function syncChildren(parent, want) {
        want.forEach((el, i) => { if (parent.children[i] !== el) parent.insertBefore(el, parent.children[i] ?? null); });
        while (parent.children.length > want.length) parent.lastChild.remove();
    }

    function load() {
        try { return JSON.parse(store.get(prefix + SAVE_KEY, '{}') || '{}') ?? {}; } catch { return {}; }
    }
    function save() {
        store.set(prefix + SAVE_KEY, JSON.stringify({ open: st.open, shut: st.shut, det: st.det, geom: st.geom }));
    }

    render();

    return {
        /** The body element of a panel; the app fills it. */
        slot(gid, pid) { return panels[gid + '/' + pid]?.body ?? null; },
        open, close,
        /** Close one panel, as its × does; nothing if it is not showing. */
        closePanel(gid, pid) { if (shownNow[gid + '/' + pid]) closePanel(gid, pid); },
        /** Show one panel again, in its group window or its own torn-off one. */
        showPanel,
        isOpen(gid) { return !!st.open[gid] && visible(gid).length > 0; },
        isShown(gid, pid) { return !!shownNow[gid + '/' + pid]; },
        /** Text beside a group window's title, e.g. which receiver it acts on. */
        setSubtitle(gid, text) {
            subtitles[gid] = text ?? '';
            if (wins[gid]) wins[gid].sub.textContent = subtitles[gid];
        },
        /** Line beside the "Windows" heading over the launcher keys. */
        setHint(text) { hint.textContent = text ?? ''; },
        /** Draw a launcher key's border to say its window suits the current mode. */
        setSuits(gid, on) { keys[gid]?.b.classList.toggle('suits', !!on); },
        /** Put every window back where it starts, and close them all. */
        reset() {
            for (const g of groups) { st.open[g.id] = false; st.shut[g.id] = []; st.det[g.id] = []; }
            st.geom = {};
            render();
            for (const [wkey, w] of Object.entries(wins)) {
                const [gid, pid] = wkey.split('/');
                const g = defs[gid];
                const p = pid && g.panels.find(x => x.id === pid);
                place(w.frame, p ? (p.dpos ?? [g.pos[0] + 60, g.pos[1] + 60, p.w ? p.w + 40 : 520, g.pos[3]]) : g.pos);
            }
        },
    };
}
