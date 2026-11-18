// FT8 Tiles (issue #332): the page. Polls GET /api/wsjtx/calls once a second
// and draws the tiles; each tile is one click.
//
// Tile names are what Windows Voice Access and switch software read out, so
// they are short and fixed: "Call 1" to "Call 9", "Stop transmit",
// "Auto Call on", "Auto Call off", "Next page", "Change band", and the band
// names. Saying "click Call 3" clicks the third station tile.

import { TileSlots } from "./ft8-tiles-slots.js";

const PER_PAGE = 9;
const POLL_MS = 1000;

const el = id => document.getElementById(id);
const slots = new TileSlots();
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

    const pill = el("ftState");
    let text = "Receiving", cls = "ft-rx";
    if (!d.connected) { text = "WSJT-Z not connected"; cls = "ft-off"; }
    else if (d.transmitting) { text = "Transmitting"; cls = "ft-tx"; }
    else if (d.txEnabled) { text = "Tx enabled"; cls = "ft-armed"; }
    pill.textContent = text;
    pill.className = `ft-state ${cls}`;
    el("ftChangeBandNow").textContent = band ? `now ${band}` : "";
}

function renderTiles(d) {
    const byCall = new Map(d.stations.map(s => [s.call, s]));
    slots.update(d.stations.map(s => s.call));
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
            lastData = await r.json();
            renderHeader(lastData);
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
    pageIndex = 0;
    if (await post("/api/wsjtx/band", { band: name })) showMessage(`Changing to ${name}`);
}

function openBands() {
    el("ftBands").hidden = false;
    el("ftMain").hidden = true;
}

function closeBands() {
    el("ftBands").hidden = true;
    el("ftMain").hidden = false;
}

function wire() {
    for (let i = 1; i <= PER_PAGE; i++) {
        const tile = el(`ftCall${i}`);
        tile.addEventListener("click", () => callStation(tile));
    }
    el("ftStop").addEventListener("click", async () => {
        if (await post("/api/wsjtx/halt")) showMessage("Transmit stopped");
    });
    el("ftAutoOn").addEventListener("click", async () => {
        if (await post("/api/wsjtx/autocall", { on: true })) showMessage("Auto Call on");
    });
    el("ftAutoOff").addEventListener("click", async () => {
        if (await post("/api/wsjtx/autocall", { on: false })) showMessage("Auto Call off");
    });
    el("ftNextPage").addEventListener("click", () => {
        pageIndex = (pageIndex + 1) % slots.pageCount(PER_PAGE);
        if (lastData) renderTiles(lastData);
    });
    el("ftChangeBand").addEventListener("click", openBands);
    el("ftBandBack").addEventListener("click", closeBands);
}

wire();
poll();
