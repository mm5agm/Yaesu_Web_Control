// Radio Web Control - fold a section of the page away
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// One button hides part of the page and shows it again, and the choice is
// remembered. It is for the sections an operator may not want on screen all
// the time - the meters, the clarifier - so the space goes to what they do
// want, usually the spectrum. Nothing is closed for good: the button stays
// where it was, says what it will bring back, and the page carries on
// updating what is hidden, so it is current the moment it is shown.
//
// Folding hides, it does not remove: the section's elements stay in the
// document and anything driving them keeps running.

const STORAGE_PREFIX = 'foldPanel_';

/**
 * Whether a section should start folded, from what storage holds.
 * Pure, so it can be tested. Only an explicit '1' folds: anything else -
 * nothing saved, storage off, a value from some other version - leaves the
 * section showing, which is how the page looked before folding existed.
 *
 * @param {string|null|undefined} stored
 * @returns {boolean}
 */
export function isFoldedValue(stored) {
    return stored === '1';
}

function readFolded(name) {
    try { return isFoldedValue(localStorage.getItem(STORAGE_PREFIX + name)); }
    catch { return false; }
}

function writeFolded(name, folded) {
    try { localStorage.setItem(STORAGE_PREFIX + name, folded ? '1' : '0'); }
    catch { /* private browsing or storage off: the next load shows it */ }
}

/**
 * Wire a fold button to the element(s) it hides.
 *
 * @param {object} opts
 * @param {string} opts.name  short id; names the stored choice
 * @param {HTMLElement|null} opts.button  the toggle; a real <button>
 * @param {HTMLElement[]} opts.targets  what folding hides
 * @param {HTMLElement|null} [opts.root]  gets the class 'is-folded' while folded, for CSS
 * @param {{text:string,title:string,aria:string}} opts.shownLabel   the button while the section shows
 * @param {{text:string,title:string,aria:string}} opts.foldedLabel  the button while it is folded
 * @param {(folded: boolean) => void} [opts.onChange]  after every change, and once at start
 * @returns {{ fold: () => void, unfold: () => void, toggle: () => void, readonly folded: boolean } | null}
 */
export function attachFold({ name, button, targets, root = null, shownLabel, foldedLabel, onChange }) {
    if (!button || !targets?.length) return null;
    const ids = targets.map(t => t.id).filter(Boolean);
    if (ids.length) button.setAttribute('aria-controls', ids.join(' '));

    let folded = false;
    const apply = f => {
        folded = f;
        for (const t of targets) t.hidden = f;
        root?.classList.toggle('is-folded', f);
        const l = f ? foldedLabel : shownLabel;
        button.setAttribute('aria-expanded', String(!f));
        button.title = l.title;
        button.setAttribute('aria-label', l.aria);
        // Text goes in a span of its own so an icon beside it survives.
        const textEl = button.querySelector('.fold-text') ?? button;
        textEl.textContent = l.text;
        const icon = button.querySelector('.bi');
        icon?.classList.toggle('bi-chevron-up', !f);
        icon?.classList.toggle('bi-chevron-down', f);
        try { onChange?.(f); } catch (e) { console.error('[fold-panel]', name, e); }
    };

    button.addEventListener('click', () => {
        apply(!folded);
        writeFolded(name, folded);
    });
    apply(readFolded(name));

    return {
        fold:   () => { apply(true);  writeFolded(name, true); },
        unfold: () => { apply(false); writeFolded(name, false); },
        toggle: () => { apply(!folded); writeFolded(name, folded); },
        get folded() { return folded; },
    };
}
