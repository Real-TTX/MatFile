// Prueft den Explorer auf schmalen Schirmen: laeuft nichts ueber den Rand,
// bleibt die Dateiliste erreichbar, kommt man aus den Details wieder zurueck?
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const BASE = process.argv[2] || 'http://localhost:2222';
const PASS = process.argv[3] || 'admin';
const OUT = process.argv[4] || 'mobile.png';
const PORT = 9390;

const EDGE = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'
].find(p => existsSync(p));

const profile = mkdtempSync(join(tmpdir(), 'mfmobile-'));
const edge = spawn(EDGE, ['--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
    '--window-size=1440,1000', '--no-first-run', '--disable-gpu', 'about:blank'], { stdio: 'ignore' });

const sleep = ms => new Promise(r => setTimeout(r, ms));

let page = null;
for (let i = 0; i < 40 && !page; i++) {
    try {
        const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
        page = list.find(t => t.type === 'page');
    } catch { }
    if (!page) await sleep(250);
}

const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise(r => socket.addEventListener('open', r));

let id = 1;
const pending = new Map();
const consoleErrors = [];
let loads = 0;

socket.addEventListener('message', e => {
    const d = JSON.parse(e.data);
    if (d.id && pending.has(d.id)) { pending.get(d.id)(d.result); pending.delete(d.id); }
    if (d.method === 'Page.loadEventFired') loads++;
    if (d.method === 'Runtime.consoleAPICalled' && d.params.type === 'error') {
        consoleErrors.push(d.params.args.map(a => a.value || a.description || '').join(' '));
    }
    if (d.method === 'Runtime.exceptionThrown') {
        consoleErrors.push(d.params.exceptionDetails.exception?.description || d.params.exceptionDetails.text);
    }
});

const send = (method, params = {}) => {
    const n = id++;
    socket.send(JSON.stringify({ id: n, method, params }));
    return new Promise(r => pending.set(n, r));
};
const evaluate = async expr => (await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result?.value;
const go = async url => { const b = loads; await send('Page.navigate', { url }); for (let i = 0; i < 60 && loads === b; i++) await sleep(150); await sleep(700); };

await send('Page.enable');
await send('Runtime.enable');

await go(`${BASE}/Login`);
await evaluate(`document.querySelector('#Input_Username').value='admin';
    document.querySelector('#Input_Password').value=${JSON.stringify(PASS)};
    document.querySelector('form.mf-form').submit(); true;`);
await sleep(1500);

// Ein Element steht nur dann im Weg, wenn es aus der Seite herausragt -
// innerhalb einer schiebbaren Leiste ist Ueberlaenge gewollt.
const messung = `(() => {
    const doc = document.documentElement;
    const listing = document.querySelector('#mfx-listing');
    const details = document.querySelector('#mfx-details');
    const sichtbar = el => el && !el.hidden && getComputedStyle(el).display !== 'none'
        && el.getBoundingClientRect().width > 0;

    return {
        breite: innerWidth,
        seitenScrollbar: doc.scrollWidth > doc.clientWidth + 1,
        listeSichtbar: sichtbar(listing),
        detailsSichtbar: sichtbar(details),
        zeilen: document.querySelectorAll('.mfx-row').length,
        menueleisteSchiebt: (() => {
            const bar = document.querySelector('#mfx-menubar');
            return bar.scrollWidth > bar.clientWidth ? getComputedStyle(bar).overflowX === 'auto' : true;
        })(),
        werkzeugleisteSchiebt: (() => {
            const bar = document.querySelector('.mfx-toolbar');
            return bar.scrollWidth > bar.clientWidth ? getComputedStyle(bar).overflowX === 'auto' : true;
        })(),
        umbruchInDerMenueleiste: [...document.querySelectorAll('.mfx-menubar-item')]
            .some(b => b.getBoundingClientRect().height > 30)
    };
})()`;

const breiten = {};

for (const [breite, hoehe] of [[1440, 900], [820, 1180], [768, 1024], [480, 900], [390, 844]]) {
    await send('Emulation.setDeviceMetricsOverride', { width: breite, height: hoehe, deviceScaleFactor: 1, mobile: breite < 800 });
    await go(`${BASE}/`);
    await sleep(500);
    breiten[breite] = await evaluate(messung);
}

// Auf dem Telefon: Eintrag waehlen, Details, zurueck zur Liste
const zustand = `(() => {
    const listing = document.querySelector('#mfx-listing');
    const details = document.querySelector('#mfx-details');
    const sichtbar = el => el && !el.hidden && getComputedStyle(el).display !== 'none'
        && el.getBoundingClientRect().width > 0;
    const zurueck = details.querySelector('.mfx-details-back');
    return {
        liste: sichtbar(listing),
        details: sichtbar(details),
        zurueckSichtbar: zurueck ? getComputedStyle(zurueck).display !== 'none' : false,
        name: details.querySelector('.mfx-details-name')?.textContent || null
    };
})()`;

const ablauf = {};
ablauf.start = await evaluate(zustand);

await evaluate(`document.querySelector('.mfx-row').dispatchEvent(new MouseEvent('mousedown', { bubbles: true, button: 0 })); true;`);
await sleep(700);
ablauf.gewaehlt = await evaluate(zustand);

const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT, Buffer.from(shot.data, 'base64'));

await evaluate(`document.querySelector('.mfx-details-back').click(); true;`);
await sleep(700);
ablauf.zurueck = await evaluate(zustand);

// Seitenleiste als Überlagerung
await evaluate(`document.querySelector('#mfx-sidebar-toggle').click(); true;`);
await sleep(500);
ablauf.seitenleiste = await evaluate(`(() => {
    const s = document.querySelector('#mfx-sidebar');
    return {
        offen: s.classList.contains('is-open'),
        links: Math.round(s.getBoundingClientRect().left),
        backdrop: !document.querySelector('#mfx-backdrop').hidden
    };
})()`);

const shotSide = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT.replace(/\.png$/, '-sidebar.png'), Buffer.from(shotSide.data, 'base64'));

console.log(JSON.stringify({ breiten, ablauf, consoleErrors }, null, 2));

const fehler = [];
for (const [breite, werte] of Object.entries(breiten)) {
    if (werte.seitenScrollbar) fehler.push(`${breite}: Seite scrollt waagerecht`);
    if (!werte.listeSichtbar) fehler.push(`${breite}: Dateiliste nicht sichtbar`);
    if (werte.umbruchInDerMenueleiste) fehler.push(`${breite}: Menueleiste bricht um`);
    if (!werte.menueleisteSchiebt) fehler.push(`${breite}: Menueleiste laeuft ueber`);
    if (!werte.werkzeugleisteSchiebt) fehler.push(`${breite}: Werkzeugleiste laeuft ueber`);
}
if (!ablauf.gewaehlt?.details || ablauf.gewaehlt?.liste) fehler.push('Details ersetzen die Liste nicht');
if (!ablauf.gewaehlt?.zurueckSichtbar) fehler.push('Kein Weg zurueck aus den Details');
if (!ablauf.zurueck?.liste) fehler.push('Zurueck fuehrt nicht zur Liste');
if (!ablauf.seitenleiste?.offen || ablauf.seitenleiste?.links !== 0) fehler.push('Seitenleiste oeffnet nicht');

if (fehler.length > 0) {
    console.error('FEHLER: ' + fehler.join(' | '));
}

socket.close();
edge.kill();
process.exit(fehler.length > 0 || consoleErrors.length > 0 ? 1 : 0);
