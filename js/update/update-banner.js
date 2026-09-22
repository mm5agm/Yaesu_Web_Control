// ── GitHub update check, and the "what's new" banner ──────────────────────
//
// Shared by Icom Web Control and Yaesu Web Control. There is nothing radio-
// specific in here: it reads three meta tags for the product name, the repo
// and the running version, asks GitHub what the newest full release is, and
// puts a banner up if that is newer than what is running.
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

    function _isNewer(latest, current) {
        // Versions may carry a pre-release suffix ("1.0.6-pre4"). Split it off
        // before comparing: parseInt reads "6-pre4" as plain 6, which would make
        // a pre-release compare EQUAL to the full release of the same number, so
        // a tester sitting on 1.0.6-pre4 would never be told that 1.0.6 itself
        // had shipped — the one group of users who most need to be moved on.
        const split = v => {
            const s = String(v);
            const i = s.indexOf('-');
            return {
                n:   (i < 0 ? s : s.slice(0, i)).split('.').map(x => parseInt(x, 10) || 0),
                pre: i < 0 ? '' : s.slice(i + 1)
            };
        };
        const a = split(latest);
        const b = split(current);
        for (let i = 0; i < Math.max(a.n.length, b.n.length); i++) {
            const diff = (a.n[i] || 0) - (b.n[i] || 0);
            if (diff > 0) return true;
            if (diff < 0) return false;
        }
        // Identical numbers: semver's rule — a pre-release sorts below the
        // release it leads to, so 1.0.6 is an upgrade from 1.0.6-pre4.
        return a.pre === '' && b.pre !== '';
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

    function _showUpdateBanner(version, releaseUrl, notesBody, productName) {
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
            `<div style="flex:1"><strong>Update available — v${_escHtml(version)}</strong><br>` +
            `<span style="color:#aab;font-size:0.78rem">A newer version of ${_escHtml(productName)} is available.</span></div>` +
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
        const repo = _meta('x-app-repo', '');
        if (!repo) return;
        const productName = _meta('x-app-name', document.title || 'this app');
        try {
            // /releases/latest — NOT /releases. GitHub defines "latest" as the
            // newest release that is neither a draft nor a pre-release, which is
            // exactly the policy we want: operators are told about full releases
            // only. Pre-releases are opt-in — someone who wants to test one goes
            // to the releases page and picks it deliberately. Never switch this
            // to the list endpoint or add a "include pre-releases" option; a
            // banner is an interruption, and interrupting someone mid-QSO to
            // offer them a less-tested build is the wrong trade. (The notes on
            // a pre-release's own GitHub page are a different matter, and the
            // release script fills those in too.)
            const resp = await fetch(`https://api.github.com/repos/${repo}/releases/latest`, {
                headers: { Accept: 'application/vnd.github+json' }
            });
            if (!resp.ok) return;
            const data = await resp.json();
            // Belt and braces: if GitHub ever hands back a pre-release or draft
            // here, stay quiet rather than trusting the endpoint's contract.
            if (data.prerelease || data.draft) return;
            const latest = (data.tag_name || '').replace(/^v/i, '');
            if (latest && _isNewer(latest, current)) {
                _showUpdateBanner(latest,
                    data.html_url || `https://github.com/${repo}/releases`,
                    data.body || '',
                    productName);
            }
        } catch { /* network unavailable or rate limited — silently skip */ }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => setTimeout(_checkForUpdate, 3000));
    } else {
        setTimeout(_checkForUpdate, 3000);
    }
})();
