// Morse keying time for a piece of text, in the units every keyer uses:
// dot = 1, dash = 3, gap inside a character = 1, between characters = 3,
// between words = 7, and one unit = 1200 / wpm milliseconds (the PARIS
// standard - "PARIS " is 50 units, so 20 wpm is 1000 units a minute).
//
// This is what a sender needs when the radio gives it no "finished" signal:
// a keyer playing a memory to the sidetone monitor with break-in off, for
// instance, reports nothing back, and the only way to know when the next
// piece can go is to know how long this one takes. A per-character average
// is not good enough for that - "EEEEE" and "00000" differ by six to one.
//
// No radio in here: pure text in, milliseconds out.

export const MORSE = {
    A: '.-',    B: '-...',  C: '-.-.',  D: '-..',   E: '.',     F: '..-.',
    G: '--.',   H: '....',  I: '..',    J: '.---',  K: '-.-',   L: '.-..',
    M: '--',    N: '-.',    O: '---',   P: '.--.',  Q: '--.-',  R: '.-.',
    S: '...',   T: '-',     U: '..-',   V: '...-',  W: '.--',   X: '-..-',
    Y: '-.--',  Z: '--..',
    0: '-----', 1: '.----', 2: '..---', 3: '...--', 4: '....-',
    5: '.....', 6: '-....', 7: '--...', 8: '---..', 9: '----.',
    '.': '.-.-.-', ',': '--..--', '?': '..--..', '/': '-..-.',
    '=': '-...-', '+': '.-.-.', '-': '-....-', '@': '.--.-.',
    "'": '.----.', '!': '-.-.--', '(': '-.--.', ')': '-.--.-',
    ':': '---...', ';': '-.-.-.', '"': '.-..-.', '_': '..--.-', '$': '...-..-',
};

// Units keyed for one character (elements plus the gaps between them),
// or 0 for anything the table does not know - an unknown character is
// something the keyer will skip, not something it will send slowly.
export function charUnits(ch) {
    const code = MORSE[String(ch).toUpperCase()];
    if (!code) return 0;
    let u = 0;
    for (const e of code) u += e === '-' ? 3 : 1;
    return u + (code.length - 1);
}

// Units from the first element to the last of the whole text. Runs of
// whitespace are one word gap; leading and trailing whitespace is not
// keyed at all. Between two characters of a word the gap is 3 units;
// between words 7 - the 7 replaces the 3, it does not add to it.
export function textUnits(text) {
    const words = String(text || '').trim().split(/\s+/).filter(Boolean);
    let total = 0;
    words.forEach((word, wi) => {
        if (wi > 0) total += 7;
        let first = true;
        for (const ch of word) {
            const u = charUnits(ch);
            if (!u) continue;
            if (!first) total += 3;
            total += u;
            first = false;
        }
    });
    return total;
}

// Milliseconds the keyer is busy with the text at the given speed.
export function durationMs(text, wpm) {
    const w = Number(wpm);
    if (!Number.isFinite(w) || w <= 0) return 0;
    return Math.round(textUnits(text) * 1200 / w);
}

// When each character of the text is under the key, for a display that
// follows the sending. One entry per character of `text` as given (the
// index is into that string), [start, end) in milliseconds from the first
// element. The windows tile: a keyed character's window runs on through
// the 3-unit gap to the next one, and a space owns the 7-unit word gap,
// so something is always "current" until the last element ends. Leading
// and trailing whitespace, a second space in a run, and characters not in
// the table get an empty window - the keyer skips them, so the display
// should too. The last end equals durationMs(text, wpm).
export function charTimeline(text, wpm) {
    const s = String(text || '');
    const w = Number(wpm);
    const unit = Number.isFinite(w) && w > 0 ? 1200 / w : 0;
    const out = [];
    let t = 0;                 // units so far
    let prev = null;           // last keyed entry
    let pendingSpace = null;   // a space waiting for the next keyed character to give it its gap
    for (let i = 0; i < s.length; i++) {
        const ch = s[i];
        const e = { index: i, start: t, end: t };
        out.push(e);
        if (/\s/.test(ch)) {
            if (prev && !pendingSpace) pendingSpace = e;
            continue;
        }
        const u = charUnits(ch);
        if (!u) continue;
        if (prev) {
            if (pendingSpace) { pendingSpace.end = t + 7; t += 7; pendingSpace = null; }
            else { prev.end = t + 3; t += 3; }
        }
        e.start = t;
        e.end = t + u;
        t += u;
        prev = e;
    }
    for (const e of out) {
        e.start = Math.round(e.start * unit);
        e.end = Math.round(e.end * unit);
    }
    return out;
}
