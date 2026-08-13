// Floating memories panel — open/close, drag, tile rendering, recall on click.

const MEM_PANEL_KEY = 'memoriesPanel';

// Prompt dialog (same dark style as _memConfirm but with a text input).
function _memPrompt(message, defaultValue = '') {
    return new Promise(resolve => {
        const dlg = document.createElement('dialog');
        dlg.style.cssText = [
            'border-radius:10px', 'border:2px solid #555', 'background:#1e1e2e',
            'color:#e0e0e0', 'padding:20px 24px', 'max-width:420px', 'width:90vw',
            'z-index:10001', 'box-shadow:0 10px 40px rgba(0,0,0,0.7)'
        ].join(';');
        dlg.innerHTML =
            `<p style="margin:0 0 12px;font-size:0.88rem">${_esc(message)}</p>` +
            `<input type="text" maxlength="12" value="${_esc(defaultValue)}" ` +
            `style="width:100%;padding:6px 10px;border-radius:5px;border:1px solid #666;` +
            `background:#2d2d44;color:#e0e0e0;font-size:0.88rem;margin-bottom:14px;box-sizing:border-box;">` +
            `<div style="display:flex;justify-content:flex-end;gap:8px">` +
            `<button data-r="cancel" style="padding:4px 14px;border-radius:5px;border:1px solid #666;background:#2d2d44;color:#ccc;cursor:pointer;font-size:0.85rem">Cancel</button>` +
            `<button data-r="ok" style="padding:4px 14px;border-radius:5px;border:1px solid #4a9;background:#2a5a3a;color:#cfe;cursor:pointer;font-size:0.85rem">OK</button>` +
            `</div>`;
        document.body.appendChild(dlg);
        dlg.showModal();
        const input = dlg.querySelector('input');
        input.focus();
        input.select();
        function finish(v) { dlg.close(); dlg.remove(); resolve(v); }
        dlg.querySelector('[data-r="ok"]').addEventListener('click', () => finish(input.value));
        dlg.querySelector('[data-r="cancel"]').addEventListener('click', () => finish(null));
        dlg.addEventListener('cancel', () => finish(null));
        input.addEventListener('keydown', e => {
            if (e.key === 'Enter') { e.preventDefault(); finish(input.value); }
        });
    });
}

const _MODES = ['LSB','USB','CW-U','CW-L','AM','AM-N','FM','FM-N','RTTY-L','RTTY-U','DATA-L','DATA-U','DATA-FM','DATA-FM-N','PSK'];

// Select dialog — like _memPrompt but with a <select>.
function _memSelect(message, currentValue) {
    return new Promise(resolve => {
        const dlg = document.createElement('dialog');
        dlg.style.cssText = [
            'border-radius:10px', 'border:2px solid #555', 'background:#1e1e2e',
            'color:#e0e0e0', 'padding:20px 24px', 'max-width:320px', 'width:90vw',
            'z-index:10001', 'box-shadow:0 10px 40px rgba(0,0,0,0.7)'
        ].join(';');
        const opts = _MODES.map(m =>
            `<option value="${m}"${m === currentValue ? ' selected' : ''}>${m}</option>`
        ).join('');
        dlg.innerHTML =
            `<p style="margin:0 0 12px;font-size:0.88rem">${_esc(message)}</p>` +
            `<select style="width:100%;padding:6px 10px;border-radius:5px;border:1px solid #666;` +
            `background:#2d2d44;color:#e0e0e0;font-size:0.88rem;margin-bottom:14px;box-sizing:border-box;">${opts}</select>` +
            `<div style="display:flex;justify-content:flex-end;gap:8px">` +
            `<button data-r="cancel" style="padding:4px 14px;border-radius:5px;border:1px solid #666;background:#2d2d44;color:#ccc;cursor:pointer;font-size:0.85rem">Cancel</button>` +
            `<button data-r="ok" style="padding:4px 14px;border-radius:5px;border:1px solid #4a9;background:#2a5a3a;color:#cfe;cursor:pointer;font-size:0.85rem">OK</button>` +
            `</div>`;
        document.body.appendChild(dlg);
        dlg.showModal();
        const sel = dlg.querySelector('select');
        sel.focus();
        function finish(v) { dlg.close(); dlg.remove(); resolve(v); }
        dlg.querySelector('[data-r="ok"]').addEventListener('click', () => finish(sel.value));
        dlg.querySelector('[data-r="cancel"]').addEventListener('click', () => finish(null));
        dlg.addEventListener('cancel', () => finish(null));
    });
}

// Replaces native confirm() to avoid the "localhost:8080 says" browser header.
function _memConfirm(message) {
    return new Promise(resolve => {
        const dlg = document.createElement('dialog');
        dlg.style.cssText = [
            'border-radius:10px', 'border:2px solid #555', 'background:#1e1e2e',
            'color:#e0e0e0', 'padding:20px 24px', 'max-width:420px', 'width:90vw',
            'z-index:10001', 'box-shadow:0 10px 40px rgba(0,0,0,0.7)'
        ].join(';');
        dlg.innerHTML =
            `<p style="margin:0 0 18px;white-space:pre-wrap;font-size:0.88rem;line-height:1.5">${
                _esc(message)
            }</p>` +
            `<div style="display:flex;justify-content:flex-end;gap:8px">` +
            `<button data-r="0" style="padding:4px 14px;border-radius:5px;border:1px solid #666;background:#2d2d44;color:#ccc;cursor:pointer;font-size:0.85rem">Cancel</button>` +
            `<button data-r="1" style="padding:4px 14px;border-radius:5px;border:1px solid #4a9;background:#2a5a3a;color:#cfe;cursor:pointer;font-size:0.85rem">OK</button>` +
            `</div>`;
        document.body.appendChild(dlg);
        dlg.showModal();
        function finish(v) { dlg.close(); dlg.remove(); resolve(v); }
        dlg.querySelectorAll('button').forEach(b =>
            b.addEventListener('click', () => finish(b.dataset.r === '1')));
        dlg.addEventListener('cancel', () => finish(false));
    });
}

let _memories = [];
let _panelOpen = false;
let _banks = [];

export function initMemoriesPanel() {
    const dialog = document.getElementById('memoriesDialog');
    const header = document.getElementById('memoriesHeader');
    if (!dialog) return;

    // Restore saved position
    _restorePosition(dialog);

    // Drag support (mouse + touch) on header
    _makeDraggable(dialog, header);

    // Close button
    document.getElementById('memoriesClose')?.addEventListener('click', closeMemoriesPanel);

    // Refresh button
    document.getElementById('memoriesRefresh')?.addEventListener('click', _loadAndRender);

    window.openMemoriesPanel = openMemoriesPanel;
    // Delegated: Flex clones #memBtn into a panel after this init runs.
    if (!document.documentElement.dataset.ywcMemBtnDelegated) {
        document.documentElement.dataset.ywcMemBtnDelegated = '1';
        document.addEventListener('click', (e) => {
            if (e.target.closest('#memBtn')) openMemoriesPanel();
        });
    }

    // Bank switcher
    document.getElementById('memBankSelect')?.addEventListener('change', e => _switchBank(e.target.value));

    // Reload when dialog opened
    dialog.addEventListener('toggle', () => {
        if (dialog.open) { _loadBanks(); _loadAndRender(); }
    });

    // Escape always closes the panel (show() does not auto-close on Escape).
    // Capture phase, matching freq-keyboard.js's pattern: its own capture-phase
    // Escape handler calls stopPropagation(), which — since it fires at document,
    // the first node in the capture path — would otherwise swallow the event
    // before any bubble-phase document listener ever ran (e.g. both panels open
    // at once). Capture-phase listeners on the same node still all run regardless
    // of a sibling's stopPropagation(); only stopImmediatePropagation would block us.
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape' && dialog.open) {
            e.preventDefault();
            closeMemoriesPanel();
        }
    }, true);

    // Refresh when the user returns to this tab (e.g. after editing in /Memories)
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden && dialog.open) _loadAndRender();
    });

    // Allow non-module scripts (e.g. site.js) to trigger a refresh after saving
    window.refreshMemoriesPanel = () => {
        if (dialog.open) _loadAndRender();
    };
}

export function openMemoriesPanel() {
    const dialog = document.getElementById('memoriesDialog');
    if (!dialog) return;
    if (!dialog.open) {
        dialog.show();   // non-modal so the rest of the UI stays interactive
        _panelOpen = true;
        _loadBanks();
        _loadAndRender();
    }
}

export function closeMemoriesPanel() {
    const dialog = document.getElementById('memoriesDialog');
    if (dialog && dialog.open) {
        dialog.close();
        _panelOpen = false;
    }
}

async function _loadAndRender() {
    const container = document.getElementById('memoriesTiles');
    if (!container) return;

    container.innerHTML = '<div class="text-muted small p-2">Loading…</div>';

    try {
        const resp = await fetch('/api/memory');
        if (!resp.ok) throw new Error(`Server returned HTTP ${resp.status}`);
        _memories = await resp.json();
        _renderTiles(container);
    } catch (e) {
        const msg = e instanceof TypeError
            ? 'Server not responding — is Yaesu Web Control still running?'
            : `Failed to load memories: ${e.message}`;
        container.innerHTML = `<div class="text-danger small p-2">${msg}</div>`;
    }
}

// Sentinel value used for the always-present built-in YWC starter bank entry.
// Must match the Memories editor page constant of the same name.
const STARTER_BANK_VALUE = '__ywc_starter__';
// Sentinel for the "split the starter bank into per-mode banks" action.
// Selecting this from the dropdown triggers the themed-create flow rather
// than a bank load.
const CREATE_THEMED_VALUE = '__ywc_create_themed__';

async function _loadBanks() {
    const sel = document.getElementById('memBankSelect');
    if (!sel) return;
    try {
        const resp = await fetch('/api/memorybank');
        if (!resp.ok) return;
        _banks = await resp.json();
        sel.innerHTML = '<option value="">Banks…</option>';
        // Always-available built-in starter bank entry. Sits at the top of
        // the dropdown so users can find the YWC factory memories without
        // visiting the Memories editor page.
        const starterOpt = document.createElement('option');
        starterOpt.value       = STARTER_BANK_VALUE;
        starterOpt.textContent = '📥 YWC Starter Bank (built-in)';
        sel.appendChild(starterOpt);
        // Action entry: split the starter bank into themed banks (FT8, CW,
        // SSB, RTTY, FM) the user can load individually.
        const themedOpt = document.createElement('option');
        themedOpt.value       = CREATE_THEMED_VALUE;
        themedOpt.textContent = '🪄 Create themed banks…';
        sel.appendChild(themedOpt);
        for (const name of _banks) {
            const opt = document.createElement('option');
            opt.value = name;
            opt.textContent = name;
            sel.appendChild(opt);
        }
        // Always visible now — the starter bank guarantees at least one
        // selectable entry beyond the placeholder.
        sel.style.display = '';
    } catch { /* ignore — banks are optional */ }
}

async function _switchBank(name) {
    if (!name) return;
    if (name === CREATE_THEMED_VALUE) {
        await _createThemedBanks();
        // Reset the dropdown — themed-create is an action, not a bank to stay
        // selected on. Refresh the list so newly created banks appear.
        const sel = document.getElementById('memBankSelect');
        if (sel) sel.value = '';
        await _loadBanks();
        return;
    }
    const statusEl = document.getElementById('memToolbarStatus');
    if (statusEl) statusEl.textContent = 'Loading bank…';
    try {
        // Built-in starter bank uses its own endpoint, otherwise the standard
        // bank-load endpoint. Both replace the current memories so the bank's
        // entries appear in the Mem panel for normal use (click to QSY,
        // edit, delete etc.) — identical behaviour either way.
        const isStarter = name === STARTER_BANK_VALUE;
        const url  = isStarter ? '/api/memory/starter-bank/load' : `/api/memorybank/${encodeURIComponent(name)}/load`;
        const init = isStarter
            ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mode: 'replace' }) }
            : { method: 'POST' };
        const resp = await fetch(url, init);
        if (resp.ok) {
            await _loadAndRender();
            if (statusEl) statusEl.textContent = isStarter ? '✓ Starter bank loaded' : `✓ Loaded "${name}"`;
            setTimeout(() => { if (statusEl) statusEl.textContent = ''; }, 3000);
        } else {
            if (statusEl) statusEl.textContent = '✗ Load failed';
        }
    } catch (e) {
        if (statusEl) statusEl.textContent = '✗ Error loading bank';
    }
}

// Split the region starter bank into themed sub-banks (FT8, FT4, CW, SSB,
// RTTY, FM). First pass leaves any existing same-name banks alone; user can
// opt in to overwriting clashes on the second pass.
async function _createThemedBanks() {
    const statusEl = document.getElementById('memToolbarStatus');
    if (!await _memConfirm(
        'Create themed banks from the YWC starter bank?\n\n' +
        'This will create banks named FT8, FT4, CW, SSB, RTTY and FM\n' +
        '(skipping any that are empty for your region).\n\n' +
        'Existing banks with the same name are left untouched —\n' +
        'you\'ll be asked separately if any are found.')) return;

    if (statusEl) statusEl.textContent = 'Creating themed banks…';
    try {
        let resp = await fetch('/api/memory/starter-bank/create-themed-banks', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ overwrite: false })
        });
        if (!resp.ok) { if (statusEl) statusEl.textContent = '✗ Create failed'; return; }
        let result = await resp.json();

        if (result.skipped && result.skipped.length > 0) {
            const list = result.skipped.join(', ');
            if (await _memConfirm(
                `These banks already exist and were not changed:\n  ${list}\n\n` +
                'Overwrite them with the starter-bank versions?')) {
                resp = await fetch('/api/memory/starter-bank/create-themed-banks', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ overwrite: true })
                });
                if (resp.ok) result = await resp.json();
            }
        }

        if (statusEl) {
            statusEl.textContent = result.created.length > 0
                ? `✓ Created ${result.created.length} themed bank${result.created.length === 1 ? '' : 's'}: ${result.created.join(', ')}`
                : '✓ No banks created — all themed names already exist (overwrite declined).';
            setTimeout(() => { if (statusEl) statusEl.textContent = ''; }, 5000);
        }
    } catch {
        if (statusEl) statusEl.textContent = '✗ Error';
    }
}

// ── Context menu ─────────────────────────────────────────────────────────────

let _ctxTargetId = null;

function _ensureContextMenu() {
    if (document.getElementById('memCtxMenu')) return;
    const menu = document.createElement('div');
    menu.id = 'memCtxMenu';
    menu.style.cssText = [
        'position:fixed', 'z-index:10002', 'display:none',
        'background:#1e1e2e', 'border:1px solid #555', 'border-radius:6px',
        'box-shadow:0 4px 20px rgba(0,0,0,0.6)', 'min-width:140px',
        'overflow:hidden', 'font-size:0.85rem', 'user-select:none'
    ].join(';');

    const items = [
        { id: 'memCtxRecall', label: '↵ Recall',       color: '#e0e0e0' },
        { id: 'memCtxSep',    label: null,              color: null },
        { id: 'memCtxRename', label: '✎ Rename',       color: '#e0e0e0' },
        { id: 'memCtxMode',   label: '⇄ Change Mode',  color: '#e0e0e0' },
        { id: 'memCtxDelete', label: '✕ Delete',       color: '#f88' },
    ];
    menu.innerHTML = items.map(it =>
        it.label === null
            ? `<div style="border-top:1px solid #444;margin:2px 0;"></div>`
            : `<div id="${it.id}" style="padding:8px 16px;cursor:pointer;color:${it.color}">${it.label}</div>`
    ).join('');
    document.body.appendChild(menu);

    menu.querySelectorAll('div[id]').forEach(el => {
        el.addEventListener('mouseenter', () => el.style.background = '#2d2d44');
        el.addEventListener('mouseleave', () => el.style.background = '');
    });

    document.getElementById('memCtxRecall').addEventListener('click', () => {
        const id = _ctxTargetId;
        _hideContextMenu();
        if (id !== null) _recallMemory(id);
    });

    document.getElementById('memCtxRename').addEventListener('click', async () => {
        const id = _ctxTargetId;
        _hideContextMenu();
        if (id === null) return;
        const mem = _memories.find(m => m.id === id);
        if (!mem) return;
        const newLabel = await _memPrompt('Rename memory (max 12 characters):', mem.label || '');
        if (newLabel === null) return;
        try {
            await fetch(`/api/memory/${id}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ...mem, label: newLabel.trim() })
            });
            await _loadAndRender();
        } catch (e) { console.error('Rename failed:', e); }
    });

    document.getElementById('memCtxMode').addEventListener('click', async () => {
        const id = _ctxTargetId;
        _hideContextMenu();
        if (id === null) return;
        const mem = _memories.find(m => m.id === id);
        if (!mem) return;
        const newMode = await _memSelect('Change mode:', mem.mode);
        if (newMode === null) return;
        try {
            await fetch(`/api/memory/${id}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ...mem, mode: newMode })
            });
            await _loadAndRender();
        } catch (e) { console.error('Change mode failed:', e); }
    });

    document.getElementById('memCtxDelete').addEventListener('click', async () => {
        const id = _ctxTargetId;
        _hideContextMenu();
        if (id === null) return;
        const mem = _memories.find(m => m.id === id);
        const name = mem ? (mem.label || 'Mem ' + mem.id) : 'this memory';
        if (!await _memConfirm(`Delete "${name}"?`)) return;
        try {
            await fetch(`/api/memory/${id}`, { method: 'DELETE' });
            await _loadAndRender();
        } catch (e) { console.error('Delete failed:', e); }
    });

    document.addEventListener('click', e => {
        if (!menu.contains(e.target)) _hideContextMenu();
    }, { capture: true });
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape') _hideContextMenu();
    });
}

function _showContextMenu(e, id) {
    _ensureContextMenu();
    _ctxTargetId = id;
    const menu = document.getElementById('memCtxMenu');
    menu.style.display = 'block';
    let x = e.clientX, y = e.clientY;
    menu.style.left = x + 'px';
    menu.style.top  = y + 'px';
    const r = menu.getBoundingClientRect();
    if (r.right  > window.innerWidth)  menu.style.left = (x - r.width)  + 'px';
    if (r.bottom > window.innerHeight) menu.style.top  = (y - r.height) + 'px';
}

function _hideContextMenu() {
    const menu = document.getElementById('memCtxMenu');
    if (menu) menu.style.display = 'none';
    _ctxTargetId = null;
}

function _renderTiles(container) {
    if (_memories.length === 0) {
        container.innerHTML =
            '<div class="text-muted small p-3">No memories saved yet. ' +
            '<a href="/Memories" target="_blank">Open the Memories editor</a> to add some.</div>';
        return;
    }

    const frag = document.createDocumentFragment();
    for (const mem of _memories) {
        const item = document.createElement('div');
        item.setAttribute('role', 'listitem');

        const tile = document.createElement('button');
        tile.type = 'button';
        tile.className = 'mem-tile';
        const label = mem.label || ('Mem ' + mem.id);
        tile.title = `Recall: ${label}`;
        tile.dataset.memId = mem.id;

        const mhz = mem.frequencyHz >= 1000
            ? (mem.frequencyHz / 1e6).toFixed(mem.frequencyHz % 1000 === 0 ? 3 : 6).replace(/\.?0+$/, '')
            : (mem.frequencyHz / 1e6).toFixed(6);

        tile.setAttribute('aria-label', `Recall ${label}, ${mhz} MHz, ${mem.mode}`);
        tile.innerHTML =
            `<span class="mem-tile-label" aria-hidden="true">${_esc(label)}</span>` +
            `<span class="mem-tile-freq" aria-hidden="true">${mhz} MHz</span>` +
            `<span class="mem-tile-mode" aria-hidden="true">${_esc(mem.mode)}</span>`;

        tile.addEventListener('click', () => _recallMemory(mem.id));
        tile.addEventListener('contextmenu', e => { e.preventDefault(); _showContextMenu(e, mem.id); });
        item.appendChild(tile);
        frag.appendChild(item);
    }

    container.innerHTML = '';
    container.appendChild(frag);
}

async function _recallMemory(id) {
    const tile = document.querySelector(`.mem-tile[data-mem-id="${id}"]`);
    if (tile) {
        tile.classList.add('mem-tile-active');
        setTimeout(() => tile.classList.remove('mem-tile-active'), 800);
    }
    try {
        await fetch(`/api/memory/${id}/recall`, { method: 'POST' });
    } catch (e) {
        console.error('Memory recall failed:', e);
    }
}

function _esc(str) {
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

// ── Drag ─────────────────────────────────────────────────────────────────────

function _makeDraggable(dialog, handle) {
    if (!handle) return;

    let startX, startY, origLeft, origTop;

    function onMove(cx, cy) {
        let newLeft = origLeft + (cx - startX);
        let newTop  = origTop  + (cy - startY);
        // Clamp inside viewport
        newLeft = Math.max(0, Math.min(window.innerWidth  - dialog.offsetWidth,  newLeft));
        newTop  = Math.max(0, Math.min(window.innerHeight - dialog.offsetHeight, newTop));
        dialog.style.left      = newLeft + 'px';
        dialog.style.top       = newTop  + 'px';
        dialog.style.margin    = '0';
        dialog.style.transform = 'none';
    }

    function onEnd() {
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup',   onEnd);
        document.removeEventListener('touchmove', onTouchMove);
        document.removeEventListener('touchend',  onEnd);
        _savePosition(dialog);
    }

    function onMouseMove(e) { onMove(e.clientX, e.clientY); }
    function onTouchMove(e) { onMove(e.touches[0].clientX, e.touches[0].clientY); }

    handle.addEventListener('mousedown', e => {
        if (e.button !== 0) return;
        if (e.target.closest('a, button')) return;
        startX = e.clientX; startY = e.clientY;
        const r = dialog.getBoundingClientRect();
        origLeft = r.left; origTop = r.top;
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup',   onEnd);
        e.preventDefault();
    });

    handle.addEventListener('touchstart', e => {
        if (e.target.closest('a, button')) return;
        startX = e.touches[0].clientX; startY = e.touches[0].clientY;
        const r = dialog.getBoundingClientRect();
        origLeft = r.left; origTop = r.top;
        document.addEventListener('touchmove', onTouchMove, { passive: false });
        document.addEventListener('touchend',  onEnd);
    }, { passive: true });
}

function _savePosition(dialog) {
    const r = dialog.getBoundingClientRect();
    localStorage.setItem(MEM_PANEL_KEY + '_pos',
        JSON.stringify({ left: r.left, top: r.top }));
}

// ── Import / Export ──────────────────────────────────────────────────────────

const _TOOLBAR_BTNS = ['memImportReplaceBtn', 'memImportAddBtn', 'memExportReplaceBtn', 'memExportAddBtn'];

function _setToolbarBusy(busy, status) {
    _TOOLBAR_BTNS.forEach(id => {
        const el = document.getElementById(id);
        if (el) el.disabled = busy;
    });
    const s = document.getElementById('memToolbarStatus');
    if (!s) return;
    if (busy) {
        s.innerHTML = '<span class="spinner-border spinner-border-sm me-1 align-middle" role="status" aria-hidden="true"></span>' + (status || '');
    } else {
        s.textContent = status || '';
    }
}

window.importMemories = async function (mode) {
    const msg = mode === 'replace'
        ? 'Load from Rig (Replace all):\n\nThis will replace ALL app memories with channels read from the radio. Your current app memories will be lost.\n\nContinue?'
        : 'Load from Rig (Add new):\n\nThis will add radio channels to your existing app memories. Nothing will be deleted.\n\nContinue?';
    if (!await _memConfirm(msg)) return;
    _setToolbarBusy(true, 'Reading rig — this may take up to 30 s…');
    try {
        const resp = await fetch('/api/memory/import-radio', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mode })
        });
        if (resp.ok) {
            const data = await resp.json();
            if (data.warning) {
                _setToolbarBusy(false, `⚠ ${data.warning}`);
            } else {
                _setToolbarBusy(false, `✓ Loaded ${data.imported} from rig`);
                await _loadAndRender();
            }
        } else {
            _setToolbarBusy(false, '✗ Load failed — is the radio connected?');
        }
    } catch (e) {
        _setToolbarBusy(false, '✗ Error');
        console.error('Memory import failed:', e);
    }
};

window.exportMemories = async function (mode) {
    if (mode === 'replace') {
        const count = _memories.length;
        const msg = `Save to Rig (Replace all):\n\nThis will write your ${count} app ${count === 1 ? 'memory' : 'memories'} to the radio, replacing ALL existing radio channels.\n\nAny memories stored on the rig that are not in the app will be lost.\n\nContinue?`;
        if (!await _memConfirm(msg)) return;
        _setToolbarBusy(true, 'Writing to rig…');
        try {
            const resp = await fetch('/api/memory/export-radio', { method: 'POST' });
            if (resp.ok) {
                const data = await resp.json();
                _setToolbarBusy(false, `✓ Saved ${data.written} to rig`);
            } else {
                _setToolbarBusy(false, '✗ Save failed — is the radio connected?');
            }
        } catch (e) {
            _setToolbarBusy(false, '✗ Error');
            console.error('Memory export failed:', e);
        }
    } else {
        const count = _memories.length;
        const msg = `Save to Rig (Add new):\n\nThis will write your ${count} app ${count === 1 ? 'memory' : 'memories'} into empty channels on the rig.\n\nExisting rig channels will not be touched.\n\nContinue?`;
        if (!await _memConfirm(msg)) return;
        _setToolbarBusy(true, 'Scanning rig for empty channels…');
        try {
            const resp = await fetch('/api/memory/export-radio-add', { method: 'POST' });
            if (resp.ok) {
                const data = await resp.json();
                if (data.written === 0 && data.noRoom > 0) {
                    _setToolbarBusy(false,
                        `Rig is full — no empty channels available. Use "Save to Rig (Replace all)" or free up some channels first.`);
                } else if (data.noRoom > 0) {
                    _setToolbarBusy(false,
                        `✓ Saved ${data.written} to rig. ${data.noRoom} couldn't fit — rig is full.`);
                } else {
                    _setToolbarBusy(false, `✓ Saved ${data.written} to rig`);
                }
            } else {
                _setToolbarBusy(false, '✗ Save failed — is the radio connected?');
            }
        } catch (e) {
            _setToolbarBusy(false, '✗ Error');
            console.error('Memory export-add failed:', e);
        }
    }
};

// ── Position persistence ──────────────────────────────────────────────────────

function _restorePosition(dialog) {
    try {
        const saved = localStorage.getItem(MEM_PANEL_KEY + '_pos');
        if (!saved) return;
        const { left, top } = JSON.parse(saved);
        // Discard saved position if it would put the dialog mostly off-screen
        if (left < 0 || left > window.innerWidth - 100) return;
        dialog.style.left      = Math.max(0, Math.min(window.innerWidth  - 320, left)) + 'px';
        dialog.style.top       = Math.max(0, Math.min(window.innerHeight - 200, top))  + 'px';
        dialog.style.margin    = '0';
        dialog.style.transform = 'none';
    } catch { /* ignore */ }
}
