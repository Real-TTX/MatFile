// Prueft die rekursive Suche: Fortschritt, eigenes Fenster, Treffer aus Unterordnern,
// Inhaltssuche und der Sprung in den Ordner des Treffers.
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const BASE = process.argv[2] || 'http://localhost:2222';
const PASS = process.argv[3] || 'admin';
const ROOT = process.argv[4] || 'Suche';
const OUT = process.argv[5] || 'search.png';
const PORT = 9368;

const EDGE = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'
].find(p => existsSync(p));

const profile = mkdtempSync(join(tmpdir(), 'mfsearch-'));
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
await go(`${BASE}/`);

// Zum Speicherort der Testdaten wechseln
await evaluate(`(() => {
    const item = [...document.querySelectorAll('#mfx-roots .mfx-side-item')]
        .find(i => i.textContent.includes(${JSON.stringify(ROOT)}));
    if (item) item.click();
    return !!item;
})()`);
await sleep(1200);

const toolbar = await evaluate(`(() => ({
    placeholder: document.querySelector('#mfx-search')?.placeholder,
    goButton: !!document.querySelector('#mfx-search-go'),
    version: document.querySelector('#mfx-version')?.textContent?.replace(/\\s+/g, ' ').trim() || null,
    rows: [...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent)
}))()`);

// Tippen filtert nur den aktuellen Ordner
await evaluate(`(() => {
    const box = document.querySelector('#mfx-search');
    box.value = 'zebra';
    box.dispatchEvent(new Event('input', { bubbles: true }));
    return true;
})()`);
await sleep(300);

const filtered = await evaluate(`[...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent)`);

// Enter startet die Suche ueber alle Unterordner
await evaluate(`(() => {
    const box = document.querySelector('#mfx-search');
    box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    return true;
})()`);
await sleep(400);

const running = await evaluate(`(() => {
    const win = document.querySelector('.mfx-window');
    if (!win) return null;
    return {
        open: !win.hidden,
        title: win.querySelector('.mfx-window-title')?.textContent,
        sub: win.querySelector('.mfx-window-sub')?.textContent,
        options: [...win.querySelectorAll('.mfx-window-option')].map(o => o.textContent.trim()),
        stopVisible: !win.querySelectorAll('.mfx-window-foot .mf-btn')[0].hidden
    };
})()`);

// Auf das Ende der Suche warten
let done = null;
for (let i = 0; i < 40; i++) {
    done = await evaluate(`(() => {
        const win = document.querySelector('.mfx-window');
        const foot = win.querySelector('.mfx-window-foot');
        return {
            stats: win.querySelector('.mfx-search-stats')?.textContent,
            count: win.querySelector('.mfx-search-count')?.textContent,
            percent: win.querySelector('.mfx-progress-fill')?.style.width,
            finished: foot.querySelectorAll('.mf-btn')[0].hidden,
            hits: [...win.querySelectorAll('.mfx-hit')].map(h => ({
                name: h.querySelector('.mfx-hit-name')?.textContent,
                path: h.querySelector('.mfx-hit-path')?.textContent,
                snippet: h.querySelector('.mfx-hit-snippet')?.textContent || null
            }))
        };
    })()`);
    if (done && done.finished) break;
    await sleep(250);
}

const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT, Buffer.from(shot.data, 'base64'));

// Inhaltssuche einschalten - die Suche laeuft neu
await evaluate(`(() => {
    document.querySelector('.mfx-window .mfx-window-option input').click();
    return true;
})()`);
await sleep(400);

let withContent = null;
for (let i = 0; i < 40; i++) {
    withContent = await evaluate(`(() => {
        const win = document.querySelector('.mfx-window');
        return {
            finished: win.querySelector('.mfx-window-foot .mf-btn').hidden,
            count: win.querySelector('.mfx-search-count')?.textContent,
            hits: [...win.querySelectorAll('.mfx-hit')].map(h => ({
                name: h.querySelector('.mfx-hit-name')?.textContent,
                snippet: h.querySelector('.mfx-hit-snippet')?.textContent || null,
                marked: h.querySelector('.mfx-hit-snippet mark')?.textContent || null
            }))
        };
    })()`);
    if (withContent && withContent.finished) break;
    await sleep(250);
}

const contentShot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT.replace(/\.png$/, '-content.png'), Buffer.from(contentShot.data, 'base64'));

// Im Ordner anzeigen: der Treffer mit Fundstelle
await evaluate(`(() => {
    const hit = [...document.querySelectorAll('.mfx-window .mfx-hit')]
        .find(h => h.querySelector('.mfx-hit-snippet'));
    (hit || document.querySelector('.mfx-window .mfx-hit')).querySelector('.mfx-btn').click();
    return true;
})()`);
await sleep(1500);

const revealed = await evaluate(`(() => ({
    breadcrumb: [...document.querySelectorAll('#mfx-breadcrumb .mfx-crumb')].map(c => c.textContent),
    selected: [...document.querySelectorAll('.mfx-row.is-selected .mfx-name')].map(e => e.textContent),
    rows: [...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent),
    windowStillOpen: !document.querySelector('.mfx-window').hidden
}))()`);

console.log(JSON.stringify({ toolbar, filtered, running, done, withContent, revealed, consoleErrors }, null, 2));

socket.close();
edge.kill();
process.exit(consoleErrors.length > 0 ? 1 : 0);
