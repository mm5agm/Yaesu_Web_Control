// Radio Web Control - Spot Map page
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// Who hears me, and who I hear, on a map and in a list, live from PSK Reporter.
// Made for a second monitor: the page is one window, the map on the left and
// the best DX, the band panel and the spot list on the right with a grip
// between them; each can be hidden. Clicking a band shows only that band
// everywhere. A UTC clock sits in the top bar, and the grey line (the night
// side of the Earth) is shaded on the map.
//
//   Live spots: the PSK Reporter MQTT feed (mqtt.pskreporter.info, run by
//   Tom Fanning M0LTE), pushed within seconds of PSK Reporter getting them.
//   Programs such as WSJT-X upload about every 5 minutes, so a spot can be
//   that late; that is normal.
//
//   History: the feed has none, so at start-up the page asks PSK Reporter's
//   query API for the last 24 hours. It allows about one query per 5 minutes
//   from one address: "heard me" straight away, "I heard" 5.5 minutes later.
//
// The callsign and grid come from the app (stationUrl: the WSJT-X/WS Status
// the app hears over UDP), so there is nothing to set up. Without WSJT-X the
// page asks once and remembers the answer in this browser. ?call= and ?grid=
// in the address win over both.
//
// The page needs window.L (Leaflet) and window.mqtt (mqtt.js), loaded by the
// app's page from its own wwwroot/lib, and an empty element to draw into.

import {
    KINDS, SpotStore, gridToLatLon, isCallsign, topicsFor, topicCall, classifyMqtt,
    fromReceptionReport, listRows, utcHhmm, formatDistance, bestDx, utcHhmmss, nightPolygon, subsolarPoint,
} from './spot-data.js';

const COLOURS = { heardMe: '#ff6b3d', iHeard: '#3db4ff' };
const KIND_LABEL = { heardMe: 'Heard me', iHeard: 'I heard' };
const WINDOWS = [[300, '5 minutes'], [600, '10 minutes'], [900, '15 minutes'], [1800, '30 minutes'],
                 [3600, '1 hour'], [10800, '3 hours'], [21600, '6 hours'], [86400, '24 hours']];
const FRESH_SECS = 180;          // spots from the last 3 minutes are drawn bigger
const HISTORY_GAP_MS = 5.5 * 60 * 1000;
// World always shows this much: 80 N clears all of Svalbard (Longyearbyen 78 N, Ny-Alesund 79 N),
// 57 S Cape Horn and South Georgia.
const WORLD_NORTH = 80, WORLD_SOUTH = -57;
// The map repeats sideways, so spots go on the copies either side too.
const COPIES = [-360, 0, 360];
const STATION_POLL_MS = 15000;
const GREY_LINE_MS = 60000;      // the grey line moves a quarter of a degree a minute
const KEY = 'spotmap.';

const CSS = `
.sm-root { position: fixed; inset: 0; display: flex; flex-direction: column; background: #111; color: #eee;
  font: 14px/1.4 "Segoe UI", system-ui, sans-serif; }
.sm-bar { flex: 0 0 auto; display: flex; flex-wrap: wrap; align-items: center; gap: 6px 16px; padding: 6px 12px;
  background: #1b1d22; border-bottom: 1px solid #333; }
.sm-bar h1 { font-size: 17px; margin: 0; font-weight: 600; }
.sm-clock { font-size: 20px; font-weight: 600; font-variant-numeric: tabular-nums; letter-spacing: .5px; color: #fff; }
.sm-clock small { font-size: 12px; font-weight: 400; color: #9aa; margin-left: 4px; letter-spacing: 0; }
.sm-bar select, .sm-bar button { background: #26292f; color: #eee; border: 1px solid #555; border-radius: 4px;
  font: inherit; padding: 2px 6px; }
.sm-bar button { cursor: pointer; }
.sm-bar button[aria-pressed="true"] { background: #2d4f6e; border-color: #3db4ff; }
.sm-bar a { color: #8bd; }
.sm-dot { display: inline-block; width: 11px; height: 11px; border-radius: 50%; margin-right: 5px; vertical-align: -1px; }
.sm-muted { color: #9aa; font-size: 12px; }
.sm-status b { color: #6f6; font-weight: 600; }
.sm-body { flex: 1 1 auto; min-height: 0; display: flex; }
.sm-map { flex: 1 1 auto; min-width: 0; position: relative; background: #111; }
.sm-grip { flex: 0 0 6px; cursor: col-resize; background: #2a2d33; touch-action: none; }
.sm-grip:hover, .sm-grip.sm-dragging { background: #3db4ff; }
.sm-side { flex: 0 0 auto; width: 420px; min-width: 200px; display: flex; flex-direction: column; background: #16181c; }
.sm-list { flex: 1 1 auto; min-height: 0; overflow: auto; }
/* No shrinking: a long spot list would squeeze the band panel to nothing. */
.sm-bands { flex: 0 0 auto; max-height: 45%; overflow: auto; border-bottom: 1px solid #444; }
.sm-side.sm-nolist .sm-bands { max-height: none; flex: 1 1 auto; border-bottom: 0; }
.sm-best { flex: 0 0 auto; border-bottom: 1px solid #444; }
.sm-side[hidden], .sm-list[hidden], .sm-bands[hidden], .sm-best[hidden], .sm-grip[hidden] { display: none; }
.sm-bestrow { display: grid; grid-template-columns: 5.6em 1fr auto auto; gap: 0 10px; align-items: baseline; width: 100%;
  padding: 4px 8px; background: none; color: #eee; border: 0; border-bottom: 1px solid #23262b; font: inherit;
  text-align: left; cursor: pointer; font-variant-numeric: tabular-nums; }
.sm-bestrow:hover { background: #24303d; }
.sm-bestrow:focus-visible { outline: 2px solid #3db4ff; outline-offset: -2px; }
.sm-bestrow:disabled { cursor: default; background: none; }
.sm-bestrow .sm-bcall { font-weight: 600; font-size: 15px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sm-bestrow .sm-bkm { font-weight: 600; text-align: right; }
.sm-bestrow .sm-muted { white-space: nowrap; }
.sm-sun { color: #ffd23d; font-size: 22px; line-height: 22px; text-align: center; text-shadow: 0 0 6px #000; }
.sm-bands-head { position: sticky; top: 0; z-index: 1; display: flex; align-items: center; gap: 8px; padding: 5px 8px;
  background: #22252b; border-bottom: 1px solid #444; font-weight: 600; }
.sm-bands-head .sm-muted { font-weight: 400; flex: 1 1 auto; }
.sm-bands-head button { background: #26292f; color: #eee; border: 1px solid #555; border-radius: 4px; font: inherit;
  font-weight: 400; font-size: 12px; padding: 1px 6px; cursor: pointer; }
.sm-bands-head button[hidden] { display: none; }
.sm-bands table { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums; }
.sm-bands th { text-align: left; font-weight: 400; color: #9aa; font-size: 12px; padding: 3px 8px 1px; }
.sm-bands td { padding: 3px 8px; border-bottom: 1px solid #23262b; white-space: nowrap; }
.sm-bands td:first-child { width: 4.5em; font-weight: 600; }
.sm-bands tbody tr { cursor: pointer; }
.sm-bands tbody tr:hover { background: #24303d; }
.sm-bands tbody tr[aria-pressed="true"] { background: #2d4f6e; }
.sm-bands tbody tr:focus-visible { outline: 2px solid #3db4ff; outline-offset: -2px; }
.sm-bcell { display: flex; align-items: center; gap: 6px; }
.sm-track { flex: 1 1 auto; height: 8px; }
.sm-bfill { display: block; height: 100%; min-width: 2px; border-radius: 0 4px 4px 0; }
.sm-count { flex: 0 0 2.6em; text-align: right; color: #eee; }
.sm-dim { opacity: .35; }
.sm-list table { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums; }
.sm-list th { position: sticky; top: 0; background: #22252b; text-align: left; font-weight: 600; padding: 5px 8px;
  cursor: pointer; user-select: none; white-space: nowrap; border-bottom: 1px solid #444; }
.sm-list th[aria-sort="ascending"]::after { content: " \\25B2"; font-size: 10px; }
.sm-list th[aria-sort="descending"]::after { content: " \\25BC"; font-size: 10px; }
.sm-list td { padding: 3px 8px; border-bottom: 1px solid #23262b; white-space: nowrap; }
.sm-list td.sm-num, .sm-list th.sm-num { text-align: right; }
.sm-list tbody tr { cursor: pointer; }
.sm-list tbody tr:hover { background: #24303d; }
.sm-list tbody tr.sm-fresh td { font-weight: 600; color: #fff; }
.sm-list tbody tr.sm-old td { color: #aab; }
.sm-empty { padding: 14px; color: #9aa; }
.sm-empty { position: absolute; top: 12px; left: 50%; transform: translateX(-50%); z-index: 600;
  width: max-content; max-width: min(760px, calc(100% - 120px)); padding: 10px 14px; border: 1px solid #555;
  border-radius: 6px; background: rgba(22, 24, 28, .93); color: #eee; font-size: 14px; line-height: 1.45; }
.sm-empty[hidden] { display: none; }
.sm-credit { position: absolute; bottom: 18px; left: 10px; z-index: 500; color: #889; font-size: 12px;
  background: rgba(17,17,17,.6); padding: 1px 4px; border-radius: 3px; pointer-events: none; }
.sm-setup { position: fixed; inset: 0; z-index: 2000; background: rgba(0,0,0,.75); display: flex;
  align-items: center; justify-content: center; }
.sm-setup[hidden] { display: none; }
.sm-setup form { background: #1c1c1c; border: 1px solid #444; border-radius: 8px; padding: 20px 24px; max-width: 440px; }
.sm-setup h2 { margin: 0 0 8px; font-size: 20px; }
.sm-setup label { display: block; margin: 10px 0; }
.sm-setup input { width: 100%; box-sizing: border-box; padding: 6px 8px; font-size: 16px; text-transform: uppercase;
  background: #111; color: #fff; border: 1px solid #555; border-radius: 4px; }
.sm-setup input::placeholder { text-transform: none; }
.sm-setup button { margin-top: 6px; padding: 7px 16px; font-size: 15px; background: #3db4ff; color: #000;
  border: 0; border-radius: 4px; cursor: pointer; }
.sm-setup .sm-error { color: #ff8a80; min-height: 1.2em; margin: 4px 0 0; }
@media (max-width: 760px) {
  .sm-body { flex-direction: column; }
  .sm-grip { display: none; }
  .sm-side { width: auto !important; height: 40%; flex: 0 0 40%; }
}`;

const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

function load(name, fallback) {
    try { const v = localStorage.getItem(KEY + name); return v === null ? fallback : JSON.parse(v); } catch { return fallback; }
}
function save(name, value) {
    try { localStorage.setItem(KEY + name, JSON.stringify(value)); } catch { /* private mode: not remembered */ }
}

function ago(secs) {
    const d = Math.max(0, Date.now() / 1000 - secs);
    if (d < 90) return 'just now';
    if (d < 3600) return Math.round(d / 60) + ' min ago';
    return (d / 3600).toFixed(1) + ' h ago';
}

async function fetchStation(url) {
    if (!url) return null;
    try {
        const r = await fetch(url, { cache: 'no-store' });
        if (!r.ok) return null;
        const j = await r.json();
        const call = String(j.call ?? '').trim().toUpperCase();
        return isCallsign(call) ? { call, grid: String(j.grid ?? '').trim().toUpperCase() } : null;
    } catch { return null; }
}

/**
 * Draws the Spot Map into root.
 * options.app      short app name for the MQTT client id ('ywc', 'iwc')
 * options.stationUrl  GET endpoint returning { call, grid } from WSJT-X, or ''
 * options.programName what to call the station source in messages ('WSJT-X')
 */
export async function startSpotMap(root, { app = 'rwc', stationUrl = '', programName = 'WSJT-X' } = {}) {
    const L = window.L, mqtt = window.mqtt;
    const style = document.createElement('style');
    style.textContent = CSS;
    document.head.append(style);

    root.className = 'sm-root';
    root.innerHTML = `
      <div class="sm-bar">
        <h1 data-sm="title">Spot Map</h1>
        <span class="sm-clock" data-sm="clock" title="Time now, UTC"></span>
        <span><span class="sm-dot" style="background:${COLOURS.heardMe}"></span>Heard me: <b data-sm="nheardMe">0</b></span>
        <span><span class="sm-dot" style="background:${COLOURS.iHeard}"></span>I heard: <b data-sm="niHeard">0</b></span>
        <label>Show <select data-sm="kinds" aria-label="Which spots to show">
          <option value="both">Both</option><option value="heardMe">Heard me</option><option value="iHeard">I heard</option>
        </select></label>
        <label>Last <select data-sm="window" aria-label="How far back to show">
          ${WINDOWS.map(([v, t]) => `<option value="${v}">${t}</option>`).join('')}
        </select></label>
        <label><select data-sm="units" aria-label="Distance units"><option value="km">km</option><option value="mi">miles</option></select></label>
        <button type="button" data-sm="worldBtn" title="Fit one whole world across the window, centred on you">World</button>
        <button type="button" data-sm="greyBtn" aria-pressed="true" title="Show or hide the grey line: the night side of the Earth, shaded">Grey line</button>
        <button type="button" data-sm="bestBtn" aria-pressed="true" title="Show or hide the furthest station each way">Best DX</button>
        <button type="button" data-sm="bandsBtn" aria-pressed="true" title="Show or hide the stations per band">Bands</button>
        <button type="button" data-sm="listBtn" aria-pressed="true" title="Show or hide the spot list">List</button>
        <span class="sm-status sm-muted" data-sm="live">Live feed: connecting…</span>
        <span class="sm-muted" data-sm="history"></span>
        <span class="sm-muted"><span data-sm="source"></span> <a href="#" data-sm="change">Change callsign</a></span>
      </div>
      <div class="sm-body">
        <div class="sm-map" data-sm="map">
          <div class="sm-empty" data-sm="empty" role="status" hidden></div>
          <div class="sm-credit">Data from PSK Reporter by Philip Gladstone. Live spots from mqtt.pskreporter.info by Tom Fanning M0LTE.</div>
        </div>
        <div class="sm-grip" data-sm="grip" role="separator" aria-orientation="vertical" aria-label="Drag to resize the side panel" tabindex="0"></div>
        <div class="sm-side" data-sm="side">
        <div class="sm-best" data-sm="best" aria-label="Best DX">
          <div class="sm-bands-head">Best DX <span class="sm-muted" data-sm="bestNote"></span></div>
          <div data-sm="bestRows"></div>
        </div>
        <div class="sm-bands" data-sm="bands" aria-label="Stations per band">
          <div class="sm-bands-head">Bands <span class="sm-muted" data-sm="bandsNote"></span>
            <button type="button" data-sm="allBands" hidden>All bands</button></div>
          <table>
            <thead><tr><th scope="col">Band</th>
              <th scope="col"><span class="sm-dot" style="background:${COLOURS.heardMe}"></span>Heard me</th>
              <th scope="col"><span class="sm-dot" style="background:${COLOURS.iHeard}"></span>I heard</th></tr></thead>
            <tbody data-sm="bandRows"></tbody>
          </table>
          <div class="sm-empty" data-sm="bandsEmpty">No spots yet.</div>
        </div>
        <div class="sm-list" data-sm="list" aria-label="Spot list">
          <table>
            <thead><tr>
              <th data-sort="call" scope="col">Who</th>
              <th data-sort="band" scope="col">Band</th>
              <th data-sort="snr" scope="col" class="sm-num">SNR</th>
              <th data-sort="distance" scope="col" class="sm-num">Distance</th>
              <th data-sort="time" scope="col" class="sm-num">Time UTC</th>
            </tr></thead>
            <tbody data-sm="rows"></tbody>
          </table>
          <div class="sm-empty" data-sm="empty">No spots yet.</div>
        </div>
        </div>
      </div>
      <div class="sm-setup" data-sm="setup" hidden>
        <form data-sm="setupForm">
          <h2>Where have I been heard?</h2>
          <p>A live map of who hears you, and who you hear, on FT8, FT4, CW, RTTY and more, from PSK Reporter.
             Your program (WSJT-X, WS, JTDX, fldigi…) needs PSK Reporter spotting turned on.</p>
          <p class="sm-muted">With ${esc(programName)} running, the callsign and grid are filled in by themselves.</p>
          <label>Your callsign<br><input data-sm="setupCall" required autocomplete="off" spellcheck="false" placeholder="e.g. MM5AGM"></label>
          <label>Your grid square (optional)<br><input data-sm="setupGrid" autocomplete="off" spellcheck="false" placeholder="e.g. IO85" maxlength="6"></label>
          <button type="submit">Show my map</button>
          <p class="sm-error" data-sm="setupError" role="alert"></p>
          <p class="sm-muted">Saved in this browser only. Nothing is sent anywhere except to PSK Reporter.</p>
        </form>
      </div>`;
    const $ = name => root.querySelector(`[data-sm="${name}"]`);

    // ---- Settings remembered in this browser ----
    const windowSel = $('window');
    windowSel.value = String(load('window', 86400));
    if (!windowSel.value) windowSel.value = '86400';
    const kindsSel = $('kinds');
    kindsSel.value = load('kinds', 'both');
    if (!kindsSel.value) kindsSel.value = 'both';
    const unitsSel = $('units');
    unitsSel.value = load('units', 'km') === 'mi' ? 'mi' : 'km';
    let sort = load('sort', { key: 'time', desc: true });
    const list = $('list'), grip = $('grip'), listBtn = $('listBtn');
    const side = $('side'), bands = $('bands'), bandsBtn = $('bandsBtn');
    const best = $('best'), bestBtn = $('bestBtn'), greyBtn = $('greyBtn');
    side.style.width = Math.max(200, Number(load('listWidth', 420)) || 420) + 'px';
    // The side panel holds the best DX and the band panel above the spot list; it goes only when all are hidden.
    const layoutSide = () => {
        list.hidden = listBtn.getAttribute('aria-pressed') !== 'true';
        bands.hidden = bandsBtn.getAttribute('aria-pressed') !== 'true';
        best.hidden = bestBtn.getAttribute('aria-pressed') !== 'true';
        side.hidden = grip.hidden = list.hidden && bands.hidden && best.hidden;
        side.classList.toggle('sm-nolist', list.hidden);
        map?.invalidateSize();
    };
    const showList = on => { listBtn.setAttribute('aria-pressed', String(on)); save('listShown', on); layoutSide(); };
    const showBands = on => { bandsBtn.setAttribute('aria-pressed', String(on)); save('bandsShown', on); layoutSide(); };
    const showBest = on => { bestBtn.setAttribute('aria-pressed', String(on)); save('bestShown', on); layoutSide(); };

    // ---- UTC clock: runs from the start, even before there is a callsign ----
    const clock = $('clock');
    const tick = () => { clock.innerHTML = `${utcHhmmss(Date.now() / 1000)}<small>UTC</small>`; };
    tick();
    // Lined up on the second, so it turns over with the PC's clock.
    setTimeout(() => { tick(); setInterval(tick, 1000); }, 1000 - Date.now() % 1000);
    // A band picked in the band panel: the map, the list and the counts show only it. Not remembered.
    let bandFilter = '';

    // ---- Who the operator is ----
    const params = new URLSearchParams(location.search);
    const saved = load('station', {}) || {};
    let station = null, source = '';
    if (isCallsign(params.get('call'))) {
        station = { call: params.get('call').trim().toUpperCase(), grid: (params.get('grid') || '').trim().toUpperCase() };
        source = 'from the address';
    } else {
        const fromApp = await fetchStation(stationUrl);
        if (fromApp) { station = fromApp; source = `from ${programName}`; }
        else if (isCallsign(saved.call)) { station = { call: saved.call, grid: saved.grid || '' }; source = 'typed in'; }
    }

    const setup = $('setup');
    const showSetup = () => {
        $('setupCall').value = station?.call ?? saved.call ?? '';
        $('setupGrid').value = station?.grid ?? saved.grid ?? '';
        setup.hidden = false;
        $('setupCall').focus();
    };
    $('setupForm').addEventListener('submit', e => {
        e.preventDefault();
        const call = $('setupCall').value.trim().toUpperCase().replace(/\s+/g, '');
        const grid = $('setupGrid').value.trim().toUpperCase();
        if (!isCallsign(call)) { $('setupError').textContent = 'Please enter a valid callsign.'; return; }
        if (grid && !gridToLatLon(grid)) { $('setupError').textContent = 'That grid square doesn\'t look right (e.g. IO85 or IO85cx).'; return; }
        save('station', { call, grid });
        // Drop any ?call= so the saved one is used.
        location.href = location.pathname;
    });
    $('change').addEventListener('click', e => { e.preventDefault(); showSetup(); });

    // The station from the app can turn up later (WSJT-X started after the
    // page) or change (another WSJT-X configuration): follow it.
    if (stationUrl && source !== 'from the address') {
        setInterval(async () => {
            const s = await fetchStation(stationUrl);
            if (!s) return;
            if (!station || s.call !== station.call) { location.reload(); return; }
            if (s.grid && s.grid !== station.grid && gridToLatLon(s.grid)) {
                station.grid = s.grid;
                home = gridToLatLon(s.grid);
                homeMarker?.remove();
                homeMarker = null;
                draw();
            }
        }, STATION_POLL_MS);
    }

    if (!station) {
        $('source').textContent = `No callsign yet: start ${programName}, or type it in.`;
        showSetup();
        return;
    }
    const CALL = station.call;
    $('title').textContent = `${CALL} spots`;
    document.title = `${CALL} Spot Map`;
    $('source').textContent = `Callsign ${source}.`;

    // ---- Map ----
    // zoomSnap 0: any zoom, not only whole steps, so one world can fill the window exactly.
    const map = L.map($('map'), { worldCopyJump: true, zoomSnap: 0, zoomDelta: 0.5, wheelPxPerZoomLevel: 120, minZoom: 1 })
        .setView([20, 0], 2);
    // Esri basemaps need no API key.
    const esri = 'https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/';
    L.tileLayer(esri + 'World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}', {
        attribution: 'Tiles &copy; Esri | spots: PSK Reporter', maxZoom: 16,
    }).addTo(map);
    L.tileLayer(esri + 'World_Dark_Gray_Reference/MapServer/tile/{z}/{y}/{x}', { maxZoom: 16 }).addTo(map);
    // The grey line goes in its own pane under the spots, so it never covers a marker.
    map.createPane('smNight').style.zIndex = 350;
    const night = L.layerGroup();
    const sunIcon = L.divIcon({ className: 'sm-sun', html: '\u2600', iconSize: [22, 22] });
    function drawGreyLine() {
        night.clearLayers();
        const now = Date.now();
        const poly = nightPolygon(now, 2);
        const line = poly.slice(0, -2);
        const sun = subsolarPoint(now);
        // Three copies side by side, so it is still there after panning past 180 degrees.
        for (const shift of [-360, 0, 360]) {
            const move = pts => pts.map(([la, lo]) => [la, lo + shift]);
            L.polygon(move(poly), { pane: 'smNight', stroke: false, fillColor: '#000', fillOpacity: 0.55, interactive: false })
                .addTo(night);
            L.polyline(move(line), { pane: 'smNight', color: '#ffd23d', weight: 1.5, opacity: 0.6, interactive: false })
                .addTo(night);
            L.marker([sun[0], sun[1] + shift], { icon: sunIcon, pane: 'smNight', keyboard: false })
                .bindTooltip(`Sun overhead at ${utcHhmm(now / 1000)} UTC`).addTo(night);
        }
    }
    const showGrey = on => {
        greyBtn.setAttribute('aria-pressed', String(on));
        save('greyShown', on);
        if (on) { drawGreyLine(); night.addTo(map); } else night.remove();
    };
    showGrey(load('greyShown', true) !== false);
    greyBtn.addEventListener('click', () => showGrey(!map.hasLayer(night)));
    setInterval(() => { if (map.hasLayer(night)) drawGreyLine(); }, GREY_LINE_MS);
    const layers = { heardMe: L.layerGroup().addTo(map), iHeard: L.layerGroup().addTo(map) };
    const markers = new Map();   // kind:call -> marker, for a click in the list
    const store = new SpotStore();
    let home = gridToLatLon(station.grid);
    let homeMarker = null;

    // ---- The view: the whole world, or where you left it ----
    let worldMode = false, worldLon = null, settingView = false;
    const setViewQuietly = (centre, zoom) => {
        settingView = true;
        try { map.setView(centre, zoom, { animate: false }); } finally { settingView = false; }
    };
    function fitWorld() {
        worldMode = true;
        save('view', { world: true });
        const size = map.getSize();
        if (!size.x || !size.y) return;          // not laid out yet: the resize watcher calls again
        // One world across the window, unless that is taller than the window and would
        // cut off land between WORLD_NORTH and WORLD_SOUTH (Svalbard to Cape Horn). Then
        // zoom out until that fits; the map repeats at the sides, spots and all.
        const band = lat => map.project([lat, 0], 0).y;
        const bandPx = band(WORLD_SOUTH) - band(WORLD_NORTH);   // at zoom 0
        const zoom = Math.min(Math.log2(size.x / 256), Math.log2(size.y / bandPx));
        worldLon = home ? home[1] : 0;
        const x = map.project([0, worldLon], zoom).x;
        const mid = (map.project([WORLD_NORTH, 0], zoom).y + map.project([WORLD_SOUTH, 0], zoom).y) / 2;
        const worldPx = 256 * Math.pow(2, zoom);
        const y = Math.min(Math.max(mid, size.y / 2), worldPx - size.y / 2);
        setViewQuietly(map.unproject([x, y], zoom), zoom);
    }
    const view = load('view', null);
    if (view && !view.world && Number.isFinite(view.zoom) && Number.isFinite(view.lat) && Number.isFinite(view.lng)) {
        setViewQuietly([view.lat, view.lng], view.zoom);
    } else {
        fitWorld();
    }
    // Pan or zoom by hand and the page remembers that view instead.
    map.on('dragstart zoomstart', () => { if (!settingView) worldMode = false; });
    map.on('moveend', () => {
        if (settingView || worldMode) return;
        const c = map.getCenter();
        save('view', { lat: c.lat, lng: c.lng, zoom: map.getZoom() });
    });
    $('worldBtn').addEventListener('click', fitWorld);

    showList(load('listShown', true) !== false);
    showBands(load('bandsShown', true) !== false);
    showBest(load('bestShown', true) !== false);
    listBtn.addEventListener('click', () => showList(list.hidden));
    bandsBtn.addEventListener('click', () => showBands(bands.hidden));
    bestBtn.addEventListener('click', () => showBest(best.hidden));

    // ---- Grip between map and list ----
    grip.addEventListener('pointerdown', e => {
        e.preventDefault();
        grip.setPointerCapture(e.pointerId);
        grip.classList.add('sm-dragging');
        const body = root.querySelector('.sm-body');
        const move = ev => {
            const right = body.getBoundingClientRect().right;
            const w = Math.min(Math.max(200, right - ev.clientX), body.clientWidth * 0.8);
            side.style.width = Math.round(w) + 'px';
            map.invalidateSize();
        };
        const up = () => {
            grip.classList.remove('sm-dragging');
            grip.removeEventListener('pointermove', move);
            grip.removeEventListener('pointerup', up);
            grip.removeEventListener('pointercancel', up);
            save('listWidth', parseInt(side.style.width, 10));
        };
        grip.addEventListener('pointermove', move);
        grip.addEventListener('pointerup', up);
        grip.addEventListener('pointercancel', up);
    });
    grip.addEventListener('keydown', e => {
        if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
        e.preventDefault();
        const w = parseInt(side.style.width, 10) + (e.key === 'ArrowLeft' ? 30 : -30);
        side.style.width = Math.max(200, w) + 'px';
        save('listWidth', parseInt(side.style.width, 10));
        map.invalidateSize();
    });
    new ResizeObserver(() => { map.invalidateSize(); if (worldMode) fitWorld(); }).observe($('map'));

    // ---- Drawing ----
    const kindsShown = () => kindsSel.value === 'both' ? KINDS : [kindsSel.value];
    let drawPending = false;
    const scheduleDraw = () => {
        if (drawPending) return;
        drawPending = true;
        setTimeout(() => { drawPending = false; draw(); }, 300);
    };

    // ---- "No spots yet" notice ----
    // Shown once the history has been asked for and nothing at all is on the
    // map, so an empty map is explained instead of looking broken. PSK Reporter
    // can know a callsign was heard (its search answer) yet send no reports.
    const empty = { asked: 0, heardAt: {} };
    function showEmpty(now) {
        const el = $('empty');
        if (!empty.asked || store.within(now, 86400, KINDS).length) { el.hidden = true; return; }
        const hhmm = s => new Date(s * 1000).toISOString().slice(11, 16);
        const known = KINDS.map(k => empty.heardAt[k]).filter(t => t && now - t < 86400);
        let html = `<b>No spots yet for ${esc(CALL)}.</b> `;
        html += known.length
            ? `PSK Reporter says ${esc(CALL)} was last spotted at ${hhmm(Math.max(...known))} UTC, but it sent no reports for the last 24 hours. Its history service is probably running behind. `
            : 'PSK Reporter has nothing for the last 24 hours. ';
        html += `New spots appear here as soon as they arrive: <b>Heard me</b> when someone hears you transmit, `
            + `and <b>I heard</b> when ${esc(programName)} reports what you decode to PSK Reporter `
            + `(${esc(programName)} Settings, Reporting tab: tick Enable PSK Reporter Spotting).`;
        el.innerHTML = html;
        el.hidden = false;
    }

    function draw() {
        const now = Date.now() / 1000;
        const windowSecs = Number(windowSel.value);
        if (!home && store.gridFromSpots) home = gridToLatLon(store.gridFromSpots);
        if (home && !homeMarker) {
            homeMarker = L.layerGroup(COPIES.map(shift =>
                L.circleMarker([home[0], home[1] + shift], { radius: 9, color: '#fff', weight: 2, fillColor: '#ffd23d', fillOpacity: 1 })
                    .bindTooltip(esc(CALL)))).addTo(map);
        }
        // Home found after the start: centre the whole-world view on it.
        if (worldMode && home && worldLon !== home[1]) fitWorld();
        showEmpty(now);
        const shown = kindsShown();
        markers.clear();
        for (const kind of KINDS) {
            layers[kind].clearLayers();
            const spots = store.within(now, windowSecs, [kind], bandFilter);
            $('n' + kind).textContent = spots.length;
            if (!shown.includes(kind)) continue;
            for (const s of spots) {
                const ll = gridToLatLon(s.grid);
                const fresh = now - s.t < FRESH_SECS;
                const line = kind === 'heardMe' ? `${s.call} heard ${CALL}` : `${CALL} heard ${s.call}`;
                const snr = s.snr !== null ? ` · ${s.snr} dB` : '';
                const tip = `<b>${esc(line)}</b><br>${esc(s.band)} ${esc(s.mode)}${snr}<br>${esc(s.grid)} · ${ago(s.t)}`;
                for (const shift of COPIES) {
                    const at = [ll[0], ll[1] + shift];
                    if (home) {
                        L.polyline([[home[0], home[1] + shift], at], { color: COLOURS[kind], weight: fresh ? 2 : 1, opacity: fresh ? 0.8 : 0.3 })
                            .addTo(layers[kind]);
                    }
                    const m = L.circleMarker(at, {
                        radius: fresh ? 11 : 7, color: fresh ? '#fff' : '#111', weight: fresh ? 2 : 1,
                        fillColor: COLOURS[kind], fillOpacity: 0.9,
                    }).bindTooltip(tip).addTo(layers[kind]);
                    if (shift === 0) markers.set(kind + ':' + s.call, m);
                }
            }
        }
        drawList(now, windowSecs, shown);
        drawBands(now, windowSecs, shown);
        drawBest(now, windowSecs, shown);
    }

    function drawBest(now, windowSecs, shown) {
        const top = bestDx(store.within(now, windowSecs, KINDS, bandFilter), home);
        const miles = unitsSel.value === 'mi';
        const windowText = WINDOWS.find(([v]) => v === windowSecs)?.[1] ?? '';
        $('bestNote').textContent = (bandFilter ? `on ${bandFilter}, ` : '') + `last ${windowText}`;
        $('bestRows').innerHTML = KINDS.map(kind => {
            const s = top[kind];
            const dot = `<span><span class="sm-dot" style="background:${COLOURS[kind]}"></span>${KIND_LABEL[kind]}</span>`;
            const dim = shown.includes(kind) ? '' : ' sm-dim';
            if (!s) {
                const why = home ? 'none yet' : 'needs your grid square';
                return `<button type="button" class="sm-bestrow${dim}" disabled>${dot}<span class="sm-muted">${why}</span></button>`;
            }
            const what = kind === 'heardMe' ? `${s.call} heard ${CALL}` : `${CALL} heard ${s.call}`;
            const snr = s.snr !== null ? `, ${s.snr} dB` : '';
            return `<button type="button" class="sm-bestrow${dim}" data-key="${esc(kind + ':' + s.call)}"`
                + ` title="${esc(`${what} on ${s.band} ${s.mode}${snr}, ${s.grid}, ${ago(s.t)}. Click to find it on the map`)}">`
                + `${dot}<span class="sm-bcall">${esc(s.call)}</span>`
                + `<span class="sm-muted">${esc(s.band)} ${utcHhmm(s.t)}</span>`
                + `<span class="sm-bkm">${formatDistance(s.km, miles)}</span></button>`;
        }).join('');
    }

    function drawBands(now, windowSecs, shown) {
        const rows = store.bandCounts(now, windowSecs);
        // A picked band with nothing left in the window keeps its row, so it can be seen and cleared.
        if (bandFilter && !rows.some(r => r.band === bandFilter)) rows.push({ band: bandFilter, heardMe: 0, iHeard: 0 });
        const max = Math.max(1, ...rows.map(r => Math.max(r.heardMe, r.iHeard)));
        const windowText = WINDOWS.find(([v]) => v === windowSecs)?.[1] ?? '';
        const cell = (r, kind) => {
            const n = r[kind];
            const what = kind === 'heardMe' ? `${n} station${n === 1 ? '' : 's'} heard ${CALL} on ${r.band}`
                                            : `${CALL} heard ${n} station${n === 1 ? '' : 's'} on ${r.band}`;
            const fill = n ? `<span class="sm-bfill" style="width:${(100 * n / max).toFixed(1)}%;background:${COLOURS[kind]}"></span>` : '';
            return `<td class="${shown.includes(kind) ? '' : 'sm-dim'}" title="${esc(what + ', last ' + windowText)}">`
                + `<div class="sm-bcell"><span class="sm-track">${fill}</span><span class="sm-count">${n}</span></div></td>`;
        };
        $('bandRows').innerHTML = rows.map(r => {
            const on = r.band === bandFilter;
            return `<tr role="button" tabindex="0" aria-pressed="${on}" data-band="${esc(r.band)}"`
                + ` aria-label="${esc(r.band)}: heard me ${r.heardMe}, I heard ${r.iHeard}. ${on ? 'Click to show all bands' : 'Click to show only this band'}">`
                + `<td>${esc(r.band)}</td>${cell(r, 'heardMe')}${cell(r, 'iHeard')}</tr>`;
        }).join('');
        $('bandsEmpty').hidden = rows.length > 0;
        $('bandsNote').textContent = bandFilter ? `showing ${bandFilter} only` : `stations, last ${windowText}`;
        $('allBands').hidden = !bandFilter;
        $('title').textContent = `${CALL} spots` + (bandFilter ? ` on ${bandFilter}` : '');
    }

    function drawList(now, windowSecs, shown) {
        const rows = listRows(store.within(now, windowSecs, shown, bandFilter), home, sort.key, sort.desc);
        const miles = unitsSel.value === 'mi';
        $('rows').innerHTML = rows.map(r => {
            const age = now - r.t;
            const cls = age < FRESH_SECS ? 'sm-fresh' : age > 3600 ? 'sm-old' : '';
            const tip = `${KIND_LABEL[r.kind]} · ${r.mode} · ${r.grid} · ${ago(r.t)}`;
            return `<tr class="${cls}" data-key="${esc(r.kind + ':' + r.call)}" title="${esc(tip)}">`
                + `<td><span class="sm-dot" style="background:${COLOURS[r.kind]}" aria-label="${KIND_LABEL[r.kind]}"></span>${esc(r.call)}</td>`
                + `<td>${esc(r.band)}</td>`
                + `<td class="sm-num">${r.snr === null ? '' : esc(r.snr)}</td>`
                + `<td class="sm-num">${formatDistance(r.km, miles)}</td>`
                + `<td class="sm-num">${utcHhmm(r.t)}</td></tr>`;
        }).join('');
        $('empty').hidden = rows.length > 0;
        for (const th of root.querySelectorAll('.sm-list th')) {
            th.setAttribute('aria-sort', th.dataset.sort === sort.key ? (sort.desc ? 'descending' : 'ascending') : 'none');
        }
    }

    root.querySelector('.sm-list thead').addEventListener('click', e => {
        const key = e.target.closest('th')?.dataset.sort;
        if (!key) return;
        // A new column starts the useful way round: newest, strongest, furthest first; calls and bands A-Z.
        sort = key === sort.key ? { key, desc: !sort.desc } : { key, desc: !(key === 'call' || key === 'band') };
        save('sort', sort);
        draw();
    });
    const findOnMap = key => {
        const m = markers.get(key);
        if (!m) return;
        map.setView(m.getLatLng(), Math.max(map.getZoom(), 4));
        m.openTooltip();
    };
    $('rows').addEventListener('click', e => findOnMap(e.target.closest('tr')?.dataset.key));
    $('bestRows').addEventListener('click', e => findOnMap(e.target.closest('button')?.dataset.key));
    const pickBand = band => { bandFilter = band === bandFilter ? '' : band; draw(); };
    $('bandRows').addEventListener('click', e => {
        const tr = e.target.closest('tr');
        if (tr) pickBand(tr.dataset.band);
    });
    $('bandRows').addEventListener('keydown', e => {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        const tr = e.target.closest('tr');
        if (!tr) return;
        e.preventDefault();
        const band = tr.dataset.band;
        pickBand(band);
        // The rows are redrawn: keep the keyboard on the same band.
        [...$('bandRows').rows].find(r => r.dataset.band === band)?.focus();
    });
    $('allBands').addEventListener('click', () => { bandFilter = ''; draw(); });
    windowSel.addEventListener('change', () => { save('window', Number(windowSel.value)); draw(); });
    kindsSel.addEventListener('change', () => { save('kinds', kindsSel.value); draw(); });
    unitsSel.addEventListener('change', () => { save('units', unitsSel.value); draw(); });
    setInterval(draw, 30000);   // ages the fresh markers and drops old ones

    const addSpot = (kind, raw) => { if (store.add(kind, raw)) scheduleDraw(); };

    // ---- Live feed ----
    const liveEl = $('live');
    let client = null, retryDelay = 5000, lastLive = 0, liveCount = 0;
    const showLive = () => {
        const last = lastLive ? `last spot ${new Date(lastLive).toLocaleTimeString()}` : 'no spots yet';
        liveEl.innerHTML = `Live feed: <b>connected</b> · ${liveCount} live · ${last}`;
    };
    function connectLive() {
        client = mqtt.connect('wss://mqtt.pskreporter.info:1886', {
            clientId: `${app}-spotmap-${topicCall(CALL)}-${Math.random().toString(16).slice(2, 10)}`,
            keepalive: 60,
            reconnectPeriod: 0,   // reconnect is done by hand below, backing off gently
            connectTimeout: 20000,
        });
        client.on('connect', () => {
            retryDelay = 5000;
            client.subscribe(topicsFor(CALL));
            showLive();
        });
        client.on('message', (_topic, payload) => {
            let msg;
            try { msg = JSON.parse(new TextDecoder().decode(payload)); } catch { return; }
            const c = classifyMqtt(msg, CALL);
            if (!c) return;
            addSpot(c.kind, c.raw);
            lastLive = Date.now();
            liveCount++;
            showLive();
        });
        const retry = () => {
            if (!client) return;
            client.removeAllListeners();
            client.end(true);
            client = null;
            liveEl.textContent = `Live feed: disconnected, trying again in ${Math.round(retryDelay / 1000)} s`;
            setTimeout(connectLive, retryDelay);
            retryDelay = Math.min(retryDelay * 2, 120000);
        };
        client.on('close', retry);
        client.on('error', retry);
    }
    setInterval(() => { if (client?.connected) showLive(); }, 30000);
    connectLive();

    // ---- History at start-up (PSK Reporter query API, JSONP) ----
    let seq = 0;
    const jsonp = url => new Promise((resolve, reject) => {
        const name = 'rwcSpotMapCb' + (++seq);
        const s = document.createElement('script');
        const timer = setTimeout(() => { cleanup(); reject(new Error('timed out')); }, 60000);
        function cleanup() { clearTimeout(timer); delete window[name]; s.remove(); }
        window[name] = data => { cleanup(); resolve(data); };
        s.onerror = () => { cleanup(); reject(new Error('network error')); };
        s.src = url + '&callback=' + name;
        document.head.append(s);
    });
    const historyEl = $('history');
    const done = {};
    async function loadHistory(kind) {
        const who = kind === 'heardMe' ? 'senderCallsign' : 'receiverCallsign';
        const label = kind === 'heardMe' ? 'heard me' : 'I heard';
        try {
            const data = await jsonp(`https://retrieve.pskreporter.info/query?${who}=${encodeURIComponent(CALL)}`
                + '&flowStartSeconds=-86400&rronly=1&noactive=1');
            if (data?.message && !data.receptionReport) {
                done[kind] = `${label} not loaded (PSK Reporter: ${data.message})`;
            } else {
                // The search part says when PSK Reporter last saw the callsign, even
                // when it sends no reports: used by the "No spots yet" notice.
                let search = data?.[kind === 'heardMe' ? 'senderSearch' : 'receiverSearch'];
                if (search && !Array.isArray(search)) search = [search];
                const seen = Number(search?.[0]?.recentFlowStartSeconds);
                if (seen) empty.heardAt[kind] = seen;
                let reports = data?.receptionReport ?? [];
                if (!Array.isArray(reports)) reports = [reports];
                for (const r of reports) addSpot(kind, fromReceptionReport(r));
                done[kind] = `${label} ${reports.length}`;
            }
        } catch (e) {
            done[kind] = `${label} not loaded (${e.message})`;
        }
        historyEl.textContent = 'History 24 h: ' + Object.values(done).join(', ')
            + (done.iHeard ? '' : '; "I heard" loads in a few minutes');
        empty.asked++;
        showEmpty(Date.now() / 1000);
        historyEl.title = 'PSK Reporter allows about one history query every 5 minutes, so the two halves load 5.5 minutes apart.';
    }
    historyEl.textContent = 'History: loading…';
    loadHistory('heardMe');
    setTimeout(() => loadHistory('iHeard'), HISTORY_GAP_MS);
    draw();
}
