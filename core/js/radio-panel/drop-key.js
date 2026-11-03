// drop-key.js - a key that drops down a list of links: the view switch and
// MENU on the radio panel, and the view switch on the RTTY Contest page.
// Styled by radio-panel-style.js (rp-menu, rp-viewbtn).

function h(tag, cls, text) {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (text != null) el.textContent = text;
    return el;
}

/**
 * Closes on a pick, a click anywhere else, or Esc. `newTab` opens the links
 * in a new tab, so the page keeps running behind them.
 * @param {string} label
 * @param {string} title
 * @param {{group?:string, items:{label:string, href:string, on?:boolean, title?:string}[]}[]} groups
 * @param {{newTab?:boolean, alignLeft?:boolean}} [opts]
 * @returns {HTMLElement}
 */
export function dropKey(label, title, groups, { newTab = false, alignLeft = false } = {}) {
    const wrap = h('div', 'rp-menu');
    const key = h('button', 'rp-viewbtn', label);
    key.type = 'button';
    key.title = title;
    key.setAttribute('aria-haspopup', 'true');
    key.setAttribute('aria-expanded', 'false');
    const list = h('div', 'rp-menu-list' + (alignLeft ? ' rp-menu-left' : ''));
    list.hidden = true;
    for (const g of groups) {
        if (g.group) list.append(h('div', 'rp-menu-group', g.group));
        for (const it of g.items ?? []) {
            const a = h('a', 'rp-menu-item' + (it.on ? ' on' : ''), it.label);
            a.href = it.href;
            if (newTab) { a.target = '_blank'; a.rel = 'noopener'; }
            if (it.on) a.setAttribute('aria-current', 'page');
            if (it.title) a.title = it.title;
            a.addEventListener('click', () => show(false));
            list.append(a);
        }
    }
    const show = on => {
        list.hidden = !on;
        key.classList.toggle('on', on);
        key.setAttribute('aria-expanded', String(on));
    };
    key.addEventListener('click', () => show(list.hidden));
    document.addEventListener('pointerdown', e => { if (!list.hidden && !wrap.contains(e.target)) show(false); });
    document.addEventListener('keydown', e => { if (e.key === 'Escape' && !list.hidden) show(false); });
    wrap.append(key, list);
    const bar = h('div', 'rp-bar');
    bar.append(wrap);
    return bar;
}
