// Radio Web Control - CW QSO log form
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// Turns what the reader copied into a line in an ADIF log. Talks only to
// /api/cw/qso/suggest and /api/cw/qso, both of which each app implements
// itself, so there is nothing radio-specific in here.
//
// One rule shapes the whole layout, and it is worth stating before the code
// rather than defending it afterwards:
//
//   A field the radio or the clock knows is filled in. A field the DECODER
//   thinks it knows is offered, and left empty until the operator picks it.
//
// The frequency, the band, the mode and the time are facts - they come from
// the rig and the system clock, not from Morse pulled out of noise. The
// callsign, the report, the name and the QTH come from the copy, and the copy
// can be confidently wrong: section 4.11h measured the decoder reporting
// confidence 1.00 on 592 characters of junk. A callsign silently pre-filled
// from that is worse than an empty box, because the operator has no reason to
// look at a field that already has something in it. So the suggestions sit
// beside the box as buttons, each carrying the evidence that ranked it -
// "follows DE", "sent 3 times" - and one click fills the box. That costs the
// operator one click on a good decode and saves them a wrong log entry on a
// bad one.

const SUGGEST_URL = '/api/cw/qso/suggest';
const SAVE_URL    = '/api/cw/qso';

export class CwQsoForm {
    constructor() {
        this._host   = null;
        this._fields = {};
        this._status = null;
        this._draft  = null;
        this._busy   = false;
    }

    /**
     * @param {HTMLElement|string} host container element, or its id. The form
     *        builds its own DOM inside it, so the host page needs only an
     *        empty div - which keeps the markup in step with this file rather
     *        than in two places that can drift apart.
     */
    init(host) {
        this._host = typeof host === 'string' ? document.getElementById(host) : host;
        if (!this._host) return;

        this._build();
        this._host.hidden = true;
    }

    get isOpen() { return !!this._host && !this._host.hidden; }

    /** Opens the form and asks the server what it thinks is in the copy. */
    async open() {
        if (!this._host) return;
        this._host.hidden = false;
        await this.refresh();
        this._fields.call?.focus();
    }

    close() {
        if (this._host) this._host.hidden = true;
    }

    async toggle() {
        if (this.isOpen) this.close(); else await this.open();
    }

    /**
     * Re-reads the copy. Worth a button of its own: the operator usually opens
     * the form when the other station starts sending their details, and the
     * name and QTH arrive after that.
     */
    async refresh() {
        this._setStatus('Reading the copy...');
        try {
            const res = await fetch(SUGGEST_URL);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            this._draft = await res.json();
            this._applyDraft();
            this._setStatus('');
        } catch (err) {
            this._setStatus(`Could not read the copy: ${err.message}`, true);
        }
    }

    // ── Building ────────────────────────────────────────────────────────────

    _build() {
        this._host.innerHTML = '';
        this._host.className = 'cwq';

        const head = el('div', 'cwq-head');
        head.appendChild(el('strong', null, 'Log this QSO'));

        const refresh = button('Re-read copy', 'btn btn-sm btn-outline-secondary');
        refresh.addEventListener('click', () => this.refresh());

        const save = button('Save to log', 'btn btn-sm btn-primary');
        save.addEventListener('click', () => this._save());
        this._saveBtn = save;

        const close = button('✕', 'btn btn-sm ywc-dialog-close');
        close.setAttribute('aria-label', 'Close the QSO log form');
        close.addEventListener('click', () => this.close());

        const btns = el('div', 'cwq-head-btns');
        btns.append(refresh, save, close);
        head.appendChild(btns);

        const grid = el('div', 'cwq-grid');
        this._fields.call = this._row(grid, 'call',  'Callsign',    'callsigns');
        this._fields.rcvd = this._row(grid, 'rcvd',  'RST rcvd',    'signalReports');
        this._fields.sent = this._row(grid, 'sent',  'RST sent',    null);
        this._fields.name = this._row(grid, 'name',  'Name',        'names');
        this._fields.qth  = this._row(grid, 'qth',   'QTH',         'locations');
        this._fields.cmt  = this._row(grid, 'cmt',   'Comment',     null);

        this._facts  = el('div', 'cwq-facts');
        this._status = el('div', 'cwq-status');
        this._status.setAttribute('role', 'status');
        this._status.setAttribute('aria-live', 'polite');

        this._host.append(head, grid, this._facts, this._status);
    }

    _row(grid, key, label, suggestKey) {
        const id = 'cwq-' + key;

        const lab = el('label', 'cwq-label', label);
        lab.htmlFor = id;

        const input = document.createElement('input');
        input.type = 'text';
        input.id = id;
        input.className = 'form-control form-control-sm cwq-input';
        input.autocomplete = 'off';
        input.spellcheck = false;

        // The chips go under the box rather than beside it so that a long
        // suggestion never squeezes the box the operator is typing into.
        const chips = el('div', 'cwq-chips');
        chips.dataset.suggest = suggestKey || '';

        const cell = el('div', 'cwq-cell');
        cell.append(input, chips);
        grid.append(lab, cell);

        input._chips = chips;
        return input;
    }

    // ── Filling ─────────────────────────────────────────────────────────────

    _applyDraft() {
        const d = this._draft || {};

        // Facts, not suggestions: these come from the rig and the clock.
        this._fields.sent.value = d.rstSent || '599';
        this._facts.textContent = describe(d);

        this._chips(this._fields.call, d.callsigns);
        this._chips(this._fields.rcvd, d.signalReports);
        this._chips(this._fields.name, d.names);
        this._chips(this._fields.qth,  d.locations);
    }

    _chips(input, items) {
        const box = input._chips;
        box.innerHTML = '';
        if (!items || !items.length) {
            // Silence is a result, and saying so stops the operator waiting
            // for suggestions that are never coming.
            box.appendChild(el('span', 'cwq-none', 'nothing in the copy'));
            return;
        }

        for (const it of items) {
            const b = button(it.value, 'cwq-chip');
            // The reason is on the face of the chip, not only in a tooltip: a
            // suggestion the operator cannot weigh is just an assertion with
            // extra steps, and a tooltip is invisible on a touchscreen and to
            // anyone reading with a screen reader in browse mode.
            b.appendChild(el('span', 'cwq-why', it.why));
            b.title = `${it.value} - ${it.why} (score ${fmt(it.score)})`;
            b.addEventListener('click', () => {
                input.value = it.value;
                input.focus();
                box.querySelectorAll('.cwq-chip').forEach(c => c.classList.remove('cwq-chip-on'));
                b.classList.add('cwq-chip-on');
            });
            box.appendChild(b);
        }
    }

    // ── Saving ──────────────────────────────────────────────────────────────

    async _save() {
        if (this._busy) return;

        const call = (this._fields.call.value || '').trim().toUpperCase();
        if (!call) {
            this._setStatus('A QSO needs a callsign. Pick one, or type it.', true);
            this._fields.call.focus();
            return;
        }

        const d = this._draft || {};
        const body = {
            callsign:     call,
            whenUtc:      d.whenUtc || null,
            frequencyMhz: d.frequencyMhz ?? null,
            band:         d.band || null,
            mode:         d.mode || 'CW',
            rstSent:      value(this._fields.sent),
            rstReceived:  value(this._fields.rcvd),
            name:         value(this._fields.name),
            qth:          value(this._fields.qth),
            comment:      value(this._fields.cmt),
            stationCall:  d.stationCall || null,
        };

        this._busy = true;
        this._saveBtn.disabled = true;
        this._setStatus('Saving...');
        try {
            const res = await fetch(SAVE_URL, {
                method:  'POST',
                headers: { 'Content-Type': 'application/json' },
                body:    JSON.stringify(body),
            });
            const json = await res.json().catch(() => ({}));
            if (!res.ok) throw new Error(json.error || `HTTP ${res.status}`);

            // Naming the file matters more than it looks: the operator's next
            // question is always where it went, and an ADIF log nobody can
            // find is one nobody imports.
            this._setStatus(`Logged ${call}. Appended to ${json.path || 'the log'}.`);
            this._clearAfterSave();
        } catch (err) {
            this._setStatus(`Not saved: ${err.message}`, true);
        } finally {
            this._busy = false;
            this._saveBtn.disabled = false;
        }
    }

    _clearAfterSave() {
        // The QSO details go; the report we send and the facts stay, because
        // the next contact on the same frequency wants the same ones and
        // retyping 599 every time is the sort of friction that stops people
        // logging at all.
        for (const k of ['call', 'rcvd', 'name', 'qth', 'cmt']) {
            this._fields[k].value = '';
            this._fields[k]._chips.innerHTML = '';
        }
        this._fields.call.focus();
    }

    _setStatus(text, problem = false) {
        if (!this._status) return;
        this._status.textContent = text || '';
        this._status.classList.toggle('cwq-problem', !!problem && !!text);
    }
}

// ── Small helpers ───────────────────────────────────────────────────────────

function el(tag, cls, text) {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = text;
    return n;
}

function button(text, cls) {
    const b = el('button', cls, text);
    b.type = 'button';
    return b;
}

function value(input) {
    const v = (input.value || '').trim();
    return v.length ? v : null;
}

function fmt(n) {
    return typeof n === 'number' ? n.toFixed(1) : '?';
}

/**
 * The line that says what will be written for the fields the operator is not
 * being asked about. They are shown rather than hidden because they are going
 * into the log either way, and a wrong band in an ADIF file is the sort of
 * thing that is only noticed months later by whoever imports it.
 */
function describe(d) {
    const bits = [];
    if (typeof d.frequencyMhz === 'number' && d.frequencyMhz > 0) {
        bits.push(`${d.frequencyMhz.toFixed(4)} MHz`);
    }
    if (d.band)        bits.push(d.band);
    if (d.mode)        bits.push(d.mode);
    if (d.whenUtc)     bits.push(`${String(d.whenUtc).replace('T', ' ').slice(0, 19)} UTC`);
    if (d.stationCall) bits.push(`as ${d.stationCall}`);
    return bits.length
        ? bits.join('  ·  ') + '  ·  from the radio and the clock'
        : 'No frequency from the radio - the log entry will have none.';
}
