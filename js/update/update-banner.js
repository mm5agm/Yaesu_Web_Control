// ── GitHub update check, and the "what's new" banner ──────────────────────
//
// Shared by Icom Web Control and Yaesu Web Control. There is nothing radio-
// specific in here: it reads three meta tags for the product name, the repo
// and the running version, asks GitHub what the newest release is, and puts a
// banner up if that is newer than what is running.
//
// What counts as "newest" depends on what the operator is running. On a full
// release it means full releases only. On a pre-release it includes newer
// pre-releases, because someone running one is a tester by definition and the
// next pre-release is the thing they signed up to be told about. See
// _checkForUpdate.
//
// It lived inline in each app's site.js, in two copies that had already
// drifted — IWC's knew about pre-release version suffixes and YWC's did not,
// so a YWC tester sitting on a pre-release would never be told that the full
// release of the same number had shipped. Sharing it fixes that for free and
// stops the next improvement having to be made twice.
//
// Host page must carry:
//   <meta name="x-app-version" content="1.2.0-pre1">
//   <meta name="x-app-repo"    content="mm5agm/Icom_Web_Control">
//   <meta name="x-app-name"    content="Icom Web Control">
(function () {
    const DISMISS_KEY_PREFIX = 'updateCheckDismissed_';

    // How many bullets to show before "…and more". A banner is an
    // interruption; it earns a few lines, not a changelog.
    const MAX_ITEMS = 5;
    const MAX_ITEM_CHARS = 180;

    function _dismissKey(version) { return DISMISS_KEY_PREFIX + version; }

    function _escHtml(s) {
        return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function _meta(name, fallback) {
        const el = document.querySelector(`meta[name="${name}"]`);
        const v = el && el.content ? el.content.trim() : '';
        return v || fallback;
    }

    // "v1.0.6-pre4" → { n: [1, 0, 6], pre: "pre4" }. Anything that is not a
    // three-part version number returns null and is then ignored everywhere.
    // That filter is load-bearing: Yaesu Web Control publishes a nightly
    // `unstable-20260920` tag as a GitHub *pre-release*, and without this a
    // tester would be offered last night's build as though it were the next
    // pre-release. Only vX.Y.Z and vX.Y.Z-suffix are ever offered.
    function _parseVersion(tag) {
        const m = /^v?(\d+)\.(\d+)\.(\d+)(?:-(.+))?$/.exec(String(tag || '').trim());
        return m ? { n: [+m[1], +m[2], +m[3]], pre: m[4] || '' } : null;
    }

    // Compare a chunk at a time, digits numerically, so pre10 beats pre2 —
    // which a plain string compare gets backwards.
    function _naturalCmp(a, b) {
        const chunks = v => String(v).match(/\d+|\D+/g) || [];
        const x = chunks(a), y = chunks(b);
        for (let i = 0; i < Math.max(x.length, y.length); i++) {
            if (x[i] === undefined) return -1;
            if (y[i] === undefined) return 1;
            const xn = /^\d+$/.test(x[i]), yn = /^\d+$/.test(y[i]);
            if (xn && yn) {
                const d = parseInt(x[i], 10) - parseInt(y[i], 10);
                if (d !== 0) return d < 0 ? -1 : 1;
            } else if (x[i] !== y[i]) {
                return x[i] < y[i] ? -1 : 1;
            }
        }
        return 0;
    }

    function _isNewer(latest, current) {
        const a = _parseVersion(latest);
        const b = _parseVersion(current);
        if (!a || !b) return false;
        for (let i = 0; i < 3; i++) {
            if (a.n[i] !== b.n[i]) return a.n[i] > b.n[i];
        }
        // Identical numbers. Semver's rule — a pre-release sorts below the
        // release it leads to — so 1.0.6 is an upgrade from 1.0.6-pre4, and a
        // tester sitting on a pre-release is told when the real thing ships.
        if (a.pre === b.pre) return false;
        if (a.pre === '') return true;
        if (b.pre === '') return false;
        // Both pre-releases: pre4 → pre5 is an upgrade too.
        return _naturalCmp(a.pre, b.pre) > 0;
    }

    // Pick the newest thing worth offering. `data` is either the single
    // release object from /releases/latest or the array from /releases.
    function _pickRelease(data, current, includePrereleases) {
        const list = Array.isArray(data) ? data : [data];
        let best = null;
        for (const r of list) {
            if (!r || r.draft) continue;
            if (r.prerelease && !includePrereleases) continue;
            const tag = r.tag_name || '';
            if (!_parseVersion(tag)) continue;
            if (!_isNewer(tag, current)) continue;
            if (best && !_isNewer(tag, best.tag_name || '')) continue;
            best = r;
        }
        return best;
    }

    // ── Release-notes rendering ───────────────────────────────────────────
    //
    // The release body is Markdown written for the GitHub releases page: a
    // blockquote of upgrade advice, then one bullet per change, each opening
    // with a bold sentence saying what changed. Those opening sentences are
    // exactly what the banner wants, so we take the bullets, keep the lead
    // sentence of each and drop the rest.
    //
    // This deliberately does NOT use a Markdown library. Everything is escaped
    // first and only <strong> is put back, so a release body — which is public
    // text fetched from the network — can never inject markup into the page.

    function _inline(md) {
        // Escape first, then re-introduce the two constructs worth keeping.
        let s = _escHtml(md);
        s = s.replace(/\[([^\]]+)\]\([^)]*\)/g, '$1');   // [text](url) → text
        s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
        s = s.replace(/`([^`]+)`/g, '$1');
        return s;
    }

    function _leadSentence(text) {
        // The bullets are long. Keep the bold lead if there is one, otherwise
        // the first sentence, and ellipsise anything still over the limit.
        const bold = text.match(/^\s*\*\*(.+?)\*\*/);
        let lead = bold ? bold[1] : (text.split(/(?<=[.!?])\s/)[0] || text);
        lead = lead.trim();
        if (lead.length > MAX_ITEM_CHARS) lead = lead.slice(0, MAX_ITEM_CHARS - 1).trimEnd() + '…';
        return lead;
    }

    function _notesHtml(body) {
        if (!body) return '';
        const lines = String(body).split(/\r?\n/);
        const items = [];
        for (const line of lines) {
            const m = line.match(/^\s{0,3}[-*]\s+(.*\S)/);
            if (!m) continue;                       // headings, blockquotes, prose
            const lead = _leadSentence(m[1]);
            if (lead) items.push(lead);
        }
        if (items.length === 0) return '';
        const shown = items.slice(0, MAX_ITEMS);
        const more  = items.length - shown.length;
        return (
            `<div style="margin-top:8px;border-top:1px solid #35506b;padding-top:6px">` +
            `<div style="color:#8fb4d4;font-size:0.72rem;text-transform:uppercase;letter-spacing:0.04em;margin-bottom:4px">What&rsquo;s new</div>` +
            `<ul style="margin:0;padding-left:16px;max-height:9.5rem;overflow-y:auto;color:#bcd;font-size:0.76rem;line-height:1.35">` +
            shown.map(t => `<li style="margin-bottom:3px">${_inline(t)}</li>`).join('') +
            `</ul>` +
            (more > 0
                ? `<div style="color:#89a;font-size:0.72rem;margin-top:4px">&hellip;and ${more} more &mdash; see the full notes.</div>`
                : '') +
            `</div>`
        );
    }

    function _dismiss(version) {
        try { localStorage.setItem(_dismissKey(version), '1'); } catch { /* private browsing */ }
        const el = document.getElementById('updateBanner');
        if (el) el.remove();
    }

    function _showUpdateBanner(version, releaseUrl, notesBody, productName, isPrerelease) {
        if (document.getElementById('updateBanner')) return;
        try { if (localStorage.getItem(_dismissKey(version))) return; } catch { /* private browsing */ }
        const banner = document.createElement('div');
        banner.id = 'updateBanner';
        banner.style.cssText = [
            'position:fixed', 'top:50%', 'left:50%', 'transform:translate(-50%,-50%)', 'z-index:9999',
            'background:#1e2a38', 'border:1px solid #4a8abf', 'border-radius:8px',
            'padding:10px 14px', 'color:#cde', 'font-size:0.84rem',
            'box-shadow:0 4px 16px rgba(0,0,0,0.6)', 'max-width:380px', 'width:360px'
        ].join(';');
        banner.innerHTML =
            `<div style="display:flex;align-items:flex-start;gap:8px">` +
            `<div style="flex:1"><strong>Update available — v${_escHtml(version)}</strong>` +
            (isPrerelease
                ? `<span style="margin-left:6px;background:#4a3a1a;border:1px solid #8a6a2a;color:#e0c070;` +
                  `border-radius:3px;padding:0 5px;font-size:0.66rem;text-transform:uppercase;letter-spacing:0.05em">Pre-release</span>`
                : '') +
            `<br>` +
            `<span style="color:#aab;font-size:0.78rem">` +
            (isPrerelease
                ? `A newer pre-release of ${_escHtml(productName)} is ready for testing.`
                : `A newer version of ${_escHtml(productName)} is available.`) +
            `</span></div>` +
            `<button id="updateBannerDismissX" ` +
            `style="background:none;border:none;color:#aaa;cursor:pointer;font-size:1rem;line-height:1;padding:0" aria-label="Dismiss">✕</button>` +
            `</div>` +
            _notesHtml(notesBody) +
            `<div style="margin-top:8px;display:flex;gap:8px">` +
            `<a href="${_escHtml(releaseUrl)}" target="_blank" rel="noopener" ` +
            `style="background:#1a4a7a;border:1px solid #4a8abf;color:#cde;padding:3px 10px;border-radius:4px;font-size:0.78rem;text-decoration:none">Download</a>` +
            `<button id="updateBannerDismissBtn" ` +
            `style="background:#2d2d44;border:1px solid #555;color:#aaa;padding:3px 10px;border-radius:4px;font-size:0.78rem;cursor:pointer">Dismiss</button>` +
            `</div>`;
        document.body.appendChild(banner);
        document.getElementById('updateBannerDismissX').addEventListener('click', () => _dismiss(version));
        document.getElementById('updateBannerDismissBtn').addEventListener('click', () => _dismiss(version));
    }

    async function _checkForUpdate() {
        const current = _meta('x-app-version', '');
        if (!current) return;
        const running = _parseVersion(current);
        if (!running) return;           // unrecognisable version: say nothing
        const repo = _meta('x-app-repo', '');
        if (!repo) return;
        const productName = _meta('x-app-name', document.title || 'this app');

        // Which endpoint depends on what is running, and NOT on any setting.
        //
        // On a full release: /releases/latest — NOT /releases. GitHub defines
        // "latest" as the newest release that is neither a draft nor a
        // pre-release, which is exactly the policy we want. A banner is an
        // interruption, and interrupting someone mid-QSO to offer them a
        // less-tested build is the wrong trade. There is deliberately no
        // "include pre-releases" option for these operators to find.
        //
        // On a pre-release: the list, pre-releases included. That is not a
        // reversal of the rule above, it is the same rule read properly —
        // someone running 1.2.0-pre1 has already opted into a less-tested
        // build, and leaving them stranded on it is how a tester ends up
        // reporting a bug that was fixed three pre-releases ago. They are the
        // one group for whom the next pre-release IS the thing to tell them
        // about. _pickRelease drops nightly `unstable-*` tags, which are
        // published as pre-releases but are not offers.
        const testing = running.pre !== '';
        const endpoint = testing
            ? `https://api.github.com/repos/${repo}/releases?per_page=30`
            : `https://api.github.com/repos/${repo}/releases/latest`;
        try {
            const resp = await fetch(endpoint, {
                headers: { Accept: 'application/vnd.github+json' }
            });
            if (!resp.ok) return;
            const best = _pickRelease(await resp.json(), current, testing);
            if (!best) return;
            _showUpdateBanner((best.tag_name || '').replace(/^v/i, ''),
                best.html_url || `https://github.com/${repo}/releases`,
                best.body || '',
                productName,
                !!best.prerelease);
        } catch { /* network unavailable or rate limited — silently skip */ }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => setTimeout(_checkForUpdate, 3000));
    } else {
        setTimeout(_checkForUpdate, 3000);
    }
})();
