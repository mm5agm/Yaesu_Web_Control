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
.rp-brand-model{font:600 23px ${SANS};letter-spacing:.1em;color:${v('accent')}}
.rp-lamps{display:flex;align-items:center;gap:18px}
.rp-lamps>span{display:flex;align-items:center;gap:7px}
.rp-top-right{display:flex;align-items:center;gap:14px}

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
.rp-keys{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px 12px;margin-top:4px}

.rp-screen-well{border-radius:12px;background:${v('well')};padding:10px;border:1px solid ${v('well-border')};box-shadow:inset 0 2px 8px rgba(0,0,0,.9);min-width:0;display:flex}
.rp-screen{flex:1;min-width:0;border-radius:6px;background:${v('screen')};border:1px solid ${v('screen-line')};padding:14px 18px;display:flex;flex-direction:column;gap:11px}
.rp-vfos{display:flex;gap:20px;align-items:stretch;height:196px}
.rp-main{flex:1;min-width:0;display:flex;flex-direction:column;justify-content:space-between;border-right:1px solid ${v('screen-line')};padding-right:16px}
.rp-main.single{border-right:0;padding-right:0}
.rp-main.sel,.rp-subvfo.sel{outline:2px solid ${v('highlight')};outline-offset:6px;border-radius:3px}
.rp-vfos.pick .rp-main,.rp-vfos.pick .rp-subvfo{cursor:pointer}
.rp-vfo-head{display:flex;align-items:center;gap:8px}
.rp-vfo-name{font:700 19px ${SANS};letter-spacing:.16em;color:${v('accent')}}
.rp-freq{display:flex;align-items:baseline;font-family:${MONO};color:${v('freq')};line-height:1;white-space:nowrap}
.rp-freq .big{font-size:112px;letter-spacing:-.02em}
.rp-freq .small{font-size:64px;color:${v('freq-small')}}
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
.rp-cursor{position:absolute;top:0;bottom:0;left:50%;width:1px;background:${v('cursor')}}
.rp-spot{position:absolute;transform:translateX(-50%);font:600 16px ${SANS};letter-spacing:.06em;color:${v('spot-fg')};background:${v('spot-bg')};border-radius:3px;padding:2px 7px;white-space:nowrap}
.rp-spec-label{position:absolute;left:10px;top:8px;font:400 15px ${MONO};color:${v('spec-label')}}
.rp-spec-empty{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;font:600 22px ${SANS};letter-spacing:.14em;text-transform:uppercase;color:${v('spec-label')}}
.rp-axis{display:flex;justify-content:space-between;font:400 15px ${MONO};color:${v('axis')};margin-top:-4px;min-height:15px}
.rp-waterfall{height:128px;position:relative;border:1px solid ${v('screen-line')};border-radius:4px;overflow:hidden;background:${v('spec-bg')}}
.rp-viewarea{flex:1;min-height:0;position:relative;display:flex;flex-direction:column}
.rp-sdrview{flex:1;min-height:0;display:flex;flex-direction:column;gap:11px}
.rp-altview{flex:1;min-height:360px;position:relative;border:1px solid ${v('screen-line')};border-radius:4px;overflow:hidden;background:${v('spec-bg')}}
.rp-viewtabs{position:absolute;right:6px;bottom:6px;display:flex;gap:2px;padding:2px;border-radius:5px;background:${v('bar-bg')};border:1px solid ${v('well-border')};z-index:3}
.rp-viewtab{font:600 13px ${SANS};letter-spacing:.1em;text-transform:uppercase;color:${v('legend-dim')};background:none;border:0;border-radius:3px;padding:3px 9px;cursor:pointer}
.rp-viewtab.on{background:${v('bar-on')};color:${v('title')}}
.rp-bands{display:grid;grid-template-columns:repeat(11,minmax(0,1fr));gap:8px}
.rp-modes{display:grid;grid-template-columns:repeat(8,minmax(0,1fr));gap:8px}

.rp-right{display:flex;flex-direction:column;align-items:center;gap:14px}
.rp-dial{width:300px;height:300px;border-radius:50%;background:repeating-conic-gradient(${v('dial-a')} 0deg 2deg,${v('dial-b')} 2deg 4deg);box-shadow:0 10px 24px rgba(0,0,0,.7),inset 0 0 0 1px ${v('dial-rim')};display:flex;align-items:center;justify-content:center}
.rp-dial-inner{width:250px;height:250px;border-radius:50%;background:${v('dial-inner')};box-shadow:inset 0 2px 0 rgba(255,255,255,.07),inset 0 -6px 14px rgba(0,0,0,.45);position:relative}
.rp-dial-dimple{position:absolute;width:52px;height:52px;border-radius:50%;left:154px;top:42px;background:${v('dial-dimple')};box-shadow:inset 0 3px 6px rgba(0,0,0,.7)}
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
`;
}
