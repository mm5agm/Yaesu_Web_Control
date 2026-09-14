// Marking up decoded CW copy: which tokens look like QSO traffic.
//
// This file is copied into each app's wwwroot at build time - see js/README.md.
// Edit it here, in the core, never in a wwwroot copy.
//
// The problem this solves, and the one it deliberately does not.
//
// The reader prints a stream of characters with no case, no punctuation and no
// word boundaries it did not measure itself. When the copy is good it is a QSO
// and reads as one. When it is bad it is a wall of the same alphabet in a
// different order, and to someone who does not read CW those two look alike.
// What an operator actually wants out of a marginal QSO is small and specific:
// the other station's call, the name, and whether the exchange has reached 73.
//
// So this marks up the tokens that have the *shape* of QSO traffic - the
// procedural signals, the Q-codes, the reports, and callsign-shaped groups -
// and leaves everything else exactly as the decoder produced it.
//
// What it must never become. The obvious next step is to use the same test the
// other way and suppress the tokens that fail it. That was measured before this
// file was written, and it does not work:
//
//     ARRL ground truth, 10 files      1.1 - 8.3% of words implausible
//     good decoder copy                1.1 - 9.3%
//     real off-air copy, cq-then-qso        22.9%
//     real off-air copy, mkii-dk9py         28.6%
//     genuine junk, 40 wpm at 0 dB          31.2%
//
// Real off-air copy at this reader's quality sits inside the junk range, because
// real off-air copy *is* about a quarter garbage words with the callsigns
// standing correct between them. A filter set anywhere that caught the junk
// would take the QSOs with it, and the junk is already caught by the block-level
// readability checks (83% of it scores Jumbled). Highlighting is the form of the
// idea that survives measurement: it adds a signal without removing any copy.
//
// And the honest reading of a highlight. It says "this token has the shape of
// something an operator sends", not "this token is correct". Garble lands on
// callsign shape by chance - it is only five or six characters - so a marked
// call still has to be confirmed the way any call is, by hearing it twice. The
// value is that it tells the eye where to look in a wall of characters, which
// is the job it can actually do.

// Procedural signals, Q-codes and the standard abbreviations. Only tokens a CW
// operator genuinely sends: padding this list with dictionary words would mark
// up garble that happens to spell one.
const PROSIGNS = new Set(`
CQ DE K KN AR SK BK BT AS R RR NR QRZ TU TNX TKS THX ES PSE PLS AGN
QTH QRM QRN QSB QSY QRP QRO QSL QRS QRQ QSO QRT QRV QRX QTC QSK QRL
RST RPT HR HW UR WX RIG ANT PWR FB OM YL XYL GM GA GE GN GB DR MNI
73 88 OP NAME CPY CUL CUAGN VY GUD GD DX WKD WL WUD SRI SED HPE
BCNU NW OK TEST VVV ARRL QST UTC GMT WPM CW SSB RTTY PSK FT8 ANTENNA
DIPOLE YAGI VERT WATTS W KW MHZ KHZ TX RX ABT AGE TEMP HPY
`.trim().split(/\s+/));

// A callsign: a prefix, a separating digit, then the suffix. The prefix is one
// or two letters, or a digit and a letter - that second form is not an oddity
// to be tidied away, it is 2E0 and 2M0, which is half the UK foundation
// licences and would otherwise never be marked. Loose enough for GB100 and
// VP8/M0XYZ; tight enough that ordinary garble mostly misses it.
//
// Both slashed forms have to be allowed and they are not the same thing:
// VP8/M0XYZ says where the operator is, MM5AGM/P says how they are operating.
const BASE = String.raw`(?:[0-9][A-Z]|[A-Z]{1,2})[0-9]{0,2}[A-Z]{0,2}[0-9][A-Z]{1,4}`;
const CALL = new RegExp(String.raw`^(?:[A-Z0-9]{1,4}\/)?${BASE}(?:\/[A-Z0-9]{1,4})?$`);

// A signal report, sent either in full or with the cut numbers. 5NN and 5TT are
// the ones actually heard; the general form covers 599, 579, 33N and so on.
const RST = /^[1-5][1-9NAT][1-9NAT]?$/;

/// Classify one token. Returns 'call', 'rst', 'proc' or '' for unrecognised.
///
/// Order matters: a report is checked before a callsign because 5NN would
/// otherwise never be seen as one, and the prosign list is checked first
/// because a few entries in it (W, K, R) are also legal callsign fragments.
export function classify(token) {
    const t = token.toUpperCase();
    if (!t) return '';
    if (PROSIGNS.has(t)) return 'proc';
    if (RST.test(t))     return 'rst';
    if (CALL.test(t))    return 'call';
    return '';
}

/// Split text into runs of {text, kind}, where kind is '' for anything not
/// recognised. Every character of the input appears in exactly one run and in
/// the original order, so rendering the runs reproduces the copy verbatim -
/// which is the whole contract with "score, never edit".
///
/// Callsign shape alone is weak: it is five or six characters, and garble finds
/// it often. What separates the two is repetition, and on the two real off-air
/// recordings it separated them completely:
///
///     cq-then-qso   repeated  OE3KAB x3
///                   seen once TE3KAB U4POC GM5NN O3X
///     mkii-dk9py    repeated  DK9PY x5, I2WIJ x4
///                   seen once MC1D EN4PVM ZA1EM
///
/// Every true call repeated; every false one appeared once. That is not a
/// coincidence of these two files - an operator sends their call two or three
/// times because the band is why they have to, and garble has no reason to
/// land on the same wrong six characters twice. So a call seen more than once
/// in the visible copy is marked 'call' and one seen once is marked 'call1',
/// and the panel draws the second faintly. Neither is removed: the first
/// sighting of a real call is a single sighting too.
export function markUp(text) {
    // Two passes, because a token's kind depends on the whole buffer. Counting
    // over what is on screen rather than over all time is the right window:
    // it is the same evidence the operator has in front of them.
    const seen = new Map();
    for (const t of text.toUpperCase().match(/\S+/g) || [])
        if (classify(t) === 'call') seen.set(t, (seen.get(t) || 0) + 1);

    const runs = [];
    // Whitespace is the only separator: the reader emits nothing else between
    // tokens, and slashes and digits belong inside callsigns and reports.
    const re = /\S+/g;
    let at = 0, m;

    while ((m = re.exec(text)) !== null) {
        let kind = classify(m[0]);
        if (!kind) continue;
        if (kind === 'call' && (seen.get(m[0].toUpperCase()) || 0) < 2) kind = 'call1';
        if (m.index > at) runs.push({ text: text.slice(at, m.index), kind: '' });
        runs.push({ text: m[0], kind });
        at = m.index + m[0].length;
    }
    if (at < text.length) runs.push({ text: text.slice(at), kind: '' });
    return runs;
}

/// Render the runs into an element as spans. Kept here rather than in the panel
/// so both apps get the same markup, and so the DOM is built rather than
/// assembled as HTML - decoded copy is remote data and has no business being
/// parsed as markup.
export function renderInto(el, text) {
    el.textContent = '';
    for (const run of markUp(text)) {
        if (!run.kind) {
            el.appendChild(document.createTextNode(run.text));
            continue;
        }
        const span = document.createElement('span');
        span.className = 'cw-tok cw-tok-' + run.kind;
        span.textContent = run.text;
        el.appendChild(span);
    }
}
