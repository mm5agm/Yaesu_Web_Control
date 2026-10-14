// radio-panel-yaesu-windows.js - which windows YWC's radio panel offers, and
// what goes in them.
//
// The window frame (launcher keys, drag, close, tear-off, remembered places)
// is shared: core/js/radio-panel/panel-windows.js. This file is the Yaesu
// side - the list of windows and their panels, and filling each panel's body.
//
// Positions are [left, top, width, height] in the panel's own 1600-wide board
// pixels, so they are the same at every zoom.

export const YAESU_WINDOWS = [
    { id: 'cw', key: 'CW', title: 'CW', pos: [330, 230, 960, 330], panels: [
        { id: 'keyer', title: 'KEYER', w: 220 },
        { id: 'cwReader', title: 'READER', dpos: [380, 610, 700, 300] },
        { id: 'cwSend', title: 'SEND', w: 260 },
    ] },
    { id: 'rtty', key: 'RTTY', title: 'RTTY', pos: [360, 560, 960, 300], panels: [
        { id: 'tuner', title: 'TUNER', w: 200 },
        { id: 'rttyReader', title: 'READER', dpos: [380, 250, 660, 280] },
        { id: 'rttySend', title: 'SEND', w: 250 },
    ] },
    { id: 'mem', key: 'MEMORIES', title: 'MEMORIES', pos: [600, 150, 900, 440], panels: [
        { id: 'memBank', title: 'BANK', w: 140 },
        { id: 'memTiles', title: 'CHANNELS' },
        { id: 'memRadio', title: 'RADIO', w: 170 },
    ] },
    { id: 'dx', key: 'DX', title: 'DX CLUSTER', pos: [840, 150, 720, 460], panels: [
        { id: 'dxSpots', title: 'SPOTS' },
        { id: 'dxWatch', title: 'WATCH LIST', w: 250 },
    ] },
    { id: 'meters', key: 'METERS', title: 'METERS', pos: [330, 420, 1000, 420], panels: [
        { id: 'mGauges', title: 'ALL GAUGES', w: 320 },
        { id: 'mHistory', title: 'S-METER HISTORY' },
    ] },
    { id: 'audio', key: 'AUDIO', title: 'REMOTE AUDIO', pos: [330, 150, 800, 300], panels: [
        { id: 'aLink', title: 'LINK', w: 240 },
        { id: 'aRx', title: 'RECEIVE' },
        { id: 'aTx', title: 'TRANSMIT' },
    ] },
    { id: 'scope', key: 'SCOPE', title: 'SCOPE · SCREEN', pos: [320, 200, 980, 340], panels: [
        { id: 'scCat', title: "RADIO'S OWN SCOPE (CAT)" },
        { id: 'scScreen', title: 'RADIO SCREEN (VIDEO)' },
    ] },
    { id: 'rx', key: 'RX SETUP', title: 'RX SETUP', pos: [280, 200, 1100, 340], panels: [
        { id: 'rxFilter', title: 'FILTER' },
        { id: 'rxShift', title: 'SHIFT · NOTCH', w: 230 },
        { id: 'rxFm', title: 'FM · REPEATER', w: 220 },
    ] },
    { id: 'tx', key: 'TX SETUP', title: 'TX SETUP', pos: [340, 560, 920, 300], panels: [
        { id: 'txVox', title: 'VOX' },
        { id: 'txTimeout', title: 'TX TIMEOUT' },
        { id: 'txAtu', title: 'ATU', w: 230 },
    ] },
    // No launcher key: opened from a VFO box.
    { id: 'kbd', title: 'FREQUENCY ENTRY', pos: [560, 220, 380, 460], panels: [
        { id: 'kbdPad', title: 'ENTER' },
    ] },
];

/**
 * Fill the window panels. Each panel's body is handed out once and kept for
 * the page's life; `onShow` from the panel says when one is on screen.
 * @returns {(groupId: string, panelId: string, shown: boolean) => void}
 */
export function startYaesuWindows(windows) {
    for (const g of YAESU_WINDOWS) {
        for (const p of g.panels) {
            const el = windows.slot(g.id, p.id);
            if (el && !el.childElementCount) {
                const n = document.createElement('div');
                n.className = 'rp-wnote';
                n.textContent = 'Not built yet.';
                el.append(n);
            }
        }
    }
    return () => {};
}
