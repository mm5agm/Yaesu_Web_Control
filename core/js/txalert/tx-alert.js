// ── High SWR / high ALC pop-up ─────────────────────────────────────────────
//
// Shared by Icom Web Control and Yaesu Web Control (YWC Discussion #257: an
// alert you can't miss while another program is driving the radio). The app
// decides when there is an alert (core Services/TxAlert/TxAlertMonitor) and
// serves it at GET /api/txalert; this module polls that and puts a large red
// box at the top of the page. Nothing radio-specific in here.
//
// The box stays up while the reading is over the limit (pulsing), and after
// the over as a record of the peak, until the operator dismisses it or five
// minutes pass. Dismissing is remembered per alert, in localStorage, so it
// closes in every tab and the next alert still shows.
//
// Load it as a module on every page that matters:
//   <script type="module" src="~/js/txalert/tx-alert.js"></script>

export const POLL_MS = 1000;
export const DISMISSED_KEY = 'txAlertDismissed';
export const DISMISSED_MAX = 20;

export function dismissKey(run, kind, id) {
    return `${run}:${kind}:${id}`;
}

// Newest last, trimmed to the last DISMISSED_MAX.
export function rememberDismissed(list, key, max = DISMISSED_MAX) {
    const out = (Array.isArray(list) ? list : []).filter(k => k !== key);
    out.push(key);
    return out.slice(-max);
}

function fmtSwr(v) { return `${Number(v).toFixed(1)} : 1`; }
function fmtPct(v) { return `${Math.round(Number(v))}%`; }

export function fmtClock(iso) {
    const d = new Date(iso);
    if (isNaN(d)) return '';
    const p = n => String(n).padStart(2, '0');
    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

// What to show for a snapshot from /api/txalert: one card per alert the
// operator hasn't dismissed, SWR first.
export function alertCards(snap, isDismissed = () => false, clock = fmtClock) {
    if (!snap || !snap.enabled) return [];
    const cards = [];
    const swr = snap.swr;
    if (swr && !isDismissed(dismissKey(snap.run, 'swr', swr.id))) {
        cards.push(swr.active
            ? { key: dismissKey(snap.run, 'swr', swr.id), kind: 'swr', active: true,
                title: 'HIGH SWR', value: fmtSwr(swr.peak),
                detail: `Over your limit of ${fmtSwr(swr.limit)}. Stop transmitting and check the antenna, tuner and feeder.` }
            : { key: dismissKey(snap.run, 'swr', swr.id), kind: 'swr', active: false,
                title: 'High SWR on the last transmission', value: fmtSwr(swr.peak),
                detail: `Peak at ${clock(swr.at)}. Your limit is ${fmtSwr(swr.limit)}.` });
    }
    const alc = snap.alc;
    if (alc && !isDismissed(dismissKey(snap.run, 'alc', alc.id))) {
        const mode = alc.mode ? ` in ${alc.mode}` : '';
        cards.push(alc.active
            ? { key: dismissKey(snap.run, 'alc', alc.id), kind: 'alc', active: true,
                title: 'HIGH ALC', value: fmtPct(alc.peak),
                detail: `Over your limit of ${fmtPct(alc.limit)}${mode}. Turn the audio down from the PC until the ALC barely moves.` }
            : { key: dismissKey(snap.run, 'alc', alc.id), kind: 'alc', active: false,
                title: 'High ALC on the last transmission', value: fmtPct(alc.peak),
                detail: `Peak at ${clock(alc.at)}${mode}. Your limit is ${fmtPct(alc.limit)}.` });
    }
    return cards;
}

const CSS = `
#txAlertHost { position: fixed; top: 12px; left: 50%; transform: translateX(-50%);
  z-index: 2147483000; display: flex; flex-direction: column; gap: 10px;
  width: min(560px, calc(100vw - 24px)); pointer-events: none; }
.tx-alert-card { pointer-events: auto; background: #b00020; color: #fff;
  border: 4px solid #fff; border-radius: 10px; padding: 14px 18px;
  box-shadow: 0 0 0 4px #b00020, 0 10px 30px rgba(0,0,0,.6);
  font-family: system-ui, sans-serif; display: grid;
  grid-template-columns: 1fr auto; gap: 4px 16px; align-items: center; }
.tx-alert-card.ended { background: #5c0011; box-shadow: 0 0 0 4px #5c0011, 0 10px 30px rgba(0,0,0,.6); }
.tx-alert-card.active { animation: txAlertPulse 0.8s ease-in-out infinite alternate; }
@keyframes txAlertPulse { from { box-shadow: 0 0 0 4px #b00020, 0 0 12px 4px #ff1744; }
  to { box-shadow: 0 0 0 8px #ff1744, 0 0 40px 16px #ff1744; } }
@media (prefers-reduced-motion: reduce) { .tx-alert-card.active { animation: none; } }
.tx-alert-title { font-size: 1.15rem; font-weight: 700; letter-spacing: .04em; }
.tx-alert-value { font-size: 2.6rem; font-weight: 800; line-height: 1.1; grid-row: span 2; text-align: right; }
.tx-alert-detail { font-size: 1rem; line-height: 1.35; }
.tx-alert-dismiss { grid-column: 1 / -1; justify-self: start; margin-top: 6px;
  background: #fff; color: #b00020; border: 0; border-radius: 6px;
  font-weight: 700; font-size: 1rem; padding: 6px 16px; cursor: pointer; }
.tx-alert-dismiss:focus-visible { outline: 3px solid #ffeb3b; outline-offset: 2px; }
`;

export function startTxAlert({ url = '/api/txalert', pollMs = POLL_MS, doc = document, store = localStorage } = {}) {
    if (doc.getElementById('txAlertHost')) return;
    const style = doc.createElement('style');
    style.textContent = CSS;
    doc.head.appendChild(style);
    const host = doc.createElement('div');
    host.id = 'txAlertHost';
    host.setAttribute('role', 'alert');
    host.setAttribute('aria-live', 'assertive');
    doc.body.appendChild(host);

    const readDismissed = () => {
        try { return JSON.parse(store.getItem(DISMISSED_KEY) || '[]'); } catch { return []; }
    };
    let last = null;
    let shown = '';

    function render() {
        const dismissed = new Set(readDismissed());
        const cards = alertCards(last, k => dismissed.has(k));
        // Only touch the DOM when something changed, so a screen reader
        // announces an alert once, not every poll.
        const sig = JSON.stringify(cards);
        if (sig === shown) return;
        shown = sig;
        host.replaceChildren(...cards.map(c => {
            const el = doc.createElement('div');
            el.className = `tx-alert-card ${c.active ? 'active' : 'ended'}`;
            el.dataset.kind = c.kind;
            const title = doc.createElement('div'); title.className = 'tx-alert-title'; title.textContent = c.title;
            const value = doc.createElement('div'); value.className = 'tx-alert-value'; value.textContent = c.value;
            const detail = doc.createElement('div'); detail.className = 'tx-alert-detail'; detail.textContent = c.detail;
            const btn = doc.createElement('button');
            btn.type = 'button'; btn.className = 'tx-alert-dismiss'; btn.textContent = 'Dismiss';
            btn.addEventListener('click', () => {
                try { store.setItem(DISMISSED_KEY, JSON.stringify(rememberDismissed(readDismissed(), c.key))); } catch { /* private mode */ }
                render();
            });
            el.append(title, value, detail, btn);
            return el;
        }));
    }

    async function poll() {
        try {
            const r = await fetch(url, { cache: 'no-store' });
            if (r.ok) { last = await r.json(); render(); }
        } catch { /* app restarting; try again next time */ }
    }

    window.addEventListener('storage', e => { if (e.key === DISMISSED_KEY) render(); });
    poll();
    setInterval(poll, pollMs);
}

// Not inside a frame: the Radio page frames some pages, and its own copy
// already shows the alert.
const framed = () => { try { return window.self !== window.top; } catch { return true; } };
if (typeof document !== 'undefined' && typeof window !== 'undefined' && !window.__txAlertNoAutostart && !framed()) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => startTxAlert());
    else startTxAlert();
}
