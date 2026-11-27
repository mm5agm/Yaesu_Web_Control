// Radio menu reminder: when YWC starts, ask the user to save the radio's
// settings if there is no save yet, the last one is a month old, or a front
// panel level (MIC GAIN, AMC, PROC, power...) differs from it. The check is
// GET /api/radiomenu/reminder, which only reads the radio. "Not now" hides
// it until YWC starts again (and a week for the old/changed reminder).
(function () {
    'use strict';
    // Not in tear-off windows, frames or the exit page.
    if (window.opener || window.self !== window.top) return;
    if (/^\/exit/i.test(location.pathname)) return;

    const RETRY_MS = 5000;
    const GIVE_UP_AFTER = 60; // about 5 minutes waiting for the radio
    let tries = 0;

    function el(tag, cls, text) {
        const e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text) e.textContent = text;
        return e;
    }

    function show(data) {
        const main = document.querySelector('main');
        if (!main || document.getElementById('radioMenuReminder')) return;
        const box = el('div', 'alert alert-info d-flex flex-wrap align-items-center gap-2 mt-2 mb-2');
        box.id = 'radioMenuReminder';
        box.setAttribute('role', 'status');
        const text = el('span', 'me-auto');
        text.append(el('strong', null, 'Save your radio settings? '), data.message || '');
        const status = el('span', 'small w-100');
        status.setAttribute('aria-live', 'polite');
        const save = el('button', 'btn btn-sm btn-primary', 'Save now');
        save.type = 'button';
        save.id = 'radioMenuReminderSave';
        const later = el('button', 'btn btn-sm btn-outline-secondary', 'Not now');
        later.type = 'button';
        later.id = 'radioMenuReminderLater';
        const where = el('a', 'small', 'Backup & Restore in Settings');
        where.href = '/Settings';

        save.addEventListener('click', async () => {
            save.disabled = later.disabled = true;
            status.className = 'small w-100 text-muted';
            status.textContent = 'Saving: reading the menu, then the front panel levels in each mode (about 15 seconds). Please do not transmit from another program meanwhile.';
            try {
                const r = await fetch('/api/radiomenu/save', { method: 'POST' });
                const d = await r.json().catch(() => ({}));
                if (!r.ok) throw new Error(d.error || r.statusText);
                box.className = 'alert alert-success d-flex flex-wrap align-items-center gap-2 mt-2 mb-2';
                text.textContent = `Saved to ${d.path}.` +
                    (d.knobsStopped ? ` Front panel levels not all saved: ${d.knobsStopped}` : '');
                status.textContent = '';
                save.remove();
                later.textContent = 'Close';
                later.disabled = false;
                saved = true;
                document.dispatchEvent(new CustomEvent('radiomenu-saved', { detail: d.name }));
            } catch (e) {
                status.className = 'small w-100 text-danger';
                status.textContent = `Not saved: ${e.message}`;
                save.disabled = later.disabled = false;
            }
        });

        let saved = false;
        later.addEventListener('click', async () => {
            box.remove();
            if (saved) return;
            try { await fetch('/api/radiomenu/reminder/later', { method: 'POST' }); } catch { /* shown again next start */ }
        });

        box.append(text, save, later, where, status);
        main.prepend(box);
    }

    async function check() {
        let data;
        try {
            const r = await fetch('/api/radiomenu/reminder');
            data = await r.json();
        } catch {
            data = { supported: true, pending: true };
        }
        if (!data.supported) return;
        if (data.pending) {
            if (++tries < GIVE_UP_AFTER) setTimeout(check, RETRY_MS);
            return;
        }
        if (data.show) show(data);
    }

    // Let the page and the radio connection settle first.
    setTimeout(check, 3000);
})();
