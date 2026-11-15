// Settings page search, shared by YWC and IWC.
//
// The index is read from the page, so a new setting is searchable as soon as
// it has a label. Each setting gets a stable name from its form field
// (Settings.DetailedLogging -> detailed-logging), which is also its deep link:
// /Settings#detailed-logging opens the section, scrolls to the setting and
// highlights it.
//
// Words a user might try that aren't on the page live in each app's own
// window.RWC_SETTINGS_KEYWORDS, keyed by that name, because the two apps don't
// have the same settings. Nothing here writes a setting or talks to the radio.
//
// To index something the label finder misses, or to give a setting a fixed
// name, put data-setting="name" on its wrapper. data-search-skip leaves a
// block out.
(function () {
    'use strict';

    const MAX_RESULTS = 30;
    const FLASH_MS = 3000;
    const WRAPPERS = '[data-setting], .form-check, .mb-3, .mb-2, [class*="col-"]';
    const SKIP = '.modal, [data-search-skip]';

    let root, input, list, status;
    let items = [];
    let keywords = {};
    let results = [];
    let savedOpen = null;   // section -> open, while a search is showing
    let flashTimer = null;

    const clean = s => (s || '').replace(/\s+/g, ' ').trim();

    function slug(s) {
        return String(s)
            .replace(/^Settings[._]/, '')
            .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
            .replace(/[^A-Za-z0-9]+/g, '-')
            .replace(/^-+|-+$/g, '')
            .toLowerCase();
    }

    function allSections() {
        return Array.from(document.querySelectorAll('details.settings-section'));
    }

    function sectionTitle(section) {
        const summary = section.querySelector(':scope > summary');
        if (!summary) return '';
        const copy = summary.cloneNode(true);
        copy.querySelectorAll('button').forEach(b => b.remove());
        return clean(copy.textContent);
    }

    function controlFor(el, wrapper) {
        const labelled = el.tagName === 'LABEL' ? el.control : null;
        if (labelled && labelled.type !== 'hidden') return labelled;
        return wrapper.querySelector('input:not([type=hidden]), select, textarea');
    }

    // The name for a deep link: fixed by data-setting, else the form field,
    // else the element id, else the words of the label.
    function nameFor(wrapper, control, text, used) {
        if (wrapper.dataset.setting) return wrapper.dataset.setting;
        const tries = [];
        if (control) {
            if (control.name && control.name.indexOf('Settings.') === 0) tries.push(slug(control.name));
            if (control.id) tries.push(slug(control.id));
        }
        tries.push(slug(text));
        for (const t of tries) if (t && !used.has(t)) return t;
        for (let i = 2; ; i++) if (!used.has(`${tries[0]}-${i}`)) return `${tries[0]}-${i}`;
    }

    // Run again for each search because the page shows and hides settings by
    // radio model. Names already given are kept, so links stay the same.
    function buildIndex() {
        const found = [];
        const used = new Set();
        for (const section of allSections()) {
            const title = sectionTitle(section);
            const body = section.querySelector(':scope > .section-body') || section;

            const name = section.id || section.dataset.setting || slug(title);
            used.add(name);
            section.dataset.setting = name;
            const words = Array.from(body.querySelectorAll('p, button'))
                .filter(e => !e.closest(SKIP))
                .map(e => e.textContent).join(' ');
            found.push({ name, section, sectionTitle: title, el: section, label: title, text: clean(words), isSection: true });

            const seen = new Set();
            for (const el of body.querySelectorAll('label, h5, h6, [data-setting]')) {
                if (el.closest(SKIP)) continue;
                const source = el.matches('label, h5, h6') ? el : el.querySelector('label, h5, h6');
                const text = clean((source || el).textContent);
                if (!text) continue;
                const heading = /^H[56]$/.test(el.tagName);
                let wrapper = heading ? el : el.closest(WRAPPERS);
                if (!wrapper || wrapper === body || !body.contains(wrapper)) wrapper = el;
                if (seen.has(wrapper)) continue;
                seen.add(wrapper);
                const control = heading ? null : controlFor(source || el, wrapper);
                const itemName = nameFor(wrapper, control, text, used);
                used.add(itemName);
                wrapper.dataset.setting = itemName;
                found.push({ name: itemName, section, sectionTitle: title, el: wrapper, control, label: text, text: clean(wrapper.textContent) });
            }
        }
        items = found;
    }

    function isShown(item) {
        for (let n = item.el; n; n = n.parentElement) {
            if (getComputedStyle(n).display === 'none') return false;
            if (n === item.section) break;
        }
        return true;
    }

    // Every word must match somewhere, and at least one outside the section
    // title, or searching "web" would list everything in Web / HTTP.
    function score(item, terms) {
        const label = item.label.toLowerCase();
        const extra = (keywords[item.name] || '').toLowerCase();
        const text = item.text.toLowerCase();
        const title = item.sectionTitle.toLowerCase();
        let total = 0;
        let onlyTitle = !item.isSection;
        for (const term of terms) {
            let s = 0;
            if (label.startsWith(term)) s = 8;
            else if (label.includes(term)) s = 6;
            else if (extra.includes(term)) s = 5;
            else if (text.includes(term)) s = 2;
            else if (title.includes(term)) s = 1;
            if (!s) return 0;
            if (s > 1) onlyTitle = false;
            total += s;
        }
        return onlyTitle ? 0 : total;
    }

    function snippet(item, terms) {
        const text = item.text;
        const lower = text.toLowerCase();
        const label = item.label.toLowerCase();
        const term = terms.find(t => !label.includes(t) && lower.includes(t));
        if (!term) return '';
        const at = lower.indexOf(term);
        const from = Math.max(0, at - 40);
        const to = Math.min(text.length, at + term.length + 60);
        return (from ? '…' : '') + text.slice(from, to) + (to < text.length ? '…' : '');
    }

    function restoreSections() {
        allSections().forEach(s => s.classList.remove('settings-search-filtered'));
        document.querySelectorAll('.settings-search-match').forEach(e => e.classList.remove('settings-search-match'));
        if (savedOpen) {
            savedOpen.forEach((open, section) => { section.open = open; });
            savedOpen = null;
        }
    }

    function showResults(show) {
        list.hidden = !show || !input.value.trim();
        input.setAttribute('aria-expanded', String(!list.hidden));
    }

    function addSpan(parent, className, text) {
        const span = document.createElement('span');
        span.className = className;
        span.textContent = text;
        parent.appendChild(span);
    }

    function renderResults(terms) {
        list.textContent = '';
        for (const { item } of results.slice(0, MAX_RESULTS)) {
            const li = document.createElement('li');
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'settings-search-result';
            b.dataset.target = item.name;
            addSpan(b, 'settings-search-label', item.label);
            addSpan(b, 'settings-search-where', item.isSection ? 'Section' : item.sectionTitle);
            const more = snippet(item, terms);
            if (more) addSpan(b, 'settings-search-snippet', more);
            li.appendChild(b);
            list.appendChild(li);
        }
        if (!results.length) {
            const li = document.createElement('li');
            li.className = 'settings-search-none';
            li.textContent = 'No settings match that.';
            list.appendChild(li);
        }
    }

    function search() {
        const terms = input.value.toLowerCase().split(/\s+/).filter(Boolean);
        restoreSections();
        if (!terms.length) {
            results = [];
            list.textContent = '';
            status.textContent = '';
            showResults(false);
            return;
        }
        buildIndex();
        results = items
            .filter(isShown)
            .map(item => ({ item, s: score(item, terms) }))
            .filter(r => r.s > 0)
            .sort((a, b) => b.s - a.s);

        savedOpen = new Map(allSections().map(s => [s, s.open]));
        const hits = new Set(results.map(r => r.item.section));
        for (const section of allSections()) {
            section.classList.toggle('settings-search-filtered', !hits.has(section));
            if (hits.has(section)) section.open = true;
        }
        for (const { item } of results) {
            if (!item.isSection) item.el.classList.add('settings-search-match');
        }
        renderResults(terms);
        showResults(true);
        const n = results.length;
        status.textContent = n === 1 ? '1 setting found' : `${n} settings found`;
    }

    function clearSearch() {
        input.value = '';
        search();
    }

    function reveal(item) {
        item.section.open = true;
        const target = item.el;
        document.querySelectorAll('.settings-search-flash').forEach(e => e.classList.remove('settings-search-flash'));
        target.classList.add('settings-search-flash');
        clearTimeout(flashTimer);
        flashTimer = setTimeout(() => target.classList.remove('settings-search-flash'), FLASH_MS);
        target.scrollIntoView({ behavior: 'smooth', block: item.isSection ? 'start' : 'center' });
        if (item.control && !item.control.disabled) item.control.focus({ preventScroll: true });
    }

    function goTo(name) {
        const item = items.find(i => i.name === name);
        if (!item) return;
        showResults(false);
        // A copyable link to this setting, without a history entry per click.
        history.replaceState(null, '', '#' + name);
        reveal(item);
    }

    function followHash() {
        const name = decodeURIComponent((location.hash || '').slice(1));
        if (!name) return;
        const item = items.find(i => i.name === name);
        if (!item) return;
        if (input.value) clearSearch();
        reveal(item);
    }

    function moveFocus(step) {
        const buttons = Array.from(list.querySelectorAll('button'));
        if (!buttons.length) return;
        const at = buttons.indexOf(document.activeElement);
        if (at + step < 0) { input.focus(); return; }
        buttons[Math.min(at + step, buttons.length - 1)].focus();
    }

    function onKey(e) {
        if (e.key === 'Escape') {
            if (input.value) { e.preventDefault(); clearSearch(); }
            input.focus();
        } else if (e.key === 'ArrowDown') {
            e.preventDefault();
            moveFocus(1);
        } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            moveFocus(-1);
        } else if (e.key === 'Enter' && e.target === input) {
            e.preventDefault();
            if (results.length) goTo(results[0].item.name);
        }
    }

    function addStyles() {
        const style = document.createElement('style');
        style.textContent = `
            .settings-search { position: sticky; top: 0; z-index: 1010; padding: .5rem 0; margin-bottom: .75rem; background: var(--bs-body-bg, #fff); }
            .settings-search-box { position: relative; }
            .settings-search-results { position: absolute; left: 0; right: 0; top: 100%; margin: .25rem 0 0; padding: 0; list-style: none;
                max-height: 60vh; overflow-y: auto; background: var(--bs-body-bg, #fff); color: var(--bs-body-color, #212529);
                border: 1px solid var(--bs-border-color, #dee2e6); border-radius: .375rem; box-shadow: 0 .5rem 1rem rgba(0,0,0,.2); }
            .settings-search-result { display: block; width: 100%; padding: .4rem .75rem; border: 0; background: none; color: inherit; text-align: left; }
            .settings-search-result:hover, .settings-search-result:focus { background: rgba(13,110,253,.15); outline: none; }
            .settings-search-label { font-weight: 600; }
            .settings-search-where { margin-left: .5rem; font-size: .8em; opacity: .75; }
            .settings-search-snippet { display: block; font-size: .85em; opacity: .75; }
            .settings-search-none { padding: .5rem .75rem; }
            .settings-search-filtered { display: none !important; }
            .settings-search-match { box-shadow: -.4rem 0 0 rgba(13,110,253,.7); }
            details.settings-section { scroll-margin-top: var(--settings-search-offset, 0); }
            .settings-search-flash { outline: 3px solid #ffc107; outline-offset: 3px; border-radius: .25rem; }
        `;
        document.head.appendChild(style);
    }

    // The menu bar sticks to the top of the page and wraps at narrow widths, so
    // its height has to be measured to sit the box under it.
    function placeBox() {
        const bar = Array.from(document.querySelectorAll('header, nav'))
            .find(e => ['sticky', 'fixed'].includes(getComputedStyle(e).position));
        const top = bar ? Math.round(bar.getBoundingClientRect().height) : 0;
        root.style.top = `${top}px`;
        document.documentElement.style.setProperty('--settings-search-offset', `${top + root.offsetHeight}px`);
    }

    function buildBox() {
        root.className = 'settings-search';
        root.setAttribute('role', 'search');
        const box = document.createElement('div');
        box.className = 'settings-search-box';

        input = document.createElement('input');
        input.type = 'search';
        input.id = 'settingsSearchInput';
        input.className = 'form-control';
        input.placeholder = 'Search settings, for example "port" or "PTT"';
        input.autocomplete = 'off';
        input.setAttribute('aria-label', 'Search settings');
        input.setAttribute('aria-controls', 'settingsSearchResults');
        input.setAttribute('aria-expanded', 'false');

        list = document.createElement('ul');
        list.id = 'settingsSearchResults';
        list.className = 'settings-search-results';
        list.hidden = true;

        status = document.createElement('div');
        status.className = 'visually-hidden';
        status.setAttribute('role', 'status');

        box.append(input, list);
        root.append(box, status);

        input.addEventListener('input', search);
        input.addEventListener('focus', () => showResults(true));
        root.addEventListener('keydown', onKey);
        list.addEventListener('click', e => {
            const b = e.target.closest('button[data-target]');
            if (b) goTo(b.dataset.target);
        });
        document.addEventListener('click', e => {
            if (!root.contains(e.target)) showResults(false);
        });
        // Before validation runs, so the page's own handler can open and focus
        // a bad value in a section the search has hidden.
        document.addEventListener('submit', () => { if (input.value) clearSearch(); }, true);
    }

    function init() {
        root = document.getElementById('settingsSearch');
        if (!root) return;
        keywords = window.RWC_SETTINGS_KEYWORDS || {};
        addStyles();
        buildBox();
        buildIndex();
        placeBox();
        window.addEventListener('resize', placeBox);
        window.addEventListener('hashchange', followHash);
        if (document.readyState === 'complete') followHash();
        else window.addEventListener('load', followHash);
    }

    // For the UI tests: the names found, and keywords for settings that have gone.
    window.SettingsSearch = {
        names: () => items.map(i => ({ name: i.name, label: i.label, section: i.sectionTitle })),
        unknownKeywords: () => Object.keys(keywords).filter(k => !items.some(i => i.name === k)),
    };

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
