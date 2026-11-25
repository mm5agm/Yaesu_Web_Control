// FT8 Tiles (issue #332): the page. Polls GET /api/wsjtx/calls once a second
// and draws the tiles; each tile is one click.
//
// Tile names are what Windows Voice Access and switch software read out, so
// they are short and fixed: "Call 1" to "Call 9", "Stop transmit",
// "Auto Call on", "Call CQ", "Next page", "Change band", "Sort",
// "Main page", and the band names. Saying "click Call 3" clicks the third station tile.

import { TileSlots, SORT_MODES, SORT_LABEL, sortCalls, txStatus, callingCq, LastQso } from "./ft8-tiles-slots.js";

const PER_PAGE = 9;
const POLL_MS = 1000;
// How long the "Logged" banner stays up after WSJT-Z logs a QSO.
const LOGGED_SHOW_SECONDS = 30;

const el = id => document.getElementById(id);
const slots = new TileSlots();
const lastQso = new LastQso();
const SORT_KEY = "ywc.ft8Tiles.sort";
let sortMode = SORT_MODES.includes(localStorage.getItem(SORT_KEY)) ? localStorage.getItem(SORT_KEY) : "heard";
let sortPending = sortMode !== "heard";   // sort the first stations to arrive
let pageIndex = 0;
let lastData = null;
let messageTimer = null;

function formatMHz(hz) {
    if (!hz) return "—";
    const mhz = Math.floor(hz / 1e6);
    const khz = Math.floor((hz % 1e6) / 1e3);
    const rest = Math.floor(hz % 1e3);
    return `${mhz}.${String(khz).padStart(3, "0")}.${String(rest).padStart(3, "0")}`;
}

function snrText(snr) {
    return snr > 0 ? `+${snr}` : `${snr}`.replace("-", "−");
}

const KIND_LABEL = { you: "Calling you", cq: "CQ", finished: "Just finished" };

function showMessage(text, isError) {
    const box = el("ftMessage");
    box.textContent = text;
    box.classList.toggle("ft-error", !!isError);
    clearTimeout(messageTimer);
    messageTimer = setTimeout(() => { box.textContent = ""; box.classList.remove("ft-error"); }, 6000);
}

async function post(url, body) {
    try {
        const r = await fetch(url, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body ?? {}),
        });
        const data = await r.json().catch(() => ({}));
        if (!r.ok) {
            showMessage(data.error || `Failed (${r.status})`, true);
            return false;
        }
        return true;
    } catch {
        showMessage("YWC isn't answering", true);
        return false;
    }
}

function renderHeader(d) {
    el("ftFreq").textContent = formatMHz(d.dialFrequency);
    const band = (d.bands.find(b => b.current) || {}).name || "";
    el("ftBandMode").textContent = [band, d.mode].filter(Boolean).join(" · ");
    el("ftCalling").textContent = d.dxCall ? `Working ${d.dxCall}` : "";

    const st = txStatus(d);
    const pill = el("ftState");
    pill.textContent = st.state;
    pill.className = `ft-state ${st.cls}`;
    el("ftStatusLine").className = st.cls;
    setText("ftTxLine", st.line);
    lastQso.update(d);
    setText("ftLastQso", lastQso.text());
    el("ftLastQso").classList.toggle("ft-logged", !!lastQso.last && lastQso.last.logged);

    // The Call CQ tile lights up while CQ is going out. Its name stays
    // "Call CQ" for Voice Access; only the words on it change.
    const cq = callingCq(d);
    el("ftCallCq").classList.toggle("ft-calling-cq", cq);
    setText("ftCallCqBig", cq ? "Calling CQ" : "Call CQ");
    setText("ftCallCqSmall", cq ? "Stop transmit ends it" : "until someone answers");
    el("ftChangeBandNow").textContent = band ? `now ${band}` : "";
}

// Only touch the text when it changes, so a screen reader isn't told it again every second.
function setText(id, text) {
    const e = el(id);
    if (e.textContent !== text) e.textContent = text;
}

// WSJT-Z sends QSO Logged once the QSO is in its log. With "Prompt me to log
// QSO" unticked in WSJT-Z that is straight after the 73, with no window to
// click, so this banner is how the operator knows it went in.
function loggedText(q) {
    if (!q) return "";
    const parts = [`Logged ✓ ${q.call}`];
    if (q.frequency) parts.push(formatMHz(q.frequency));
    if (q.mode) parts.push(q.mode);
    if (q.sent || q.received) parts.push(`sent ${q.sent || "—"} rcvd ${q.received || "—"}`);
    return parts.join(" · ");
}

function renderLogged(d) {
    const box = el("ftLogged");
    const q = d.logged;
    const show = !!q && q.ageSeconds < LOGGED_SHOW_SECONDS;
    const text = show ? loggedText(q) : "";
    if (box.textContent !== text) box.textContent = text;
    box.hidden = !show;
}

function renderTiles(d) {
    const byCall = new Map(d.stations.map(s => [s.call, s]));
    slots.update(d.stations.map(s => s.call));
    if (sortPending && d.stations.length) {
        slots.arrange(sortCalls(d.stations, sortMode));
        sortPending = false;
    }
    const pages = slots.pageCount(PER_PAGE);
    if (pageIndex >= pages) pageIndex = 0;

    const fadeAfter = d.periodSeconds + 3;
    slots.page(pageIndex, PER_PAGE).forEach((call, i) => {
        const tile = el(`ftCall${i + 1}`);
        const s = call ? byCall.get(call) : null;
        tile.dataset.call = s ? s.call : "";
        tile.disabled = !s;
        tile.classList.toggle("ft-empty", !s);
        tile.classList.toggle("ft-faded", !!s && s.ageSeconds > fadeAfter);
        tile.classList.toggle("ft-you", !!s && s.kind === "you");
        tile.classList.toggle("ft-working", !!s && s.call === d.dxCall);
        tile.querySelector(".ft-call").textContent = s ? s.call : "";
        tile.querySelector(".ft-detail").textContent = s
            ? [s.grid, snrText(s.snr)].filter(Boolean).join(" · ")
            : "";
        const badge = tile.querySelector(".ft-badge");
        badge.textContent = s ? KIND_LABEL[s.kind] : "";
        badge.className = `ft-badge ${s ? "ft-badge-" + s.kind : ""}`;
    });

    const total = d.stations.length;
    el("ftSummary").textContent = total === 0
        ? (d.connected ? "waiting for decodes" : "")
        : `${total} to call` + (pages > 1 ? ` · page ${pageIndex + 1} of ${pages}` : "");
    const next = el("ftNextPage");
    next.disabled = pages <= 1;
    el("ftNextPageMore").textContent = pages > 1 ? `page ${pageIndex + 1} of ${pages}` : "";
}

function renderBands(d) {
    const grid = el("ftBandGrid");
    const names = d.bands.map(b => b.name).join("|");
    if (grid.dataset.names !== names) {
        grid.dataset.names = names;
        grid.innerHTML = "";
        for (const b of d.bands) {
            const btn = document.createElement("button");
            btn.type = "button";
            btn.className = "ft-tile ft-band";
            btn.dataset.band = b.name;
            btn.textContent = b.name;
            btn.addEventListener("click", () => chooseBand(b.name));
            grid.appendChild(btn);
        }
    }
    for (const btn of grid.querySelectorAll("button")) {
        const b = d.bands.find(x => x.name === btn.dataset.band);
        btn.classList.toggle("ft-current", !!b && b.current);
    }
}

async function poll() {
    try {
        const r = await fetch("/api/wsjtx/calls", { cache: "no-store" });
        if (r.ok) {
            const data = await r.json();
            showOff(!data.enabled);
            if (!data.enabled) { setTimeout(poll, POLL_MS); return; }
            lastData = data;
            renderHeader(lastData);
            renderLogged(lastData);
            renderTiles(lastData);
            renderBands(lastData);
        }
    } catch { /* YWC restarting: try again next tick */ }
    setTimeout(poll, POLL_MS);
}

async function callStation(tile) {
    const call = tile.dataset.call;
    if (!call) return;
    tile.classList.add("ft-pressed");
    setTimeout(() => tile.classList.remove("ft-pressed"), 400);
    if (await post("/api/wsjtx/reply", { call })) showMessage(`Calling ${call}`);
}

async function chooseBand(name) {
    closeBands();
    slots.clear();
    sortPending = sortMode !== "heard";
    pageIndex = 0;
    if (await post("/api/wsjtx/band", { band: name })) showMessage(`Changing to ${name}`);
}

// Turned off in Settings: hide the tiles and say how to turn it on.
function showOff(off) {
    el("ftOff").hidden = !off;
    document.querySelector(".ft-sub").hidden = off;
    el("ftStatusLine").hidden = off;
    if (off) {
        el("ftMain").hidden = true;
        el("ftBands").hidden = true;
        el("ftState").textContent = "FT8 Tiles off";
        el("ftState").className = "ft-state ft-off";
    } else if (el("ftBands").hidden) {
        el("ftMain").hidden = false;
    }
}

function openBands() {
    el("ftBands").hidden = false;
    el("ftMain").hidden = true;
}

function closeBands() {
    el("ftBands").hidden = true;
    el("ftMain").hidden = false;
}

function showSort() {
    el("ftSortNow").textContent = SORT_LABEL[sortMode];
}

function wire() {
    for (let i = 1; i <= PER_PAGE; i++) {
        const tile = el(`ftCall${i}`);
        tile.addEventListener("click", () => callStation(tile));
    }
    el("ftStop").addEventListener("click", async () => {
        // Stop means stop: halt now, and turn WSJT-Z's Auto CQ and Auto Call
        // off so it doesn't start again next period.
        const halted = await post("/api/wsjtx/halt");
        const autoOff = await post("/api/wsjtx/autocq", { on: false });
        if (halted) showMessage(autoOff ? "Transmit stopped, Auto off" : "Transmit stopped");
    });
    el("ftAutoOn").addEventListener("click", async () => {
        if (await post("/api/wsjtx/autocall", { on: true })) showMessage("Auto Call on");
    });
    el("ftCallCq").addEventListener("click", async () => {
        if (await post("/api/wsjtx/autocq", { on: true })) showMessage("Calling CQ: Stop transmit ends it");
    });
    el("ftNextPage").addEventListener("click", () => {
        pageIndex = (pageIndex + 1) % slots.pageCount(PER_PAGE);
        if (lastData) renderTiles(lastData);
    });
    el("ftChangeBand").addEventListener("click", openBands);
    el("ftSort").addEventListener("click", () => {
        sortMode = SORT_MODES[(SORT_MODES.indexOf(sortMode) + 1) % SORT_MODES.length];
        localStorage.setItem(SORT_KEY, sortMode);
        showSort();
        pageIndex = 0;
        if (lastData) {
            slots.arrange(sortCalls(lastData.stations, sortMode));
            renderTiles(lastData);
        }
        showMessage(`Sorted ${SORT_LABEL[sortMode]}`);
    });
    showSort();
    el("ftBandBack").addEventListener("click", closeBands);
}

wire();
poll();
