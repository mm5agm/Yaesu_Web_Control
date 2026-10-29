// radio-panel-style.js - the radio panel's stylesheet, injected from JS.
//
// It lives in a .js file because the apps' CopySharedCoreJs build target copies
// core/js/**/*.js and nothing else, so a .css file here would never reach either
// app. Every colour is a --rp-* custom property set by radio-panel-themes.js;
// there are no literal colours below, which is what makes themes complete.
//
// Fonts are the apps' own vendored copies (wwwroot/lib/fonts), not a CDN: both
// apps run on shack PCs that may never be online. `fontBase` points at them.

export function injectRadioPanelStyle(doc = document, fontBase = '/lib/fonts') {
    if (doc.getElementById('rp-style')) return;
    const el = doc.createElement('style');
    el.id = 'rp-style';
    el.textContent = css(fontBase);
    doc.head.appendChild(el);
}

const v = name => `var(--rp-${name})`;
const SANS = "'Barlow Condensed', 'Arial Narrow', sans-serif";
const MONO = "'Share Tech Mono', Consolas, monospace";

function css(fontBase) {
    const bc = w => `@font-face{font-family:'Barlow Condensed';font-style:normal;font-weight:${w};font-display:swap;src:url('${fontBase}/barlow-condensed/files/barlow-condensed-latin-${w}-normal.woff2') format('woff2')}`;
    return `
${bc(500)}${bc(600)}${bc(700)}
@font-face{font-family:'Share Tech Mono';font-style:normal;font-weight:400;font-display:swap;src:url('${fontBase}/share-tech-mono/files/share-tech-mono-latin-400-normal.woff2') format('woff2')}

html,body{margin:0;background:${v('page')}}
.rp-page{min-height:100vh;box-sizing:border-box;background:${v('page')};font-family:${SANS};color:${v('text')};overflow:hidden}
.rp-scale{width:1600px;transform-origin:0 0;padding:20px;box-sizing:border-box}
.rp-board-grip{position:absolute;right:0;bottom:0;width:26px;height:26px;cursor:nwse-resize;touch-action:none;z-index:50;background:linear-gradient(135deg,transparent 52%,${v('legend-dim')} 52% 58%,transparent 58% 68%,${v('legend-dim')} 68% 74%,transparent 74% 84%,${v('legend-dim')} 84% 90%,transparent 90%)}
.rp-panel{box-sizing:border-box;padding:22px 26px 24px;border-radius:18px;background:${v('panel')};border:1px solid ${v('panel-border')};box-shadow:inset 0 1px 0 rgba(255,255,255,.06),0 18px 40px rgba(0,0,0,.55);display:flex;flex-direction:column;gap:18px}
.rp-panel *{box-sizing:border-box}
.rp-panel button{font-family:${SANS}}

.rp-legend{font:600 15px ${SANS};letter-spacing:.14em;color:${v('legend')};text-transform:uppercase}
.rp-sub{font:400 13px ${MONO};color:${v('sub')};letter-spacing:0;min-height:13px;line-height:13px}
.rp-key{appearance:none;border:1px solid ${v('key-border')};background:${v('key-bg')};color:${v('key-fg')};border-radius:7px;height:58px;font:600 17px ${SANS};letter-spacing:.08em;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:3px;cursor:pointer;padding:0;box-shadow:0 3px 0 ${v('key-shadow')},inset 0 1px 0 rgba(255,255,255,.07)}
.rp-key:active{transform:translateY(2px);box-shadow:0 1px 0 ${v('key-shadow')}}
.rp-key[disabled]{cursor:default}
.rp-key[disabled]:active{transform:none;box-shadow:0 3px 0 ${v('key-shadow')},inset 0 1px 0 rgba(255,255,255,.07)}
.rp-led{width:30px;height:5px;border-radius:2px;background:${v('led-off')}}
.rp-led.on{background:${v('led-on')};box-shadow:0 0 7px ${v('led-on')}}
.rp-led.tx{background:${v('tx')};box-shadow:0 0 8px ${v('tx')}}
.rp-lcd{font:400 17px ${MONO};color:${v('lcd-fg')};background:${v('lcd-bg')};border:1px solid ${v('lcd-border')};border-radius:4px;padding:4px 10px;min-width:84px;text-align:center;white-space:nowrap}
.rp-soft{height:56px;border-radius:6px;border:1px solid ${v('soft-border')};background:${v('soft-bg')};color:${v('soft-fg')};font:600 20px ${SANS};letter-spacing:.06em;cursor:pointer;padding:0 4px}
.rp-soft.on{background:${v('soft-on-bg')};border-color:${v('highlight')};color:${v('soft-on-fg')};box-shadow:inset 0 0 0 1px ${v('highlight')}}
.rp-soft[disabled]{cursor:default}
.rp-chip{font:600 17px ${SANS};letter-spacing:.08em;padding:4px 11px;border-radius:4px;border:1px solid ${v('chip-border')};color:${v('chip-fg')};white-space:nowrap}
.rp-chip:empty{display:none}
.rp-chip.mode{color:${v('freq')};border-color:${v('highlight')}}
.rp-chip.rx{border-color:${v('rx')};color:${v('rx-fg')}}
.rp-chip.tx{background:${v('tx')};color:${v('tx-fg')};border-color:${v('tx')}}
.rp-chip.dim{border-color:${v('chip-dim-border')};color:${v('chip-dim-fg')}}
.rp-chip.hidden{display:none}
.rp-chip.sel{background:${v('highlight')};border-color:${v('highlight')};color:${v('screen')}}
.rp-chip.oob{background:${v('tx')};border-color:${v('tx')};color:${v('tx-fg')}}
.rp-chipbtn{appearance:none;border:1px solid ${v('key-border')};background:${v('chipbtn-bg')};color:${v('chipbtn-fg')};border-radius:5px;height:40px;font:600 16px ${SANS};letter-spacing:.1em;cursor:pointer;padding:0}
.rp-chipbtn.on{background:${v('accent-bg')};border-color:${v('accent')};color:${v('accent-soft')}}
.rp-lamp{width:12px;height:12px;border-radius:50%;background:${v('lamp-off')};display:inline-block}
.rp-lamp.on{background:${v('lamp-on')};box-shadow:0 0 6px ${v('lamp-on')}}
.rp-lamp.warn{background:${v('accent')};box-shadow:0 0 6px ${v('accent')}}
.rp-lamp.bad{background:${v('tx')};box-shadow:0 0 6px ${v('tx')}}
.rp-bar{display:flex;gap:4px;background:${v('bar-bg')};border:1px solid ${v('well-border')};border-radius:6px;padding:3px;align-items:center}
.rp-viewbtn{appearance:none;border:0;background:transparent;color:${v('sub')};font:600 16px ${SANS};letter-spacing:.12em;text-transform:uppercase;padding:8px 16px;border-radius:4px;cursor:pointer;text-decoration:none}
.rp-viewbtn.on{background:${v('bar-on')};color:${v('title')}}
.rp-select{appearance:none;background:transparent;border:0;color:${v('sub')};font:600 16px ${SANS};letter-spacing:.1em;text-transform:uppercase;padding:6px 10px;cursor:pointer}
.rp-select option{background:${v('panel')};color:${v('text')}}

.rp-top{height:50px;display:flex;align-items:center;justify-content:space-between;gap:24px}
.rp-brand{display:flex;align-items:baseline;gap:14px}
.rp-brand-short{font-size:34px;font-weight:700;letter-spacing:.18em;color:${v('title')}}
.rp-brand-ver{font:500 14px ${SANS};letter-spacing:.04em;color:${v('legend-dim')}}
.rp-brand-model{font:600 23px ${SANS};letter-spacing:.1em;color:${v('accent')}}
.rp-lamps{display:flex;align-items:center;gap:18px}
.rp-lamps>span,.rp-lampkey{display:flex;align-items:center;gap:7px}
.rp-lampkey{font:inherit;color:inherit;letter-spacing:inherit;background:none;border:0;padding:0;cursor:pointer}
.rp-top-right{display:flex;align-items:center;gap:14px}
.rp-menu{position:relative}
.rp-menu-list{position:absolute;right:-4px;top:calc(100% + 8px);z-index:99999;min-width:240px;display:flex;flex-direction:column;padding:6px 0;border-radius:6px;background:${v('bar-bg')};border:1px solid ${v('well-border')};box-shadow:0 10px 28px rgba(0,0,0,.6)}
.rp-menu-list[hidden]{display:none}
.rp-menu-list.rp-menu-left{left:-4px;right:auto}
.rp-menu-group{padding:8px 16px 4px;font:600 13px ${SANS};letter-spacing:.14em;color:${v('legend-dim')}}
.rp-menu-item{padding:7px 16px 7px 28px;font:500 17px ${SANS};color:${v('text')};text-decoration:none;white-space:nowrap}
.rp-keylist{top:0;max-height:70vh;overflow-y:auto}
button.rp-menu-item{background:none;border:0;text-align:left;cursor:pointer}
.rp-menu-item.on{color:${v('title')}}
.rp-menu-item:hover,.rp-menu-item:focus-visible{background:${v('bar-on')};color:${v('title')};outline:none}

.rp-body{height:800px;display:grid;grid-template-columns:292px minmax(0,1fr) 350px;gap:22px}
.rp-left{display:flex;flex-direction:column;gap:12px}
.rp-meter-well{border-radius:10px;background:${v('meter-well')};padding:7px;box-shadow:inset 0 2px 6px rgba(0,0,0,.8);border:1px solid ${v('well-border')}}
.rp-meter{display:block;border-radius:6px;background:${v('meter-face')}}
.rp-meter .ink{stroke:${v('meter-ink')};fill:none;stroke-width:1.4}
.rp-meter .ink2{stroke:${v('meter-ink-2')};fill:none;stroke-width:1.1}
.rp-meter .red{stroke:${v('meter-red')};fill:none;stroke-width:4}
.rp-meter text{font-family:${MONO}}
.rp-meter .t-ink{fill:${v('meter-ink')}}
.rp-meter .t-ink2{fill:${v('meter-ink-2')}}
.rp-meter .t-red{fill:${v('meter-red')}}
.rp-meter .t-title{font-family:${SANS};font-weight:700}
.rp-meter .needle{stroke:${v('needle')};stroke-width:1.8;stroke-linecap:round}
.rp-meter .hub{fill:${v('meter-hub')}}
.rp-meter-btns{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:5px}
.rp-meter-btns.many{gap:4px}
.rp-meter-btns.many .rp-chipbtn{height:34px;font-size:14px;letter-spacing:.04em}
.rp-keys{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px 12px}
.rp-panel{position:relative}
.rp-pop-head{display:flex;align-items:center;gap:8px;border-top:1px solid ${v('well-border')};padding-top:6px}
.rp-pop-head .rp-legend{font-size:14px;color:${v('legend-dim')}}
.rp-pop-hint{font:400 13px ${MONO};color:${v('sub-freq-small')};white-space:nowrap;overflow:hidden}
.rp-pop-keys{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:6px}
.rp-skey{appearance:none;border:1px solid ${v('key-border')};background:${v('key-bg')};color:${v('key-fg')};border-radius:6px;height:40px;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:2px;cursor:pointer;padding:0;box-shadow:0 2px 0 ${v('key-shadow')}}
.rp-skey .rp-led{width:22px;height:4px}
.rp-skey-label{font:600 14px ${SANS};letter-spacing:.06em;white-space:nowrap}
.rp-skey .rp-sub{font-size:11px;min-height:11px;line-height:11px;color:${v('key-fg')};opacity:.75}
.rp-skey.suits{border-color:${v('highlight')}}
.rp-skey:active{transform:translateY(2px);box-shadow:none}
.rp-win{position:absolute;z-index:10;border-radius:8px;overflow:hidden;background:${v('panel')};border:1px solid ${v('key-border')};box-shadow:0 18px 44px rgba(0,0,0,.75);display:flex;flex-direction:column;min-width:200px;min-height:120px}
.rp-win-bar{height:34px;flex:none;display:flex;align-items:center;gap:10px;padding:0 10px;background:${v('key-bg')};border-bottom:1px solid ${v('key-border')};cursor:move;user-select:none;touch-action:none}
.rp-win-title{font:700 16px ${SANS};letter-spacing:.1em;color:${v('title')};white-space:nowrap}
.rp-win-sub{font:400 13px ${MONO};color:${v('sub-label')};white-space:nowrap;overflow:hidden;text-overflow:ellipsis;min-width:0;flex:1}
.rp-win-join{appearance:none;font:600 13px ${SANS};letter-spacing:.1em;color:${v('soft-fg')};background:${v('soft-bg')};border:1px solid ${v('chip-border')};border-radius:4px;padding:2px 8px;cursor:pointer;white-space:nowrap}
.rp-win-join[hidden]{display:none}
.rp-win-x{appearance:none;border:0;background:${v('accent-bg')};color:${v('accent-soft')};border-radius:4px;width:26px;height:22px;font:700 14px sans-serif;cursor:pointer;padding:0;flex:none}
.rp-win-body{flex:1;min-height:0;display:flex;gap:8px;padding:8px}
.rp-win-grip{position:absolute;right:0;bottom:0;width:16px;height:16px;cursor:nwse-resize;touch-action:none;background:linear-gradient(135deg,transparent 50%,${v('key-border')} 50%)}
.rp-wpanel{min-width:0;display:flex;flex-direction:column;border:1px solid ${v('well-border')};border-radius:6px;background:${v('bar-bg')};overflow:hidden}
.rp-wpanel-head{height:26px;flex:none;display:flex;align-items:center;gap:6px;padding:0 6px 0 10px;border-bottom:1px solid ${v('well-border')};background:${v('chipbtn-bg')}}
.rp-wpanel-title{font:700 14px ${SANS};letter-spacing:.14em;color:${v('lcd-fg')};white-space:nowrap;margin-right:auto;overflow:hidden;text-overflow:ellipsis}
.rp-wbtn{appearance:none;border:1px solid ${v('chip-border')};background:${v('soft-bg')};color:${v('soft-fg')};border-radius:3px;width:22px;height:18px;font:600 12px sans-serif;cursor:pointer;padding:0;line-height:1;flex:none}
.rp-wbtn[hidden]{display:none}
.rp-wbtn.rp-wx{border-color:${v('key-border')};background:${v('key-bg')};color:${v('chipbtn-fg')}}
.rp-wnote{font:500 14px ${SANS};color:${v('legend-dim')}}
.rp-wpanel-body{flex:1;min-height:0;display:flex;flex-direction:column;gap:7px;padding:8px 10px;overflow:auto;font:500 15px ${SANS};color:${v('text')}}
/* What goes in a window panel: the adapter builds these, the theme colours them. */
.rp-wpanel-body.flush{padding:0;overflow:hidden}
.rp-wframe{flex:1;width:100%;min-height:0;border:0;display:block;background:${v('screen')}}
.rp-wrow{display:flex;align-items:baseline;justify-content:space-between;gap:8px}
.rp-wlabel{font:600 13px ${SANS};letter-spacing:.12em;color:${v('sub-label')}}
.rp-wval{font:600 18px ${MONO};color:${v('lcd-fg')};white-space:nowrap}
.rp-wbtns{display:flex;flex-wrap:wrap;gap:6px}
.rp-wkey{appearance:none;border:1px solid ${v('soft-border')};background:${v('soft-bg')};color:${v('soft-fg')};border-radius:5px;min-height:32px;padding:3px 10px;font:600 14px ${SANS};letter-spacing:.06em;cursor:pointer}
.rp-wkey.on{background:${v('soft-on-bg')};border-color:${v('highlight')};color:${v('soft-on-fg')}}
.rp-wkey:disabled{opacity:.45;cursor:default}
.rp-wkey.warn{border-color:${v('accent')};color:${v('accent-soft')}}
/* A key that transmits, and the note that says so. Armed: one more press keys the radio. */
.rp-wnote.tx{color:${v('tx')};font-weight:600}
.rp-wkey.tx{border-color:${v('tx')};color:${v('tx')}}
.rp-wkey.tx.armed{background:${v('tx')};color:${v('tx-fg')}}
.rp-wval.tx{color:${v('tx')}}
.rp-status-chip{font-size:14px;padding:2px 8px}
.rp-clock{font-variant-numeric:tabular-nums;letter-spacing:.06em;margin-right:6px}
.rp-txbanner{position:fixed;left:16px;right:16px;top:8px;z-index:100000;display:flex;align-items:center;gap:18px;padding:10px 16px;border-radius:8px;background:${v('tx')};color:${v('tx-fg')};font:700 20px ${SANS};letter-spacing:.04em;box-shadow:0 6px 24px rgba(0,0,0,.6)}
.rp-txbanner.hidden{display:none}
.rp-txbanner-text{flex:1}
.rp-txbanner-btn{font:700 16px ${SANS};letter-spacing:.08em;padding:6px 14px;border-radius:5px;border:2px solid ${v('tx-fg')};background:transparent;color:${v('tx-fg')};cursor:pointer}
.rp-wbar{display:grid;grid-template-columns:62px 1fr 92px;align-items:center;gap:8px}
.rp-wbar .lbl{font:600 13px ${SANS};letter-spacing:.12em;color:${v('sub-label')}}
.rp-wbar .track{height:10px;background:${v('sbar-track')};border-radius:2px;overflow:hidden}
.rp-wbar .fill{width:0;height:100%;background:${v('highlight')};transition:width .12s linear}
.rp-wbar.tx .fill{background:${v('tx')}}
.rp-wbar .txt{font:600 14px ${MONO};color:${v('lcd-fg')};text-align:right;white-space:nowrap}
.rp-wcanvas{flex:1;min-height:0;width:100%;display:block;background:${v('screen')};border-radius:4px}
.rp-wselect{font:500 14px ${SANS};background:${v('chipbtn-bg')};color:${v('chipbtn-fg')};border:1px solid ${v('key-border')};border-radius:4px;padding:3px 6px;max-width:100%}
.rp-wlist{display:flex;flex-direction:column;gap:4px;overflow:auto;min-height:0}
.rp-wtiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:6px;overflow:auto;min-height:0;align-content:start}
.rp-wtile{appearance:none;text-align:left;border:1px solid ${v('soft-border')};background:${v('soft-bg')};color:${v('soft-fg')};border-radius:5px;padding:5px 8px;cursor:pointer;display:flex;flex-direction:column;gap:1px;min-width:0}
.rp-wtile:hover{border-color:${v('highlight')}}
.rp-wtile.on{border-color:${v('highlight')};box-shadow:inset 0 0 0 1px ${v('highlight')}}
.rp-wtile .t{font:600 14px ${SANS};white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.rp-wtile .f{font:600 15px ${MONO};color:${v('lcd-fg')}}
.rp-wtile .m{font:500 12px ${SANS};letter-spacing:.08em;color:${v('sub-label')}}
.rp-wentry{font:600 30px ${MONO};color:${v('freq')};background:${v('screen')};border:1px solid ${v('screen-line')};border-radius:5px;padding:6px 10px;text-align:right;min-height:52px}
.rp-wpad{display:grid;grid-template-columns:repeat(3,1fr);gap:6px;flex:1}
.rp-wpad .rp-wkey{font-size:22px;min-height:44px}
.rp-chip.ent{margin-left:auto}

.rp-screen-well{border-radius:12px;background:${v('well')};padding:10px;border:1px solid ${v('well-border')};box-shadow:inset 0 2px 8px rgba(0,0,0,.9);min-width:0;display:flex}
.rp-screen{flex:1;min-width:0;border-radius:6px;background:${v('screen')};border:1px solid ${v('screen-line')};padding:14px 18px;display:flex;flex-direction:column;gap:11px}
.rp-vfos{position:relative;display:flex;gap:20px;align-items:stretch;height:196px}
.rp-chip.act{cursor:pointer}
.rp-chip.act:hover{outline:1px solid ${v('highlight')};outline-offset:1px}
.rp-picker{position:absolute;z-index:20;display:flex;flex-wrap:wrap;gap:6px;max-width:440px;padding:8px;border-radius:6px;background:${v('screen')};border:1px solid ${v('highlight')};box-shadow:0 6px 18px rgba(0,0,0,.55)}
.rp-picker[hidden]{display:none}
.rp-picker .rp-chipbtn{font-size:17px;padding:7px 12px;min-width:64px}
.rp-freq.wheel span[data-step]:hover,.rp-subfreq.wheel span[data-step]:hover{background:${v('accent-bg')};border-radius:4px;cursor:ns-resize}
.rp-main{flex:1;min-width:0;container-type:inline-size;display:flex;flex-direction:column;justify-content:space-between;border-right:1px solid ${v('screen-line')};padding-right:16px}
.rp-main.single{border-right:0;padding-right:0}
.rp-main.sel,.rp-subvfo.sel{outline:2px solid ${v('highlight')};outline-offset:6px;border-radius:3px}
.rp-vfos.pick .rp-main,.rp-vfos.pick .rp-subvfo{cursor:pointer}
.rp-vfo-head{display:flex;align-items:center;gap:8px}
.rp-vfo-name{font:700 19px ${SANS};letter-spacing:.16em;color:${v('accent')}}
.rp-freq{display:flex;align-items:baseline;font-family:${MONO};color:${v('freq')};line-height:1;white-space:nowrap}
/* "14.073.440" is about 4.3x the big size wide: 21cqw keeps it inside the VFO box */
.rp-freq .big{font-size:min(112px,21cqw);letter-spacing:-.02em}
.rp-freq .small{font-size:min(64px,12cqw);color:${v('freq-small')}}
.rp-chips{display:flex;gap:6px;flex-wrap:wrap;min-height:31px}
.rp-subvfo{width:320px;display:flex;flex-direction:column;justify-content:space-between}
.rp-subvfo .rp-vfo-name{font-size:18px;color:${v('sub-label')}}
.rp-subfreq{font-family:${MONO};color:${v('sub-freq')};line-height:1;white-space:nowrap}
.rp-subfreq .big{font-size:56px}
.rp-subfreq .small{font-size:34px;color:${v('sub-freq-small')}}
.rp-sbar{display:flex;align-items:center;gap:8px}
.rp-sbar .lbl{font:600 15px ${SANS};letter-spacing:.14em;color:${v('sub-label')}}
.rp-sbar .track{flex:1;height:10px;background:${v('sbar-track')};border-radius:2px;overflow:hidden}
.rp-sbar .fill{width:0;height:100%;background:${v('highlight')};transition:width .12s linear}
.rp-sbar .txt{font:600 16px ${SANS};letter-spacing:.06em;color:${v('sub-label')};min-width:86px;text-align:right}

.rp-spectrum{flex:1;min-height:200px;position:relative;border:1px solid ${v('screen-line')};border-radius:4px;overflow:hidden;background:${v('spec-bg')}}
.rp-spectrum canvas,.rp-waterfall canvas{position:absolute;inset:0;width:100%;height:100%;display:block}
.rp-passband{position:absolute;top:0;bottom:0;background:${v('passband')};border-left:1px solid ${v('passband-edge')};border-right:1px solid ${v('passband-edge')};display:none}
.rp-pbcentre{position:absolute;top:0;bottom:0;width:1px;background:${v('passband-edge')};display:none}
.rp-cursor{position:absolute;top:0;bottom:0;left:50%;width:1px;background:${v('cursor')}}
.rp-spot{position:absolute;transform:translateX(-50%);font:600 16px ${SANS};letter-spacing:.06em;color:${v('spot-fg')};background:${v('spot-bg')};border-radius:3px;padding:2px 7px;white-space:nowrap}
.rp-spec-label{position:absolute;left:10px;top:8px;font:400 15px ${MONO};color:${v('spec-label')}}
.rp-tunable{cursor:crosshair}
.rp-edge{position:absolute;top:0;bottom:2px;width:0;border-left:2px dashed ${v('tx')};pointer-events:none}
.rp-mark{position:absolute;bottom:4px;height:14px;width:0;border-left:1px solid ${v('spec-label')};pointer-events:none}
.rp-mark-label{position:absolute;transform:translateX(-50%);font:500 13px ${SANS};color:${v('spec-label')};white-space:nowrap;pointer-events:none}
.rp-xhair{position:absolute;top:0;bottom:0;width:0;border-left:1px dashed ${v('spec-label')};opacity:.7;pointer-events:none;z-index:2}
.rp-xread{position:absolute;bottom:6px;margin-left:6px;padding:1px 6px;border-radius:3px;background:${v('spec-bg')};color:${v('spec-label')};font:400 14px ${MONO};white-space:nowrap;pointer-events:none;z-index:3}
.rp-xread.left{transform:translateX(-100%);margin-left:-6px}
.rp-pin{position:absolute;top:0;bottom:0;width:0;border-left:2px solid ${v('spot-bg')};pointer-events:none;z-index:2}
.rp-pin-read{position:absolute;bottom:34px;margin-left:6px;padding:1px 6px;border-radius:3px;border:1px solid ${v('spot-bg')};background:${v('spec-bg')};color:${v('spec-label')};font:600 14px ${MONO};white-space:nowrap;pointer-events:none;z-index:3}
.rp-pin-read.left{transform:translateX(-100%);margin-left:-6px}
.rp-spec-oob{display:none;position:absolute;left:50%;top:8px;transform:translateX(-50%);padding:2px 10px;border-radius:4px;background:${v('tx')};color:${v('tx-fg')};font:600 14px ${SANS};letter-spacing:.12em;z-index:2}
.rp-spec-oob.on{display:block}
.rp-spec-tools{position:absolute;left:6px;bottom:6px;z-index:3;display:flex;gap:4px;align-items:stretch;cursor:default}
.rp-spec-span{font-size:13px;padding:3px 8px;border-radius:5px;background:${v('bar-bg')};border:1px solid ${v('well-border')}}
.rp-spec-tools .rp-wkey{min-height:26px;padding:2px 8px;font-size:12px}
.rp-spec-tools .rp-wkey.on{background:${v('tx')};border-color:${v('tx')};color:${v('tx-fg')}}
.rp-specpane{position:relative}
.rp-specpane.held .rp-spectrum{outline:2px solid ${v('tx')};outline-offset:-2px}
.rp-spec-settings{display:none;position:absolute;left:6px;bottom:42px;z-index:6;padding:10px 12px;gap:6px;flex-direction:column;border-radius:6px;background:${v('soft-bg')};border:1px solid ${v('soft-border')};color:${v('soft-fg')};font:500 13px ${SANS};box-shadow:0 6px 18px rgba(0,0,0,.45);cursor:default}
.rp-spec-settings.open{display:flex}
.rp-spec-row{display:grid;grid-template-columns:64px 150px 56px;align-items:center;gap:8px}
.rp-spec-row input[type=range]{width:150px}
.rp-spec-val{font-variant-numeric:tabular-nums;text-align:right}
.rp-spop-key.rp-spop-hold.on{background:${v('tx')};border-color:${v('tx')};color:${v('tx-fg')}}
.rp-spec-empty{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;font:600 22px ${SANS};letter-spacing:.14em;text-transform:uppercase;color:${v('spec-label')}}
.rp-axis{display:flex;justify-content:space-between;font:400 15px ${MONO};color:${v('axis')};margin-top:-4px;min-height:15px}
.rp-waterfall{height:128px;position:relative;border:1px solid ${v('screen-line')};border-radius:4px;overflow:hidden;background:${v('spec-bg')}}
.rp-viewarea{flex:1;min-height:0;position:relative;display:flex;flex-direction:column}
.rp-sdrview{flex:1;min-height:0;display:flex;flex-direction:column;gap:8px}
.rp-specpane{flex:1;min-height:0;display:flex;flex-direction:column;gap:11px}
.rp-sdrview.both .rp-specpane{gap:4px;padding:2px;border:2px solid transparent;border-radius:6px}
.rp-sdrview.both .rp-specpane.sel{border-color:${v('highlight')}}
.rp-sdrview.both .rp-spectrum{min-height:0}
.rp-sdrview.both .rp-waterfall{height:56px}
/* setWaterfall(id, false): a strip that keeps the span, HOLD and SCOPE keys */
.rp-specpane.no-wf .rp-waterfall{height:36px;background:none;border-color:transparent}
.rp-specpane.no-wf .rp-waterfall>:not(.rp-spec-tools){display:none}
.rp-altview{flex:1;min-height:360px;position:relative;border:1px solid ${v('screen-line')};border-radius:4px;overflow:hidden;background:${v('spec-bg')}}
.rp-viewtabs{position:absolute;right:6px;bottom:6px;display:flex;gap:2px;padding:2px;border-radius:5px;background:${v('bar-bg')};border:1px solid ${v('well-border')};z-index:3}
.rp-viewtab{font:600 13px ${SANS};letter-spacing:.1em;text-transform:uppercase;color:${v('legend-dim')};background:none;border:0;border-radius:3px;padding:3px 9px;cursor:pointer}
.rp-viewtab.on{background:${v('bar-on')};color:${v('title')}}
.rp-bands{display:grid;grid-template-columns:repeat(11,minmax(0,1fr));gap:8px}
.rp-modes{display:grid;grid-template-columns:repeat(8,minmax(0,1fr));gap:8px}

.rp-right{display:flex;flex-direction:column;align-items:center;gap:14px}
.rp-dial-vfo{display:inline-block;margin-left:10px;padding:0 7px;border:1px solid currentColor;border-radius:4px;font:700 15px ${MONO};letter-spacing:0;color:${v('vfo-a')}}
.rp-dial-vfo[data-vfo=B]{color:${v('vfo-b')}}
.rp-dial{width:300px;height:300px;border-radius:50%;background:repeating-conic-gradient(${v('dial-a')} 0deg 2deg,${v('dial-b')} 2deg 4deg);box-shadow:0 10px 24px rgba(0,0,0,.7),inset 0 0 0 1px ${v('dial-rim')};display:flex;align-items:center;justify-content:center}
.rp-dial-inner{width:250px;height:250px;border-radius:50%;background:${v('dial-inner')};box-shadow:inset 0 2px 0 rgba(255,255,255,.07),inset 0 -6px 14px rgba(0,0,0,.45);position:relative}
.rp-dial-dimple{position:absolute;width:52px;height:52px;border-radius:50%;left:154px;top:42px;background:${v('dial-dimple')};box-shadow:inset 0 3px 6px rgba(0,0,0,.7)}
.rp-dial.dual{background:repeating-conic-gradient(from var(--rp-ring-rot,0deg),${v('dial-a')} 0deg 2deg,${v('dial-b')} 2deg 4deg)}
.rp-turn{cursor:grab;touch-action:none;user-select:none}
.rp-turn.turning{cursor:grabbing}
.rp-group.pick{cursor:pointer}
.rp-dial[data-hot=A],.rp-dial[data-hot=all]{outline:3px solid ${v('accent')};outline-offset:3px}
.rp-dial[data-hot=B] .rp-dial-knob{outline:3px solid ${v('accent')};outline-offset:3px}
.rp-dial-well{width:204px;height:204px;border-radius:50%;background:${v('dial-b')};box-shadow:inset 0 3px 8px rgba(0,0,0,.8);display:flex;align-items:center;justify-content:center}
.rp-dial-knob{width:168px;height:168px;border-radius:50%;background:repeating-conic-gradient(from var(--rp-knob-rot,0deg),${v('dial-a')} 0deg 3deg,${v('dial-b')} 3deg 6deg);display:flex;align-items:center;justify-content:center;box-shadow:0 4px 10px rgba(0,0,0,.7)}
.rp-dial-knob .rp-dial-inner{width:130px;height:130px}
.rp-dial-knob .rp-dial-dimple{width:36px;height:36px;left:78px;top:22px}
.rp-dial.sel-a{box-shadow:0 10px 24px rgba(0,0,0,.7),inset 0 0 0 1px ${v('dial-rim')},0 0 0 3px ${v('accent')},0 0 22px ${v('accent')}}
.rp-dial.sel-b .rp-dial-knob{box-shadow:0 4px 10px rgba(0,0,0,.7),0 0 0 3px ${v('accent')},0 0 22px ${v('accent')}}
.rp-dial-tags{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;width:100%}
.rp-dial-tag{display:flex;flex-direction:column;align-items:center;gap:3px;padding:8px 4px;border-radius:6px;font:600 15px ${SANS};letter-spacing:.12em;border:1px solid ${v('key-border')};background:${v('key-bg')};color:${v('legend-dim')}}
.rp-dial-tag.on{border-color:${v('accent')};background:${v('accent-bg')};color:${v('accent-soft')}}
.rp-dial-tag-vfo{font:400 14px ${MONO};letter-spacing:0}
.rp-right.dual{gap:12px}
.rp-vfokeys.dense{gap:6px}
.rp-vfokeys.dense .rp-key{font-size:15px;height:52px}
.rp-mem{width:100%;display:flex;flex-direction:column;gap:6px}
.rp-mem-head{display:flex;align-items:center;gap:8px}
.rp-mem-head .rp-legend{font-size:14px}
.rp-mem-on{font:400 13px ${MONO};color:${v('sub-label')}}
.rp-mem-keys{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:6px}
.rp-mem-keys .rp-skey[disabled]{cursor:default;opacity:.55}
.rp-clar-row{display:flex;gap:6px;align-items:center}
.rp-clar-key{height:30px;padding:0 9px}
.rp-step{display:grid;grid-template-columns:70px minmax(0,1fr) 70px;gap:10px;width:100%;align-items:center}
.rp-step .big{font-size:26px}
.rp-step .rp-sub{color:${v('lcd-fg')}}
.rp-vfokeys{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;width:100%}
.rp-clar{display:flex;align-items:center;gap:16px;margin-top:2px}
.rp-clar>div{display:flex;flex-direction:column;gap:6px}

.rp-knobs{height:176px;border-top:1px solid ${v('well-border')};padding-top:14px;display:grid;grid-template-columns:repeat(11,minmax(0,1fr));gap:8px}
.rp-knob{display:flex;flex-direction:column;align-items:center;gap:6px}
.rp-knobsvg .o{fill:${v('knob-outer')}}
.rp-knobsvg .m{fill:${v('knob-mid')};stroke:${v('knob-ring')}}
.rp-knobsvg .c{fill:${v('knob-cap')}}
.rp-knobsvg .mark{stroke:${v('knob-mark')};stroke-width:3;stroke-linecap:round}
.rp-knob.idle .rp-knobsvg .mark{opacity:.35}
.rp-knobsvg .c.ring{stroke:${v('knob-ring')}}
.rp-knobsvg .mark2{stroke:${v('knob-mark')};stroke-width:2.5;stroke-linecap:round;opacity:.8}
.rp-knobsvg g.idle .mark,.rp-knobsvg g.idle .mark2{opacity:.3}

.rp-groups{height:200px;border-top:1px solid ${v('well-border')};padding-top:12px;display:grid;grid-template-columns:minmax(0,1fr) 330px minmax(0,1fr);gap:18px}
.rp-groups.single{grid-template-columns:minmax(0,1fr) 330px}
.rp-group{min-width:0;display:flex;flex-direction:column;gap:6px;padding:8px 10px;border-radius:8px;border:2px solid ${v('well-border')}}
.rp-group.sel{border-color:${v('highlight')}}
.rp-group.sel .rp-knobsvg .m{stroke:${v('highlight')}}
.rp-knobsvg[data-hot=outer] circle.o:first-of-type,.rp-knobsvg[data-hot=all] circle.o:first-of-type{stroke:${v('accent')};stroke-width:2}
.rp-knobsvg[data-hot=inner] .c.ring{stroke:${v('accent')};stroke-width:2.5}
.rp-group-head{display:flex;align-items:center;gap:8px;height:18px}
.rp-group-name{font:700 15px ${SANS};letter-spacing:.16em;color:${v('sub-label')};white-space:nowrap}
.rp-group.sel .rp-group-name{color:${v('highlight')}}
.rp-group-shared .rp-group-name{color:${v('legend-dim')}}
.rp-chip.rp-group-tag{font-size:12px;letter-spacing:.16em;padding:1px 7px}
.rp-group-row{display:flex;gap:10px;align-items:flex-start}
.rp-group-shared .rp-group-row{justify-content:center;gap:8px}
.rp-cknob{width:92px;flex:none;display:flex;flex-direction:column;align-items:center;gap:4px}
.rp-cknob .rp-legend{font-size:14px;letter-spacing:.1em;white-space:nowrap}
.rp-cknob .rp-lcd{font-size:14px;padding:2px 6px;min-width:0}
.rp-gkeys{flex:1;min-width:0;display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:6px}
.rp-gkeys .rp-skey{height:42px}
.rp-gkeys .rp-skey[disabled]{cursor:default}

.rp-spec-pop{position:absolute;right:6px;top:6px;z-index:3}
.rp-sdrview{position:relative}
.rp-spec-away{position:absolute;inset:0;z-index:4;display:none;flex-direction:column;align-items:center;justify-content:center;gap:14px;border:1px dashed ${v('screen-line')};border-radius:4px;background:${v('spec-bg')};font:600 20px ${SANS};letter-spacing:.14em;text-transform:uppercase;color:${v('spec-label')}}
.rp-spec-away.on{display:flex}

.rp-spop{height:100vh;display:flex;flex-direction:column;position:relative}
.rp-spop-grip{position:absolute;right:0;bottom:0;width:24px;height:24px;cursor:nwse-resize;touch-action:none;z-index:5;background:linear-gradient(135deg,transparent 52%,${v('legend-dim')} 52% 58%,transparent 58% 68%,${v('legend-dim')} 68% 74%,transparent 74% 84%,${v('legend-dim')} 84% 90%,transparent 90%)}
.rp-spop-top{height:46px;flex:none;display:flex;align-items:center;gap:10px;padding:0 10px;background:${v('key-bg')};border-bottom:1px solid ${v('key-border')}}
.rp-spop-title{font:700 18px ${SANS};letter-spacing:.1em;color:${v('key-fg')};white-space:nowrap}
.rp-spop-tabs{display:flex;gap:2px;padding:2px;border-radius:5px;background:${v('bar-bg')};border:1px solid ${v('well-border')}}
.rp-spop-tab{font:600 16px ${SANS};letter-spacing:.1em;text-transform:uppercase;color:${v('legend-dim')};background:none;border:0;border-radius:3px;padding:3px 12px;cursor:pointer}
.rp-spop-tab.on{background:${v('bar-on')};color:${v('title')}}
.rp-spop-gap{flex:1}
.rp-spop-key{appearance:none;border:1px solid ${v('soft-border')};background:${v('soft-bg')};color:${v('soft-fg')};border-radius:5px;min-height:32px;padding:2px 12px;font:600 16px ${SANS};letter-spacing:.06em;cursor:pointer}
.rp-spop-key.on{background:${v('soft-on-bg')};border-color:${v('highlight')};color:${v('soft-on-fg')}}
.rp-spop-x{min-width:30px;background:${v('accent-bg')};color:${v('accent-soft')};border-color:transparent}
.rp-spop-body{flex:1;min-height:0;display:flex;flex-direction:column;gap:10px;padding:10px}
.rp-spop-holder{flex:1;min-height:0;display:flex}
/* A pane with no waterfall needs room for its trace only: about what the
   trace has in a pane with one (1 of the 1 + 1.2 below), the rest to that pane */
.rp-spop-holder:has(>.rp-pane.no-wf){flex:.45}
/* window-popout.js: one window group in a page of its own. The board's
   frame is undone; the panel boxes fill the page. */
.rp-wpop-board{flex:1;min-height:0;padding:0;border:0;border-radius:0;box-shadow:none;gap:0}
.rp-wpop-board>.rp-win-body{height:100%}
.rp-pane{flex:1;min-width:0;min-height:0;display:flex;flex-direction:column;gap:6px}
.rp-pane .rp-spectrum{flex:1;min-height:80px}
.rp-pane .rp-waterfall{height:auto;flex:1.2;min-height:60px}
.rp-pane.no-wf .rp-waterfall{display:none}
.rp-pane-grip{flex:none;height:8px;margin:-2px 0;border-radius:3px;cursor:ns-resize;touch-action:none;background:linear-gradient(${v('legend-dim')},${v('legend-dim')}) center/40px 2px no-repeat}
.rp-pane-grip:hover{background-color:${v('soft-bg')}}
.rp-pane.no-wf .rp-pane-grip{display:none}
.rp-pane.no-spec .rp-spectrum,.rp-pane.no-spec .rp-axis,.rp-pane.no-spec .rp-pane-grip{display:none}
.rp-pane.no-spec .rp-waterfall{flex:1 1 0 !important}
.rp-pane.no-wf .rp-spectrum{flex:1 1 0 !important}
.rp-jump{position:absolute;top:50%;transform:translateY(-50%);z-index:4;width:30px;height:48px;padding:0;border:1px solid ${v('screen-line')};border-radius:4px;background:${v('spec-bg')};color:${v('spec-label')};font:700 18px ${SANS};line-height:1;cursor:pointer;opacity:.6}
.rp-jump:hover,.rp-jump:focus-visible{opacity:1}
.rp-jump-dn{left:4px}
.rp-jump-up{right:4px}
/* In the pop-out each pane sits on the radio's screen, as on the board: its
   label and scale are screen colours, which on the page itself (grey in
   Silver) could not be read */
.rp-spop .rp-pane{position:relative;padding:6px 8px 8px;border-radius:6px;background:${v('screen')};border:1px solid ${v('screen-line')}}
.rp-spop .rp-axis{font-size:17px;min-height:18px}
.rp-pane>.rp-spec-settings{left:auto;right:8px;top:44px;bottom:auto;font-size:16px}
.rp-pane>.rp-spec-settings .rp-spec-row{grid-template-columns:84px 180px 70px}
.rp-pane>.rp-spec-settings .rp-spec-row input[type=range]{width:180px}
.rp-pane-bar{display:flex;align-items:center;gap:10px;min-height:32px}
.rp-pane-label{flex:1;font:400 18px ${MONO};color:${v('chip-fg')};white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.rp-pane-tools{display:flex;gap:6px}
.rp-pane-bar.has-span .rp-pane-label{flex:0 1 auto}
.rp-pane-bar.has-span .rp-pane-tools{margin-left:auto}
.rp-pane-spanwrap{position:relative;flex:none;display:inline-flex}
.rp-pane-spanwrap::after{content:'▾';position:absolute;right:4px;top:50%;transform:translateY(-50%);pointer-events:none;font:400 14px ${MONO};color:${v('chip-fg')}}
.rp-pane-span{appearance:none;-webkit-appearance:none;background:transparent;border:0;border-radius:4px;margin:0;padding:1px 20px 1px 4px;font:400 18px ${MONO};color:${v('chip-fg')};cursor:pointer}
.rp-pane-span:hover,.rp-pane-span:focus-visible{background:${v('accent-bg')};outline:none}
.rp-pane-span option{background:${v('chipbtn-bg')};color:${v('chipbtn-fg')};font:500 15px ${SANS}}
.rp-spop-span{font:500 16px ${SANS};min-height:32px;background:${v('chipbtn-bg')};color:${v('chipbtn-fg')};border:1px solid ${v('key-border')};border-radius:4px;padding:2px 6px}
`;
}
