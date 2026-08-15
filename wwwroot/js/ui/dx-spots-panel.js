// Yaesu Web Control – DX Spots List Panel
//
// Popup list of DX cluster spots. Sortable columns, click-to-QSY, filtered
// to the operator's current band (with an "All bands" override). Works
// regardless of whether an SDR is configured — relies only on the SignalR
// DxSpot event stream, which flows unconditionally.

// ?v=1 is a one-time cache-buster, not a number to bump — see gaugeFactory.js.
import { modeForHz } from './band-plan.js?v=1';

const LS_KEY     = 'dxSpotsPanel';
const AGE_MAX_MS = 15 * 60 * 1000;   // matches DxSpotAgeMinutes default
const TICK_MS    = 30 * 1000;        // re-render to age out stale rows
const MAX_SPOTS  = 500;              // hard cap on in-memory list

// Amateur band edges in Hz — used to decide which band a spot belongs to.
const BAND_EDGES = [
    { name: '160m', lo:   1800000, hi:   2000000 },
    { name:  '80m', lo:   3500000, hi:   4000000 },
    { name:  '60m', lo:   5250000, hi:   5450000 },
    { name:  '40m', lo:   7000000, hi:   7300000 },
    { name:  '30m', lo:  10100000, hi:  10150000 },
    { name:  '20m', lo:  14000000, hi:  14350000 },
    { name:  '17m', lo:  18068000, hi:  18168000 },
    { name:  '15m', lo:  21000000, hi:  21450000 },
    { name:  '12m', lo:  24890000, hi:  24990000 },
    { name:  '10m', lo:  28000000, hi:  29700000 },
    { name:   '6m', lo:  50000000, hi:  54000000 },
    { name:   '4m', lo:  70000000, hi:  70500000 },
    { name:   '2m', lo: 144000000, hi: 148000000 },
];

const MODE_RX = /\b(FT8|FT4|JS8|JT65|JT9|MFSK|FSK441|JT4|MSK144|CW|SSB|USB|LSB|RTTY|PSK31|PSK63|PSK|AM|FM|DATA|DIGITAL)\b/i;

// Approximate FT8/FT4 standard frequencies (kHz) for the fallback mode parser
const FT8_KHZ = [1840, 3573, 5357, 7074, 10136, 14074, 18100, 21074, 24915, 28074, 50313, 70154];
const FT4_KHZ = [3575, 7047, 10140, 14080, 18104, 21140, 24919, 28180];

export class DxSpotsPanel {
    constructor() {
        this._spots          = [];
        this._vfoHz          = 0;
        this._showAllBands   = false;
        this._sortBy         = 'age';
        this._sortDir        = 'asc';
        this._dialog         = null;
        this._tbody          = null;
        this._title          = null;
        this._count          = null;
        this._empty          = null;
        this._allBandsChk    = null;
        this._tickTimer      = null;
        this._rowClickWired  = false;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    init() {
        this._dialog      = document.getElementById('dxSpotsDialog');
        if (!this._dialog) return;
        this._tbody       = document.getElementById('dxSpotsTbody');
        this._title       = document.getElementById('dxSpotsTitle');
        this._count       = document.getElementById('dxSpotsCount');
        this._empty       = document.getElementById('dxSpotsEmpty');
        this._allBandsChk = document.getElementById('dxSpotsAllBandsChk');

        this._loadSettings();
        if (this._allBandsChk) {
            this._allBandsChk.checked = this._showAllBands;
            this._allBandsChk.addEventListener('change', () => {
                this._showAllBands = this._allBandsChk.checked;
                this._saveSettings();
                this._render();
            });
        }

        // Sortable column headers — `data-sort` carries the column key
        for (const th of this._dialog.querySelectorAll('th[data-sort]')) {
            th.addEventListener('click', () => this._setSort(th.dataset.sort));
        }

        this._initDrag();
        this._render();

        // Periodic re-render so rows age out even when no new spot arrives.
        // Cleared on close; restarted on show.
        this._tickTimer = setInterval(() => {
            if (this._dialog && this._dialog.open) this._render();
        }, TICK_MS);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    setSpots(spots) {
        this._spots = Array.isArray(spots) ? spots.slice() : [];
        this._render();
    }

    addSpot(spot) {
        if (!spot) return;
        // Dedupe by callsign + frequency — newer entries replace older ones
        const i = this._spots.findIndex(
            s => s.callsign === spot.callsign && s.frequencyHz === spot.frequencyHz
        );
        if (i >= 0) this._spots[i] = spot;
        else        this._spots.unshift(spot);
        if (this._spots.length > MAX_SPOTS) this._spots.length = MAX_SPOTS;
        this._render();
    }

    setVfoFrequency(hz) {
        const prev = this._currentBand();
        this._vfoHz = hz;
        if (prev !== this._currentBand()) this._render();
    }

    show() {
        if (!this._dialog) return;
        this._dialog.show();
        this._render();
    }

    toggle() {
        if (!this._dialog) return;
        if (this._dialog.open) this._dialog.close();
        else                   this.show();
    }

    // ── Band / mode helpers ─────────────────────────────────────────────────

    _currentBand() {
        return this._bandForHz(this._vfoHz);
    }

    _bandForHz(hz) {
        if (!hz) return '';
        const b = BAND_EDGES.find(b => hz >= b.lo && hz <= b.hi);
        return b ? b.name : '';
    }

    _modeFromSpot(spot) {
        const m = (spot.comment || '').match(MODE_RX);
        if (m) {
            const tok = m[1].toUpperCase();
            if (tok === 'USB' || tok === 'LSB')      return 'SSB';
            if (tok === 'DIGITAL' || tok === 'DATA') return 'DATA';
            return tok;
        }
        // Frequency-based fallback for FT8/FT4 and rough CW sub-band guess
        const khz = spot.frequencyHz / 1000;
        if (FT8_KHZ.some(f => Math.abs(khz - f) < 3)) return 'FT8';
        if (FT4_KHZ.some(f => Math.abs(khz - f) < 3)) return 'FT4';
        // CW lives in the first ~30 kHz of each HF band — approximate only
        const cwStarts = [1800, 3500, 7000, 10100, 14000, 18068, 21000, 24890, 28000, 50000];
        if (cwStarts.some(s => khz >= s && khz < s + 30)) return 'CW';
        return '';
    }

    // ── Filtering / sorting ─────────────────────────────────────────────────

    _filteredSpots() {
        const band = this._currentBand();
        const now  = Date.now();
        const onlyWatched = !!window.dxOnlyWatched;
        return this._spots.filter(s => {
            const t = new Date(s.receivedUtc).getTime();
            if (now - t > AGE_MAX_MS) return false;
            // "Show only watched callsigns" toggle in the DX Watch popup —
            // see Pages/Index.cshtml. The flag is set by DxClusterService.
            if (onlyWatched && !s.isWatched) return false;
            if (this._showAllBands || !band) return true;
            return this._bandForHz(s.frequencyHz) === band;
        });
    }

    /** Force a re-render — used when an external setting (the "only watched"
     *  filter toggle) changes and the panel needs to update immediately. */
    redraw() {
        this._render();
    }

    _setSort(col) {
        if (this._sortBy === col) {
            this._sortDir = this._sortDir === 'asc' ? 'desc' : 'asc';
        } else {
            this._sortBy  = col;
            this._sortDir = 'asc';
        }
        this._saveSettings();
        this._render();
    }

    _sortSpots(spots) {
        const dir = this._sortDir === 'asc' ? 1 : -1;
        return spots.slice().sort((a, b) => {
            let av, bv;
            switch (this._sortBy) {
                case 'callsign': av = a.callsign;   bv = b.callsign;   break;
                case 'freq':     av = a.frequencyHz; bv = b.frequencyHz; break;
                case 'spotter':  av = a.spotter;    bv = b.spotter;    break;
                case 'mode':     av = this._modeFromSpot(a); bv = this._modeFromSpot(b); break;
                case 'time':
                    av = new Date(a.receivedUtc).getTime();
                    bv = new Date(b.receivedUtc).getTime();
                    return (av - bv) * dir;
                case 'age':
                    // Ascending Age = newest first (smallest age)
                    av = new Date(a.receivedUtc).getTime();
                    bv = new Date(b.receivedUtc).getTime();
                    return (bv - av) * dir;
                default: return 0;
            }
            if (av == null) av = '';
            if (bv == null) bv = '';
            return av < bv ? -dir : av > bv ? dir : 0;
        });
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    _render() {
        if (!this._tbody) return;

        const filtered = this._sortSpots(this._filteredSpots());

        if (this._title) {
            const band = this._currentBand();
            this._title.textContent = this._showAllBands
                ? 'DX Spots — All bands'
                : (band ? `DX Spots — ${band}` : 'DX Spots');
        }
        if (this._count) {
            this._count.textContent = `${filtered.length} shown / ${this._spots.length} total`;
        }

        if (filtered.length === 0) {
            this._tbody.innerHTML = '';
            if (this._empty) this._empty.style.display = '';
            this._updateSortIndicators();
            return;
        }
        if (this._empty) this._empty.style.display = 'none';

        const now = Date.now();
        let html = '';
        for (const s of filtered) {
            const t        = new Date(s.receivedUtc);
            const ageMin   = Math.floor((now - t.getTime()) / 60000);
            const ageStr   = ageMin < 1 ? '<1m' : `${ageMin}m`;
            const timeStr  = `${String(t.getUTCHours()).padStart(2,'0')}:${String(t.getUTCMinutes()).padStart(2,'0')}`;
            const freqStr  = (s.frequencyHz / 1000).toFixed(1);
            const callCls  = s.isWatched ? 'dxs-call dxs-watched' : 'dxs-call';
            const mode     = this._modeFromSpot(s);
            const comment  = this._esc(s.comment || '');
            const spotter  = this._esc(s.spotter || '');
            const callsign = this._esc(s.callsign || '');
            html += `<tr data-hz="${s.frequencyHz}">`
                  + `<td class="${callCls}">${callsign}</td>`
                  + `<td class="dxs-num">${freqStr}</td>`
                  + `<td class="dxs-mode">${mode}</td>`
                  + `<td class="dxs-num">${timeStr}</td>`
                  + `<td class="dxs-num">${ageStr}</td>`
                  + `<td>${spotter}</td>`
                  + `<td class="dxs-comment" title="${comment}">${comment}</td>`
                  + `</tr>`;
        }
        this._tbody.innerHTML = html;
        this._updateSortIndicators();

        if (!this._rowClickWired) {
            this._tbody.addEventListener('click', (e) => {
                const tr = e.target.closest('tr');
                if (!tr) return;
                const hz = parseInt(tr.dataset.hz, 10);
                if (hz && window.radioControl && typeof window.radioControl.setFrequency === 'function') {
                    window.radioControl.setFrequency('A', hz);
                    // Match the spectrum-panel click behaviour — follow the
                    // QSY with a band-plan-aware mode change so clicking
                    // an FT8 spot from a phone spot also flips USB→DATA-U.
                    // modeForHz returns the mode name window.setMode accepts.
                    const targetMode = modeForHz(hz);
                    if (targetMode && typeof window.setMode === 'function') {
                        try { window.setMode('A', targetMode); } catch { /* ignore */ }
                    }
                }
            });
            this._rowClickWired = true;
        }
    }

    _updateSortIndicators() {
        if (!this._dialog) return;
        const arrow = this._sortDir === 'asc' ? ' ▲' : ' ▼';
        for (const th of this._dialog.querySelectorAll('th[data-sort]')) {
            const base = th.dataset.label || th.textContent.replace(/[▲▼]\s*$/, '').trim();
            th.dataset.label = base;
            th.textContent   = th.dataset.sort === this._sortBy ? base + arrow : base;
        }
    }

    _esc(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => (
            { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
        ));
    }

    // ── Persistence ─────────────────────────────────────────────────────────

    _saveSettings() {
        if (!this._dialog) return;
        const s = {
            showAllBands: this._showAllBands,
            sortBy:       this._sortBy,
            sortDir:      this._sortDir,
            left:   this._dialog.style.left   || '',
            top:    this._dialog.style.top    || '',
            width:  this._dialog.style.width  || '',
            height: this._dialog.style.height || '',
        };
        try { localStorage.setItem(LS_KEY, JSON.stringify(s)); } catch {}
    }

    _loadSettings() {
        if (!this._dialog) return;
        try {
            const raw = localStorage.getItem(LS_KEY);
            if (!raw) return;
            const s = JSON.parse(raw);
            if (typeof s.showAllBands === 'boolean') this._showAllBands = s.showAllBands;
            if (s.sortBy)  this._sortBy  = s.sortBy;
            if (s.sortDir) this._sortDir = s.sortDir;
            if (s.left || s.top) {
                this._dialog.style.margin = '0';
                if (s.left) this._dialog.style.left = s.left;
                if (s.top)  this._dialog.style.top  = s.top;
            }
            if (s.width)  this._dialog.style.width  = s.width;
            if (s.height) this._dialog.style.height = s.height;
        } catch { /* ignore corrupt data */ }
    }

    // ── Drag ────────────────────────────────────────────────────────────────

    _initDrag() {
        const header = this._dialog.querySelector('.dxs-header');
        if (!header) return;
        header.addEventListener('mousedown', (e) => {
            if (e.target.closest('button, input, label')) return;
            const rect  = this._dialog.getBoundingClientRect();
            const origX = e.clientX, origY = e.clientY;
            const baseL = rect.left,  baseT = rect.top;

            this._dialog.style.margin = '0';
            this._dialog.style.left   = `${baseL}px`;
            this._dialog.style.top    = `${baseT}px`;

            const onMove = (ev) => {
                let l = baseL + (ev.clientX - origX);
                let t = baseT + (ev.clientY - origY);
                l = Math.max(-rect.width + 40, Math.min(window.innerWidth - 40, l));
                t = Math.max(0,                 Math.min(window.innerHeight - 40, t));
                this._dialog.style.left = `${l}px`;
                this._dialog.style.top  = `${t}px`;
            };
            const onUp = () => {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup',   onUp);
                this._saveSettings();
            };
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup',   onUp);
            e.preventDefault();
        });
    }
}
