// cw-tokens.js marks up decoded CW copy. Two properties matter, and they pull
// in opposite directions, which is why both are pinned here.
//
// The first is a contract with the decoder: markUp must not change the copy.
// The whole readability design (docs/design/cw-decoder.md section 6) rests on
// "score, never edit" - nothing distinguishes a spurious S from the S in a
// callsign, so plausible-corrected copy is more dangerous than visibly-wrong
// copy for an operator who does not read CW. A renderer that quietly dropped or
// reordered a character would break that rule from the other end.
//
// The second is that the markup has to be worth having: it must fire on real
// QSO traffic and stay silent on junk. The fixtures below are excerpts of
// actual decoder output from the bench recordings, junk included.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"
// (the bare directory form fails on Node 24 under Windows - it tries to load
//  the directory itself as a module.)

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { classify, markUp } from '../../js/cw/cw-tokens.js';

// Real decoder output, verbatim, from bench/cq-then-qso.wav and
// bench/mkii-dk9py.wav. Both are off-air recordings of ordinary QSOs at the
// quality this reader actually achieves - which is to say roughly a quarter
// garbage, with the callsigns standing correct between it.
const REAL_OE3KAB =
    ' TE3KAB OE3KAB K CQ CQ CQ DE OE3KAB OE3KAB K M N E M K4? E U4POC GM5NN ' +
    ' KARL BK E SE EE EHE ER KARL OE 73G S TUEE EEHE 4CS 5C KARL O3X';

const REAL_DK9PY =
    ' 1D I EE E1 MC1D ARMIN 2T62 O M  TM TEERQ CWT DK9PY GVM EN4PVM ARMIN ' +
    '2T62 UEE FQCWT DK9PY IZA E V ZA1EM ARMIN 2T62 E E H A3T6 X CQCWTE I2WIJ ' +
    'EFQ CWT DK9PY I2WIJ NQ CWT DK9PY I2WIJ 2 IE IEAIE IK I2WIJ ARMIN 2T62 ' +
    'BOB 24T6 TU CQ N E EQ CWT DK9PY E E E EE ES IEIE H 5NQ E E E I E';

// The 40 wpm ARRL file at 0 dB SNR: a signal the reader cannot copy, decoded
// into characters that are not words.
const JUNK = '565ES5AETI EUEERSFAE';

const kinds = text => markUp(text).map(r => r.kind).filter(Boolean);
const marked = (text, kind) =>
    markUp(text).filter(r => r.kind === kind).map(r => r.text);

test('the runs reproduce the copy exactly', () => {
    for (const text of [REAL_OE3KAB, REAL_DK9PY, JUNK, '', '   ', 'CQ']) {
        assert.equal(markUp(text).map(r => r.text).join(''), text);
    }
});

test('leading and trailing whitespace survives', () => {
    // The panel appends poll to poll, so a lost trailing space would run two
    // tokens together and change what the next markUp sees.
    assert.equal(markUp('  CQ DE  ').map(r => r.text).join(''), '  CQ DE  ');
});

test('prosigns, reports and callsigns are recognised', () => {
    assert.equal(classify('CQ'), 'proc');
    assert.equal(classify('de'), 'proc');       // case-insensitive
    assert.equal(classify('73'), 'proc');
    assert.equal(classify('5NN'), 'rst');
    assert.equal(classify('599'), 'rst');
    assert.equal(classify('MM5AGM'), 'call');
    assert.equal(classify('2E0ABC'), 'call');
    assert.equal(classify('VP8/M0XYZ'), 'call');
    assert.equal(classify('EEEIE'), '');
});

test('junk is not marked up at all', () => {
    // The one result that decides whether the colour means anything: if garble
    // lit up, the highlight would be decoration rather than information.
    assert.deepEqual(kinds(JUNK), []);
});

test('a real QSO gets its callsign and its prosigns', () => {
    assert.ok(marked(REAL_OE3KAB, 'call').includes('OE3KAB'));
    assert.ok(kinds(REAL_OE3KAB).includes('proc'));
});

test('a call heard more than once outranks a call heard once', () => {
    // Callsign shape is six characters and garble finds it by chance. On both
    // real recordings repetition separated true from false completely: OE3KAB
    // three times against four one-off shapes, DK9PY five and I2WIJ four
    // against three one-offs. Neither is hidden - the first sighting of a real
    // call is a single sighting too - but they must not look alike.
    assert.deepEqual(new Set(marked(REAL_DK9PY, 'call')),
                     new Set(['DK9PY', 'I2WIJ']));

    const once = new Set(marked(REAL_DK9PY, 'call1'));
    assert.ok(once.has('MC1D'));
    assert.ok(once.has('EN4PVM'));
    assert.ok(once.has('ZA1EM'));
});

test('a call promotes as soon as it is heard again', () => {
    assert.deepEqual(kinds('CQ DE MM5AGM K'),        ['proc', 'proc', 'call1', 'proc']);
    assert.deepEqual(kinds('CQ DE MM5AGM MM5AGM K'),
                     ['proc', 'proc', 'call', 'call', 'proc']);
});
