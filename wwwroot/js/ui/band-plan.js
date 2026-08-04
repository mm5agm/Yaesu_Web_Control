// Band segment data for IARU Region 1, Region 2, Region 3, and Japan.
// Frequencies in Hz. Each entry gives the representative dial frequency and
// the mode string used by this app (matches CatMessageDispatcher mode names).
//
// Region 1 = Europe, Africa, Middle East, Northern Asia (IARU R1 band plan)
// Region 2 = Americas (IARU R2; USA FCC Part 97 used as primary reference)
// Region 3 = Asia-Pacific excluding Japan (IARU R3 band plan)
// Japan    = JARL band plan (differs from IARU R3 in several key areas)
//
// FT8 frequencies (14.074, 7.074 etc.) are the same worldwide regardless of region.
// Differences are mainly in the SSB segment start, 80m/40m phone calling areas,
// and 60m allocations.

export const BAND_PLANS = {
    Region1: {
        '160m': {
            CW:   { freq:  1820000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  1840000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  1850000, mode: 'LSB',     label: 'SSB' }
        },
        '80m': {
            CW:   { freq:  3520000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  3573000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq:  3580000, mode: 'RTTY-L',  label: 'RTTY' },
            SSB:  { freq:  3690000, mode: 'LSB',     label: 'SSB' }
        },
        '60m': {
            // IARU R1 secondary allocation 5351.5–5366.5 kHz (WRC-15).
            // Individual countries within R1 have their own channel plans;
            // these entries cover the standard FT8 spot and mid-band USB.
            FT8:  { freq:  5357000, mode: 'USB',     label: 'FT8' },
            USB:  { freq:  5362000, mode: 'USB',     label: 'USB' }
        },
        '40m': {
            CW:   { freq:  7020000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  7074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq:  7040000, mode: 'RTTY-L',  label: 'RTTY' },
            SSB:  { freq:  7090000, mode: 'LSB',     label: 'SSB' }
        },
        '30m': {
            CW:   { freq: 10115000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 10136000, mode: 'DATA-U',  label: 'FT8' }
        },
        '20m': {
            CW:   { freq: 14025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 14074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 14080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 14225000, mode: 'USB',     label: 'SSB' }
        },
        '17m': {
            CW:   { freq: 18080000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 18100000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 18130000, mode: 'USB',     label: 'SSB' }
        },
        '15m': {
            CW:   { freq: 21025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 21074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 21080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 21280000, mode: 'USB',     label: 'SSB' }
        },
        '12m': {
            CW:   { freq: 24895000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 24915000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 24940000, mode: 'USB',     label: 'SSB' }
        },
        '10m': {
            CW:   { freq: 28025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 28074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 28080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 28500000, mode: 'USB',     label: 'SSB' }
        },
        '6m': {
            CW:   { freq: 50050000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 50313000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 50150000, mode: 'USB',     label: 'SSB' }
        },
        '4m': {
            // 70 MHz band; available in many Region 1 countries
            CW:   { freq: 70050000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 70154000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 70200000, mode: 'USB',     label: 'SSB' }
        }
    },

    Region2: {
        '160m': {
            CW:   { freq:  1820000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  1840000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  1850000, mode: 'LSB',     label: 'SSB' }
        },
        '80m': {
            CW:   { freq:  3510000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  3573000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  3800000, mode: 'LSB',     label: 'SSB' }
        },
        '60m': {
            // USA FCC Part 97 channels (primary R2 reference; dial frequencies shown)
            CH1:  { freq:  5330500, mode: 'USB',     label: '5.331' },
            CH2:  { freq:  5346500, mode: 'USB',     label: '5.347' },
            CH3:  { freq:  5357000, mode: 'USB',     label: '5.357' },
            CH4:  { freq:  5371500, mode: 'USB',     label: '5.372' },
            CH5:  { freq:  5403500, mode: 'USB',     label: '5.404' }
        },
        '40m': {
            CW:   { freq:  7010000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  7074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq:  7080000, mode: 'RTTY-L',  label: 'RTTY' },
            SSB:  { freq:  7200000, mode: 'LSB',     label: 'SSB' }
        },
        '30m': {
            CW:   { freq: 10115000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 10136000, mode: 'DATA-U',  label: 'FT8' }
        },
        '20m': {
            CW:   { freq: 14025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 14074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 14080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 14225000, mode: 'USB',     label: 'SSB' }
        },
        '17m': {
            CW:   { freq: 18080000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 18100000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 18130000, mode: 'USB',     label: 'SSB' }
        },
        '15m': {
            CW:   { freq: 21025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 21074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 21080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 21300000, mode: 'USB',     label: 'SSB' }
        },
        '12m': {
            CW:   { freq: 24895000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 24915000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 24940000, mode: 'USB',     label: 'SSB' }
        },
        '10m': {
            CW:   { freq: 28025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 28074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 28080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 28500000, mode: 'USB',     label: 'SSB' }
        },
        '6m': {
            CW:   { freq: 50050000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 50313000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 50125000, mode: 'USB',     label: 'SSB' }
        }
        // No 4m (70 MHz) allocation in Region 2
    },

    Region3: {
        '160m': {
            CW:   { freq:  1820000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  1840000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  1850000, mode: 'LSB',     label: 'SSB' }
        },
        '80m': {
            // R3 phone segment starts higher than R1 (~3700–3900 kHz)
            CW:   { freq:  3520000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  3573000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq:  3580000, mode: 'RTTY-L',  label: 'RTTY' },
            SSB:  { freq:  3770000, mode: 'LSB',     label: 'SSB' }
        },
        '60m': {
            // WRC-15 secondary 5351.5–5366.5 kHz; access varies by country in R3
            FT8:  { freq:  5357000, mode: 'USB',     label: 'FT8' },
            USB:  { freq:  5362000, mode: 'USB',     label: 'USB' }
        },
        '40m': {
            // R3 phone segment 7100–7300 kHz
            CW:   { freq:  7020000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  7074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq:  7040000, mode: 'RTTY-L',  label: 'RTTY' },
            SSB:  { freq:  7100000, mode: 'LSB',     label: 'SSB' }
        },
        '30m': {
            CW:   { freq: 10115000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 10136000, mode: 'DATA-U',  label: 'FT8' }
        },
        '20m': {
            CW:   { freq: 14025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 14074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 14080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 14225000, mode: 'USB',     label: 'SSB' }
        },
        '17m': {
            CW:   { freq: 18080000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 18100000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 18130000, mode: 'USB',     label: 'SSB' }
        },
        '15m': {
            CW:   { freq: 21025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 21074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 21080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 21290000, mode: 'USB',     label: 'SSB' }
        },
        '12m': {
            CW:   { freq: 24895000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 24915000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 24940000, mode: 'USB',     label: 'SSB' }
        },
        '10m': {
            CW:   { freq: 28025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 28074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 28080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 28500000, mode: 'USB',     label: 'SSB' }
        },
        '6m': {
            CW:   { freq: 50050000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 50313000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 50150000, mode: 'USB',     label: 'SSB' }
        }
        // No 4m (70 MHz) allocation in Region 3
    },

    Japan: {
        '160m': {
            // JA primary allocation 1810–1825 kHz (CW/narrow); phone on 1907.5–1912.5 kHz
            CW:   { freq:  1820000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  1840000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  1908000, mode: 'LSB',     label: 'SSB (1.9M)' }
        },
        '80m': {
            // JA phone segment ~3700–3800 kHz
            CW:   { freq:  3520000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  3573000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  3740000, mode: 'LSB',     label: 'SSB' }
        },
        // No 60m secondary allocation in Japan
        '40m': {
            // JA phone segment 7100–7200 kHz
            CW:   { freq:  7025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq:  7074000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq:  7100000, mode: 'LSB',     label: 'SSB' }
        },
        '30m': {
            CW:   { freq: 10115000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 10136000, mode: 'DATA-U',  label: 'FT8' }
        },
        '20m': {
            CW:   { freq: 14025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 14074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 14080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 14225000, mode: 'USB',     label: 'SSB' }
        },
        '17m': {
            CW:   { freq: 18080000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 18100000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 18130000, mode: 'USB',     label: 'SSB' }
        },
        '15m': {
            CW:   { freq: 21025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 21074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 21080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 21290000, mode: 'USB',     label: 'SSB' }
        },
        '12m': {
            CW:   { freq: 24895000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 24915000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 24940000, mode: 'USB',     label: 'SSB' }
        },
        '10m': {
            CW:   { freq: 28025000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 28074000, mode: 'DATA-U',  label: 'FT8' },
            RTTY: { freq: 28080000, mode: 'RTTY-U',  label: 'RTTY' },
            SSB:  { freq: 28500000, mode: 'USB',     label: 'SSB' }
        },
        '6m': {
            CW:   { freq: 50050000, mode: 'CW-U',   label: 'CW' },
            FT8:  { freq: 50313000, mode: 'DATA-U',  label: 'FT8' },
            SSB:  { freq: 50150000, mode: 'USB',     label: 'SSB' }
        }
        // No 4m (70 MHz) allocation in Japan
    }
};

// Backward-compatibility aliases: settings saved when the options were "UK" and "USA"
// continue to resolve to the correct plan without requiring a manual settings update.
BAND_PLANS.UK  = BAND_PLANS.Region1;
BAND_PLANS.USA = BAND_PLANS.Region2;

// ── Per-region band envelopes ────────────────────────────────────────────────
//
// Lower/upper frequency limits for each amateur band, in Hz. Drawn as red
// dashed guard-rail lines on the spectrum so operators see at a glance when
// they tune outside their region's amateur allocation.
//
// Sources:
//   Region 1   — IARU R1 + RSGB (UK) for HF; UK figures for 4m/6m where R1
//                varies by country
//   Region 2   — FCC Part 97 (USA)
//   Region 3   — IARU R3 (varies by country; uses representative limits)
//   Japan      — JARL band plan
//
// Where bands are internally fragmented (Japan 160m and 80m have multiple
// non-contiguous sub-bands), we ship a single broad envelope from the lowest
// sub-band's start to the highest sub-band's end. The operator's own local
// regulator's plan is the authority — these markers are a guide, not legal
// advice.
//
// Externalising this data to a JSON file in the install folder is on the
// roadmap for the next release so corrections can ship without a YWC update.
export const BAND_EDGES = {
    Region1: [
        { name: '160m', lo:   1810000, hi:   2000000 },
        { name:  '80m', lo:   3500000, hi:   3800000 },
        { name:  '60m', lo:   5351500, hi:   5366500 },
        { name:  '40m', lo:   7000000, hi:   7200000 },
        { name:  '30m', lo:  10100000, hi:  10150000 },
        { name:  '20m', lo:  14000000, hi:  14350000 },
        { name:  '17m', lo:  18068000, hi:  18168000 },
        { name:  '15m', lo:  21000000, hi:  21450000 },
        { name:  '12m', lo:  24890000, hi:  24990000 },
        { name:  '10m', lo:  28000000, hi:  29700000 },
        { name:   '6m', lo:  50000000, hi:  52000000 },
        { name:   '4m', lo:  70000000, hi:  70500000 },
    ],
    Region2: [
        { name: '160m', lo:   1800000, hi:   2000000 },
        { name:  '80m', lo:   3500000, hi:   4000000 },
        { name:  '60m', lo:   5330500, hi:   5403500 },
        { name:  '40m', lo:   7000000, hi:   7300000 },
        { name:  '30m', lo:  10100000, hi:  10150000 },
        { name:  '20m', lo:  14000000, hi:  14350000 },
        { name:  '17m', lo:  18068000, hi:  18168000 },
        { name:  '15m', lo:  21000000, hi:  21450000 },
        { name:  '12m', lo:  24890000, hi:  24990000 },
        { name:  '10m', lo:  28000000, hi:  29700000 },
        { name:   '6m', lo:  50000000, hi:  54000000 },
        // No 4m allocation in Region 2.
    ],
    Region3: [
        { name: '160m', lo:   1800000, hi:   2000000 },
        { name:  '80m', lo:   3500000, hi:   3900000 },
        { name:  '60m', lo:   5351500, hi:   5366500 },
        { name:  '40m', lo:   7000000, hi:   7300000 },
        { name:  '30m', lo:  10100000, hi:  10150000 },
        { name:  '20m', lo:  14000000, hi:  14350000 },
        { name:  '17m', lo:  18068000, hi:  18168000 },
        { name:  '15m', lo:  21000000, hi:  21450000 },
        { name:  '12m', lo:  24890000, hi:  24990000 },
        { name:  '10m', lo:  28000000, hi:  29700000 },
        { name:   '6m', lo:  50000000, hi:  54000000 },
        // No 4m allocation in Region 3.
    ],
    Japan: [
        // 160m in Japan is fragmented: CW/narrow 1810–1825 kHz and phone
        // 1907.5–1912.5 kHz. We ship one envelope spanning both sub-bands;
        // operators know the in-between gap is non-amateur.
        { name: '160m', lo:   1810000, hi:   1912500 },
        // 80m in Japan is also fragmented (multiple phone segments between
        // 3535 and 3805 kHz). Envelope captures the legal range.
        { name:  '80m', lo:   3500000, hi:   3805000 },
        // No 60m allocation in Japan.
        { name:  '40m', lo:   7000000, hi:   7200000 },
        { name:  '30m', lo:  10100000, hi:  10150000 },
        { name:  '20m', lo:  14000000, hi:  14350000 },
        { name:  '17m', lo:  18068000, hi:  18168000 },
        { name:  '15m', lo:  21000000, hi:  21450000 },
        { name:  '12m', lo:  24890000, hi:  24990000 },
        { name:  '10m', lo:  28000000, hi:  29700000 },
        { name:   '6m', lo:  50000000, hi:  54000000 },
        // No 4m allocation in Japan.
    ],
};
BAND_EDGES.UK  = BAND_EDGES.Region1;
BAND_EDGES.USA = BAND_EDGES.Region2;

// ── Nearest band ────────────────────────────────────────────────────────────
//
// Answers a different question to the tables above: not "may I transmit here"
// but "which band was the operator aiming at". Used to mark a band button red
// when the radio is outside every allocation in the operator's region — a UK
// operator on 3.9 MHz is unmistakably *at* 80m, just not legally on it, and a
// band grid with nothing lit at all doesn't say that.
//
// Distance is measured to the nearest edge of each band, so it works the same
// whether the operator has drifted off the top of a band or off the bottom.
// (An earlier attempt used a worldwide band envelope and only ever caught the
// top: for nearly every band the worldwide *lower* edge is the same as the
// region's, so tuning below a band fell outside the envelope and matched
// nothing at all.)
//
// Never use this to decide whether transmitting is permitted — a returned band
// name means the opposite, that the frequency is NOT inside it. `edges` should
// be BAND_EDGES resolved to the configured region; that is the authority.
export function nearestBandForHz(hz, edges) {
    if (!hz || hz <= 0 || !Array.isArray(edges) || edges.length === 0) return null;

    let best = null;
    let bestDistance = Infinity;
    for (const band of edges) {
        if (typeof band?.lo !== 'number' || typeof band?.hi !== 'number') continue;
        // Zero inside the band, otherwise the gap to the closer edge.
        const distance = hz < band.lo ? band.lo - hz
                       : hz > band.hi ? hz - band.hi
                       : 0;
        if (distance < bestDistance) {
            bestDistance = distance;
            best = band.name;
        }
    }
    return best;
}

// ── External JSON override ──────────────────────────────────────────────────
//
// The hardcoded BAND_PLANS / BAND_EDGES above are the shipped defaults — they
// always work, even if the JSON file is missing, corrupt, or fails to fetch.
//
// At startup, Index.cshtml calls loadBandPlanFromServer() to overlay updates
// from /bandplan.default.json (sitting in the install folder). Operators who
// notice the RSGB / FCC / JARL has tweaked a band plan can drop in an updated
// JSON file without waiting for a YWC release — restart the app and the new
// data takes effect. The format is documented in the file's _comment field.
//
// We mutate BAND_PLANS / BAND_EDGES in place rather than re-exporting so
// existing consumers (Index.cshtml's `BAND_PLANS[region]` access pattern,
// segmentForHz, getSegments, etc.) pick up the new values automatically.
export async function loadBandPlanFromServer() {
    try {
        // Cache-bust on the URL so users dropping in an updated JSON see it
        // immediately on next reload rather than the browser's cached copy.
        const res = await fetch('/bandplan.default.json?t=' + Date.now());
        if (!res.ok) return false;
        const data = await res.json();
        if (data?.bandPlans && typeof data.bandPlans === 'object') {
            for (const key of Object.keys(BAND_PLANS)) delete BAND_PLANS[key];
            Object.assign(BAND_PLANS, data.bandPlans);
            BAND_PLANS.UK  = BAND_PLANS.Region1;
            BAND_PLANS.USA = BAND_PLANS.Region2;
        }
        if (data?.bandEdges && typeof data.bandEdges === 'object') {
            for (const key of Object.keys(BAND_EDGES)) delete BAND_EDGES[key];
            Object.assign(BAND_EDGES, data.bandEdges);
            BAND_EDGES.UK  = BAND_EDGES.Region1;
            BAND_EDGES.USA = BAND_EDGES.Region2;
        }
        return true;
    } catch {
        // Network failure / bad JSON — keep the hardcoded defaults.
        return false;
    }
}

export function getSegments(bandPlan, band) {
    const plan = BAND_PLANS[bandPlan] || BAND_PLANS['Region1'];
    return plan[band] || null;
}

/**
 * The band-edge entry for a named band in a given region, or null.
 * Segments are activity centres with no upper bound of their own, so the
 * edges are what stops the top segment claiming everything above it.
 */
export function edgeForBand(bandPlan, band) {
    const edges = BAND_EDGES[bandPlan] || BAND_EDGES['Region1'];
    if (!Array.isArray(edges) || !band) return null;
    const wanted = band.toLowerCase();
    return edges.find(e => typeof e?.name === 'string' && e.name.toLowerCase() === wanted) || null;
}

/**
 * Which segment of the given band contains the given frequency.
 * Each segment "owns" from its representative frequency up to (but not
 * including) the next segment's frequency, in ascending order. Returns the
 * segment key (e.g. "FT8", "SSB") or null if the band isn't in the plan
 * or the frequency is outside the band's edges in this region.
 *
 * Used to keep the per-VFO Segment dropdown showing the segment the
 * operator is currently in, even when they tune via the spectrum or
 * the radio's front-panel knob instead of picking from the dropdown.
 */
export function segmentForHz(bandPlan, band, hz) {
    const segments = getSegments(bandPlan, band);
    if (!segments) return null;
    // Segments have a lower bound but no upper one, so without this check
    // the highest segment would keep claiming the frequency however far
    // above the band the operator tunes. Out of band is not a segment.
    const edge = edgeForBand(bandPlan, band);
    if (edge && (hz < edge.lo || hz > edge.hi)) return null;
    // Sort segment keys by frequency ascending so we can pick the last one
    // whose freq <= hz. Object key order in modern JS is insertion order,
    // which is already ascending in the band-plan data, but sort
    // defensively in case the data is edited.
    const ordered = Object.entries(segments).sort((a, b) => a[1].freq - b[1].freq);
    let match = null;
    for (const [key, seg] of ordered) {
        if (typeof seg.freq !== 'number') continue;
        if (hz >= seg.freq) match = key;
        else break;
    }
    // Inside the band but below the lowest segment: default to the first
    // (lowest-freq) segment. On HF that's typically CW — and 14.010 MHz is
    // still in the 20 m CW sub-band even though it's below the CW watering
    // hole at 14.025. Without this fallback the dropdown would go blank for
    // frequencies between the band edge and the first activity centre.
    // The edge check above has already rejected genuinely-below-band, so
    // this only ever fires for frequencies the operator may actually use.
    if (match === null && ordered.length > 0) match = ordered[0][0];
    return match;
}

/**
 * Best-guess radio mode for a given frequency, based on the usual amateur
 * sub-band assignments. Used when the operator clicks a new frequency on the
 * spectrum so the mode follows them without a separate mode-button press.
 *
 * Region-agnostic: the digital and CW sub-bands are broadly the same across
 * IARU regions, so a single lookup covers all four. The 60m channels return
 * USB because every region uses USB on 5 MHz.
 *
 * Returns one of the mode names accepted by window.setMode() (LSB, USB, CW-U,
 * DATA-U, RTTY-U, FM, AM). Returns null for frequencies outside known amateur
 * bands so the caller can decide not to change mode.
 */
export function modeForHz(hz) {
    const khz = hz / 1000;
    // 160m
    if (khz >= 1800 && khz < 1838) return 'CW-U';
    if (khz >= 1838 && khz < 1843) return 'DATA-U';   // FT8 1840
    if (khz >= 1843 && khz < 2000) return 'LSB';
    // 80m
    if (khz >= 3500 && khz < 3570) return 'CW-U';
    if (khz >= 3570 && khz < 3620) return 'DATA-U';   // FT8 3573, FT4 3575
    if (khz >= 3620 && khz < 4000) return 'LSB';
    // 60m — USB worldwide
    if (khz >= 5250 && khz < 5450) return 'USB';
    // 40m
    if (khz >= 7000 && khz < 7040) return 'CW-U';
    if (khz >= 7040 && khz < 7100) return 'DATA-U';   // FT4 7047, FT8 7074
    if (khz >= 7100 && khz < 7300) return 'LSB';
    // 30m — CW + digital only
    if (khz >= 10100 && khz < 10130) return 'CW-U';
    if (khz >= 10130 && khz < 10150) return 'DATA-U'; // FT8 10136, FT4 10140
    // 20m
    if (khz >= 14000 && khz < 14070) return 'CW-U';
    if (khz >= 14070 && khz < 14099) return 'DATA-U'; // FT8 14074, RTTY 14080
    if (khz >= 14099 && khz < 14350) return 'USB';
    // 17m
    if (khz >= 18068 && khz < 18095) return 'CW-U';
    if (khz >= 18095 && khz < 18109) return 'DATA-U'; // FT8 18100, FT4 18104
    if (khz >= 18109 && khz < 18168) return 'USB';
    // 15m
    if (khz >= 21000 && khz < 21070) return 'CW-U';
    if (khz >= 21070 && khz < 21149) return 'DATA-U'; // FT8 21074, FT4 21140
    if (khz >= 21149 && khz < 21450) return 'USB';
    // 12m
    if (khz >= 24890 && khz < 24915) return 'CW-U';
    if (khz >= 24915 && khz < 24929) return 'DATA-U'; // FT8 24915, FT4 24919
    if (khz >= 24929 && khz < 24990) return 'USB';
    // 10m
    if (khz >= 28000 && khz < 28070) return 'CW-U';
    if (khz >= 28070 && khz < 28190) return 'DATA-U'; // FT8 28074, FT4 28180
    if (khz >= 28190 && khz < 28225) return 'USB';
    if (khz >= 28225 && khz < 29000) return 'USB';
    if (khz >= 29000 && khz < 29700) return 'FM';
    // 6m
    if (khz >= 50000 && khz < 50100) return 'CW-U';
    if (khz >= 50100 && khz < 50500) return 'USB';
    if (khz >= 50500 && khz < 54000) return 'FM';
    // 4m
    if (khz >= 70000 && khz < 70500) return 'USB';
    // 2m
    if (khz >= 144000 && khz < 144500) return 'USB';
    if (khz >= 144500 && khz < 148000) return 'FM';

    return null;
}
