/**
 * Named Flex UI layout administration, shared by the Flex UI "Manage layouts"
 * dialog and the Settings page.
 *
 * Pure server CRUD against /api/flexlayouts — it never touches the live
 * workspace. On /flexui the caller passes `onSelect` so a row can switch the
 * active arrangement; on Settings there is no live workspace and no onSelect.
 *
 * The layout payload is opaque to this module too: it is read, duplicated,
 * exported and imported whole, never inspected.
 */

const API = '/api/flexlayouts';

export function initFlexLayoutsAdmin(container, opts = {}) {
    if (!container) return { refresh: () => {} };

    const state = {
        presets: [],
        activeId: null,
        onSelect: typeof opts.onSelect === 'function' ? opts.onSelect : null,
        onChange: typeof opts.onChange === 'function' ? opts.onChange : null,
    };

    container.replaceChildren();

    const toolbar = document.createElement('div');
    toolbar.className = 'd-flex align-items-center gap-2 mb-2';

    const importBtn = document.createElement('button');
    importBtn.type = 'button';
    importBtn.className = 'btn btn-sm btn-outline-secondary';
    importBtn.textContent = 'Import layout…';

    const importInput = document.createElement('input');
    importInput.type = 'file';
    importInput.accept = 'application/json,.json';
    importInput.hidden = true;
    importBtn.addEventListener('click', () => importInput.click());
    importInput.addEventListener('change', () => importFile(importInput.files?.[0]));

    const status = document.createElement('span');
    status.className = 'small text-muted';
    status.setAttribute('role', 'status');
    status.setAttribute('aria-live', 'polite');

    toolbar.append(importBtn, importInput, status);

    const list = document.createElement('div');
    list.className = 'flex-layouts-admin__list';
    list.setAttribute('role', 'list');

    container.append(toolbar, list);

    function setStatus(message) {
        status.textContent = message || '';
    }

    async function refresh() {
        try {
            const res = await fetch(API, { headers: { Accept: 'application/json' } });
            if (!res.ok) throw new Error(String(res.status));
            const snap = await res.json();
            state.presets = Array.isArray(snap.layouts) ? snap.layouts : [];
            state.activeId = snap.activeId || '__default__';
            setStatus('');
        } catch {
            state.presets = [];
            setStatus('Could not load layouts.');
        }
        render();
    }

    function render() {
        list.replaceChildren();
        if (state.presets.length === 0) {
            const empty = document.createElement('p');
            empty.className = 'text-muted small mb-0';
            empty.textContent = 'No saved layouts yet. On the Flex UI, arrange the panels and use “Save current as…”.';
            list.appendChild(empty);
            return;
        }
        for (const preset of state.presets) list.appendChild(buildRow(preset));
    }

    function buildRow(preset) {
        const row = document.createElement('div');
        row.className = 'flex-layouts-admin__row d-flex align-items-center gap-2 py-1';
        row.setAttribute('role', 'listitem');

        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'form-control form-control-sm';
        input.value = preset.name || '';
        input.maxLength = 40;
        input.setAttribute('aria-label', `Name for layout ${preset.name || ''}`);

        const isActive = preset.id === state.activeId;
        const badge = document.createElement('span');
        badge.className = `badge ${isActive ? 'text-bg-success' : 'text-bg-secondary'}`;
        badge.textContent = isActive ? 'Active' : '';
        badge.hidden = !isActive;

        row.append(input, badge);

        if (state.onSelect) {
            row.appendChild(button('Use', () => state.onSelect(preset.id)));
        }
        row.append(
            button('Rename', () => {
                const name = input.value.trim();
                if (!name || name === preset.name) return;
                mutate(() => fetch(`${API}/${encodeURIComponent(preset.id)}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ name }),
                }), 'Renamed');
            }),
            button('Duplicate', () => duplicate(preset)),
            button('Export', () => exportPreset(preset)),
            button('Delete', () => confirmDelete(preset), 'btn-outline-danger'),
        );

        return row;
    }

    function button(label, onClick, extraClass = '') {
        const b = document.createElement('button');
        b.type = 'button';
        b.className = `btn btn-sm btn-outline-secondary ${extraClass}`.trim();
        b.textContent = label;
        b.addEventListener('click', onClick);
        return b;
    }

    async function mutate(action, okMessage) {
        try {
            const res = await action();
            if (!res.ok) {
                const body = await res.json().catch(() => null);
                setStatus(body?.error || `Request failed (${res.status}).`);
                return;
            }
            setStatus(okMessage || '');
            await refresh();
            state.onChange?.();
        } catch {
            setStatus('Request failed.');
        }
    }

    async function fetchPreset(id) {
        try {
            const res = await fetch(`${API}/${encodeURIComponent(id)}`, {
                headers: { Accept: 'application/json' },
            });
            if (!res.ok) return null;
            return await res.json();
        } catch {
            return null;
        }
    }

    async function duplicate(preset) {
        const detail = await fetchPreset(preset.id);
        if (!detail) { setStatus('Could not read that layout.'); return; }
        await mutate(() => fetch(API, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: nextCopyName(preset.name), layout: detail.layout }),
        }), 'Duplicated');
    }

    function nextCopyName(base) {
        const taken = new Set(state.presets.map((p) => (p.name || '').toLowerCase()));
        let n = 1;
        let name;
        do {
            name = n === 1 ? `${base} copy` : `${base} copy ${n}`;
            n++;
        } while (taken.has(name.toLowerCase()));
        return name.slice(0, 40);
    }

    function confirmDelete(preset) {
        if (!window.confirm(`Delete the layout “${preset.name}”? This cannot be undone.`)) return;
        mutate(() => fetch(`${API}/${encodeURIComponent(preset.id)}`, { method: 'DELETE' }), 'Deleted');
    }

    async function exportPreset(preset) {
        const detail = await fetchPreset(preset.id);
        if (!detail) { setStatus('Could not read that layout.'); return; }
        const blob = new Blob(
            [JSON.stringify({ name: detail.name, layout: detail.layout }, null, 2)],
            { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `${sanitise(detail.name) || 'layout'}.json`;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    }

    async function importFile(file) {
        if (!file) return;
        try {
            const doc = JSON.parse(await file.text());
            const layout = doc?.layout ?? doc;
            const fallback = file.name.replace(/\.json$/i, '') || 'Imported';
            const name = (doc?.name || fallback).slice(0, 40);
            await mutate(() => fetch(API, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, layout }),
            }), 'Imported');
        } catch {
            setStatus('That file is not a valid layout JSON.');
        } finally {
            importInput.value = '';
        }
    }

    function sanitise(name) {
        return (name || '').replace(/[^A-Za-z0-9-_ ]+/g, '').trim().replace(/\s+/g, '-');
    }

    refresh();
    return { refresh };
}

/**
 * Makes a <dialog>'s header a drag handle, clamping the dialog inside the
 * viewport. Same interaction as the memories / frequency-keyboard dialogs.
 * Idempotent per handle.
 */
export function enableDialogDrag(dialog, handle) {
    if (!dialog || !handle || handle.dataset.ywcDragWired === '1') return;
    handle.dataset.ywcDragWired = '1';

    let startX = 0;
    let startY = 0;
    let originLeft = 0;
    let originTop = 0;

    function moveTo(x, y) {
        const width = dialog.offsetWidth;
        const height = dialog.offsetHeight;
        const left = Math.max(0, Math.min(window.innerWidth - width, originLeft + (x - startX)));
        const top = Math.max(0, Math.min(window.innerHeight - height, originTop + (y - startY)));
        dialog.style.left = `${left}px`;
        dialog.style.top = `${top}px`;
        // Neutralise the modal's UA inset — without this, right/bottom:0 would
        // stretch the dialog once margin is cleared.
        dialog.style.right = 'auto';
        dialog.style.bottom = 'auto';
        dialog.style.margin = '0';
        dialog.style.transform = 'none';
    }

    const onMouseMove = (e) => moveTo(e.clientX, e.clientY);
    const onTouchMove = (e) => moveTo(e.touches[0].clientX, e.touches[0].clientY);
    function onEnd() {
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onEnd);
        document.removeEventListener('touchmove', onTouchMove);
        document.removeEventListener('touchend', onEnd);
    }

    handle.addEventListener('mousedown', (e) => {
        if (e.button !== 0 || e.target.closest('button, a, input')) return;
        const r = dialog.getBoundingClientRect();
        startX = e.clientX;
        startY = e.clientY;
        originLeft = r.left;
        originTop = r.top;
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onEnd);
        e.preventDefault();
    });

    handle.addEventListener('touchstart', (e) => {
        if (e.target.closest('button, a, input')) return;
        const r = dialog.getBoundingClientRect();
        startX = e.touches[0].clientX;
        startY = e.touches[0].clientY;
        originLeft = r.left;
        originTop = r.top;
        document.addEventListener('touchmove', onTouchMove, { passive: false });
        document.addEventListener('touchend', onEnd);
    }, { passive: true });
}
