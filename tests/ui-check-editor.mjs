// Prueft Ansicht und Bearbeitung von Textdateien: Syntaxhervorhebung,
// begrenzte Breite, Speichern und der Schutz vor ungespeicherten Aenderungen.
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const BASE = process.argv[2] || 'http://localhost:2222';
const PASS = process.argv[3] || 'admin';
const ROOT = process.argv[4] || 'Demo';
const OUT = process.argv[5] || 'editor.png';
const PORT = 9382;

const EDGE = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'
].find(p => existsSync(p));

const profile = mkdtempSync(join(tmpdir(), 'mfedit-'));
const edge = spawn(EDGE, ['--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
    '--window-size=1600,1000', '--no-first-run', '--disable-gpu', 'about:blank'], { stdio: 'ignore' });

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

const send = (m, p = {}) => { const n = id++; socket.send(JSON.stringify({ id: n, method: m, params: p })); return new Promise(r => pending.set(n, r)); };
const evaluate = async e => (await send('Runtime.evaluate', { expression: e, awaitPromise: true, returnByValue: true })).result?.value;
const go = async u => { const b = loads; await send('Page.navigate', { url: u }); for (let i = 0; i < 60 && loads === b; i++) await sleep(150); await sleep(700); };

await send('Page.enable');
await send('Runtime.enable');

await go(`${BASE}/Login`);
await evaluate(`document.querySelector('#Input_Username').value='admin';
    document.querySelector('#Input_Password').value=${JSON.stringify(PASS)};
    document.querySelector('form.mf-form').submit(); true;`);
await sleep(1500);
await go(`${BASE}/`);

// In den Ordner mit den Beispieldateien wechseln
await evaluate(`(() => {
    const item = [...document.querySelectorAll('#mfx-roots .mfx-side-item')]
        .find(i => i.textContent.includes(${JSON.stringify(ROOT)}));
    if (item) item.click();
    return !!item;
})()`);
await sleep(1200);
await evaluate(`(() => {
    const row = [...document.querySelectorAll('.mfx-row')].find(r => r.dataset.path === 'Code');
    row.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    return true;
})()`);
await sleep(1200);

const folder = await evaluate(`[...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent)`);

/** Datei per Doppelklick oeffnen. */
const open = async name => {
    await evaluate(`(() => {
        const row = [...document.querySelectorAll('.mfx-row')].find(r => r.dataset.path.endsWith(${JSON.stringify(name)}));
        row.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
        return true;
    })()`);
    await sleep(1300);
};

const editorState = () => evaluate(`(() => {
    const box = document.querySelector('.mfe');
    if (!box) return null;
    const doc = document.querySelector('.mfx-preview-doc');
    const tokens = {};
    box.querySelectorAll('.mfe-highlight span').forEach(s => {
        const type = s.className.replace('tok-', '');
        tokens[type] = (tokens[type] || 0) + 1;
    });
    return {
        language: box.dataset.language,
        docWidth: Math.round(doc.getBoundingClientRect().width),
        viewportWidth: window.innerWidth,
        lines: box.querySelector('.mfe-numbers').textContent.split('\\n').length,
        readOnly: box.querySelector('.mfe-input').readOnly,
        tokens,
        title: document.querySelector('.mfx-preview-title')?.textContent,
        editVisible: !document.querySelector('.mfx-preview-bar .mfx-btn').hidden,
        buttons: [...document.querySelectorAll('.mfx-preview-bar .mfx-btn')].filter(b => !b.hidden).length
    };
})()`);

await open('config.json');
const json = await editorState();
const shot1 = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT, Buffer.from(shot1.data, 'base64'));

// Bearbeiten, tippen, speichern
await evaluate(`(() => {
    const buttons = [...document.querySelectorAll('.mfx-preview-bar .mfx-btn')];
    buttons[0].click();
    return true;
})()`);
await sleep(400);

const editing = await evaluate(`(() => {
    const input = document.querySelector('.mfe-input');
    return { readOnly: input.readOnly, focused: document.activeElement === input };
})()`);

await evaluate(`(() => {
    const input = document.querySelector('.mfe-input');
    input.value = input.value.replace('2222', '3333');
    input.dispatchEvent(new Event('input', { bubbles: true }));
    return true;
})()`);
await sleep(300);

const dirty = await evaluate(`document.querySelector('.mfx-preview-title')?.textContent`);

await evaluate(`(() => {
    const buttons = [...document.querySelectorAll('.mfx-preview-bar .mfx-btn')].filter(b => !b.hidden);
    buttons.find(b => b.title.includes('peichern') || b.title.includes('Save')).click();
    return true;
})()`);
await sleep(1500);

const afterSave = await evaluate(`(() => ({
    title: document.querySelector('.mfx-preview-title')?.textContent,
    flash: document.querySelector('.mfx-flash')?.textContent || null,
    value: document.querySelector('.mfe-input')?.value
}))()`);

// Schliessen und erneut oeffnen: der neue Inhalt steht in der Datei
await evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); true;`);
await sleep(900);
await open('config.json');

const reopened = await evaluate(`(() => ({
    value: document.querySelector('.mfe-input')?.value,
    readOnly: document.querySelector('.mfe-input')?.readOnly
}))()`);

await evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); true;`);
await sleep(700);

// Markdown und XML bekommen eigene Farben
await open('liesmich.md');
const markdown = await editorState();
const shot2 = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT.replace(/\.png$/, '-md.png'), Buffer.from(shot2.data, 'base64'));
await evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); true;`);
await sleep(700);

await open('daten.xml');
const xml = await editorState();
await evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); true;`);
await sleep(700);

console.log(JSON.stringify({ folder, json, editing, dirty, afterSave, reopened, markdown, xml, consoleErrors }, null, 2));

socket.close();
edge.kill();
process.exit(consoleErrors.length > 0 ? 1 : 0);
