/*
 * feature-setup.js - "set this up now" pop-outs for Home-page panels.
 *
 * Some panels on Home are always visible but cannot do anything until
 * something is chosen in Settings: the CW reader needs the radio's USB
 * recording device, the DX spots panel needs a cluster host and a login
 * callsign. Telling the operator to go and find those fields is a poor
 * answer when we know exactly which field is missing, so each panel can
 * instead offer to fix its own prerequisite here and stay where it is.
 *
 * The dialog is built in JS rather than in the Razor page so that a panel
 * can adopt it without a markup change, and so that the shared CW panel in
 * core/ can reach it through one optional global rather than importing
 * anything YWC-specific.
 *
 * Writes go through /api/feature-setup, which whitelists the exact fields
 * each pop-out is allowed to touch. This is not a general settings editor
 * and should not become one - anything beyond a single missing
 * prerequisite belongs on the Settings page.
 */

const API = "/api/feature-setup";

let dlg = null;      // the <dialog>, built once on first use
let bodyEl = null;
let titleEl = null;
let msgEl = null;
let saveBtn = null;
let onSave = null;   // async () => ({ success, message, error })

function buildDialog() {
    if (dlg) return;

    dlg = document.createElement("dialog");
    dlg.id = "featureSetupDialog";
    dlg.className = "p-0 border-0 rounded shadow";
    dlg.style.maxWidth = "34rem";
    dlg.style.width = "92vw";
    dlg.innerHTML = `
        <form method="dialog" class="m-0">
            <div class="d-flex align-items-center justify-content-between px-3 py-2 border-bottom">
                <h5 class="m-0" id="featureSetupTitle">Set up</h5>
                <button type="submit" class="btn btn-sm btn-outline-secondary"
                        value="cancel" aria-label="Close">&times;</button>
            </div>
            <div class="p-3" id="featureSetupBody"></div>
            <div class="px-3 pb-3">
                <div id="featureSetupMsg" class="small" role="status" aria-live="polite"></div>
            </div>
            <div class="d-flex justify-content-end gap-2 px-3 py-2 border-top">
                <button type="submit" class="btn btn-secondary" value="cancel">Cancel</button>
                <button type="button" class="btn btn-primary" id="featureSetupSave">Save</button>
            </div>
        </form>`;
    document.body.appendChild(dlg);

    bodyEl  = dlg.querySelector("#featureSetupBody");
    titleEl = dlg.querySelector("#featureSetupTitle");
    msgEl   = dlg.querySelector("#featureSetupMsg");
    saveBtn = dlg.querySelector("#featureSetupSave");

    saveBtn.addEventListener("click", async () => {
        if (!onSave) return;
        saveBtn.disabled = true;
        setMessage("Saving...", "text-muted");
        try {
            const r = await onSave();
            if (r && r.success) {
                setMessage(r.message || "Saved.", "text-success");
                document.dispatchEvent(new CustomEvent("ywc:feature-setup-saved",
                    { detail: { kind: dlg.dataset.kind } }));
                setTimeout(() => dlg.close(), 1400);
            } else {
                setMessage((r && r.error) || "Could not save.", "text-danger");
            }
        } catch (err) {
            setMessage("Could not save: " + err, "text-danger");
        } finally {
            saveBtn.disabled = false;
        }
    });
}

function setMessage(text, cls) {
    if (!msgEl) return;
    msgEl.className = "small " + (cls || "text-muted");
    msgEl.textContent = text || "";
}

async function getJson(url) {
    const r = await fetch(url);
    if (!r.ok) throw new Error(url + " returned " + r.status);
    return await r.json();
}

async function postJson(url, body) {
    const r = await fetch(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
    });
    if (!r.ok) throw new Error(url + " returned " + r.status);
    return await r.json();
}

function show(kind, title) {
    dlg.dataset.kind = kind;
    titleEl.textContent = title;
    setMessage("");
    if (!dlg.open) dlg.showModal();
}

/* -- DX cluster ---------------------------------------------------------
   Four fields, not one toggle: the host and the login callsign both
   default to empty, so a bare "enable it" switch would save a
   configuration that cannot connect and would look like a bug. */

async function openDxCluster() {
    buildDialog();
    const state = await getJson(`${API}/state`);
    const dx = state.dxCluster || {};

    bodyEl.innerHTML = `
        <p class="small text-muted">
            DX spots come from a telnet cluster server. Enter one below and the
            spots panel starts filling &mdash; the same fields live in
            Settings &rsaquo; DX Cluster if you want to change them later.
        </p>
        <div class="mb-2">
            <label class="form-label small mb-1" for="fsDxHost">Cluster host</label>
            <input class="form-control" id="fsDxHost" placeholder="gb7ujs.ddns.net">
        </div>
        <div class="row g-2 mb-2">
            <div class="col-4">
                <label class="form-label small mb-1" for="fsDxPort">Port</label>
                <input class="form-control" id="fsDxPort" type="number" min="1" max="65535">
            </div>
            <div class="col-8">
                <label class="form-label small mb-1" for="fsDxCall">Your callsign</label>
                <input class="form-control text-uppercase" id="fsDxCall" placeholder="MM5AGM">
            </div>
        </div>
        <div class="form-check">
            <input class="form-check-input" type="checkbox" id="fsDxEnabled" checked>
            <label class="form-check-label" for="fsDxEnabled">Connect to the cluster</label>
        </div>`;

    dlg.querySelector("#fsDxHost").value = dx.host || "";
    dlg.querySelector("#fsDxPort").value = dx.port || 7300;
    dlg.querySelector("#fsDxCall").value = dx.callsign || "";
    dlg.querySelector("#fsDxEnabled").checked = dx.enabled !== false;

    onSave = () => postJson(`${API}/dx-cluster`, {
        enabled:  dlg.querySelector("#fsDxEnabled").checked,
        host:     dlg.querySelector("#fsDxHost").value,
        port:     parseInt(dlg.querySelector("#fsDxPort").value, 10) || 7300,
        callsign: dlg.querySelector("#fsDxCall").value
    });

    show("dx-cluster", "Set up the DX cluster");
    dlg.querySelector("#fsDxHost").focus();
}

/* -- CW reader audio ----------------------------------------------------
   The reader takes a capture-only hold on the radio's RX endpoint, so the
   only thing it can be missing is that recording device. The TX device is
   deliberately not offered here: touching it could break an existing
   Remote Audio setup, and the reader never needs it. */

async function openCwAudio() {
    buildDialog();
    const [state, devices] = await Promise.all([
        getJson(`${API}/state`),
        getJson("/api/audio/devices")
    ]);
    const current = (state.cwAudio && state.cwAudio.rxDevice) || "";
    const inputs = (devices && devices.inputs) || [];

    const options = inputs.map(d => {
        const sel = d.key === current ? " selected" : "";
        const hint = d.likelyRadio ? " (looks like the radio)" : "";
        return `<option value="${escapeAttr(d.key)}"${sel}>${escapeHtml(d.displayName || d.name)}${hint}</option>`;
    }).join("");

    bodyEl.innerHTML = `
        <p class="small text-muted">
            The CW reader decodes the receive audio coming back from the radio
            over USB, so it needs to know which recording device that is. It
            opens that device for listening only &mdash; it never takes the
            radio's playback side, so this will not interfere with WSJT-X or
            anything else that transmits.
        </p>
        <label class="form-label small mb-1" for="fsCwRx">Radio RX (recording) device</label>
        <select class="form-select" id="fsCwRx">
            <option value="">Choose a device...</option>
            ${options}
        </select>
        ${inputs.length === 0
            ? `<div class="small text-danger mt-2">No recording devices found. Check the radio's USB cable, then reopen this box.</div>`
            : ``}`;

    onSave = () => postJson(`${API}/audio-rx`, {
        device: dlg.querySelector("#fsCwRx").value
    });

    show("cw-audio", "Choose the radio's audio input");
    dlg.querySelector("#fsCwRx").focus();
}

function escapeHtml(s) {
    return String(s ?? "").replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));
}
function escapeAttr(s) {
    return escapeHtml(s).replace(/"/g, "&quot;");
}

/* -- Public entry point -------------------------------------------------
   `kind` is one of "dx-cluster" or "cw-audio". Unknown kinds are ignored
   rather than throwing, because callers may be shared code that cannot
   know which pop-outs this app provides. */

export async function openFeatureSetup(kind) {
    try {
        if (kind === "dx-cluster") await openDxCluster();
        else if (kind === "cw-audio") await openCwAudio();
        else return false;
        return true;
    } catch (err) {
        console.error("[feature-setup] could not open", kind, err);
        return false;
    }
}

/* The shared CW panel in core/ is used by more than one app, so it must not
   import anything from this repo. It calls this global if the host app
   happens to provide it, and carries on unchanged if it does not. */
window.radioFeatureSetup = {
    open: openFeatureSetup,
    provides: kind => kind === "dx-cluster" || kind === "cw-audio"
};

/* The DX spots panel's empty state already tells the operator to configure
   the cluster in Settings. Give it the shortcut too, from here rather than
   from the Razor page, so adopting the pop-out needs no markup change. */
function adoptDxEmptyState() {
    const empty = document.getElementById("dxSpotsEmpty");
    if (!empty || empty.querySelector("#fsDxOpenBtn")) return;
    const wrap = document.createElement("div");
    wrap.className = "mt-2";
    wrap.innerHTML = `
        <button type="button" class="btn btn-sm btn-outline-primary" id="fsDxOpenBtn">
            <i class="bi bi-gear" aria-hidden="true"></i>&nbsp;Set up the DX cluster
        </button>`;
    wrap.querySelector("#fsDxOpenBtn").addEventListener("click", () => openFeatureSetup("dx-cluster"));
    empty.appendChild(wrap);
}

if (document.readyState === "loading")
    document.addEventListener("DOMContentLoaded", adoptDxEmptyState);
else
    adoptDxEmptyState();
