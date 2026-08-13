// Prueft den neuen Ablauf: Speicherort ueber das Pluszeichen anlegen, bearbeiten, entfernen.
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const BASE = process.argv[2] || 'http://localhost:2222';
const PASS = process.argv[3] || 'admin';
const OUT = process.argv[4] || 'rootflow.png';
const PORT = 9366;

const EDGE = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'
].find(p => existsSync(p));

const profile = mkdtempSync(join(tmpdir(), 'mfroot-'));
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

// Ohne Eintraege gibt es keinen Block - nur den Knopf fuer den ersten Speicherort.
const visibleBlocks = () => evaluate(`(() => {
    const shown = sel => { const el = document.querySelector(sel); return !!el && !el.hidden && el.getBoundingClientRect().height > 0; };
    return {
        headings: [...document.querySelectorAll('.mfx-side-group')].filter(g => !g.hidden).map(g => g.querySelector('h2').textContent),
        folders: shown('#mfx-roots-group'),
        connections: shown('#mfx-connections-group'),
        favorites: shown('#mfx-bookmarks-group'),
        firstButton: shown('#mfx-side-first')
    };
})()`);

const before = await evaluate(`(() => ({
    message: document.querySelector('.mfx-message')?.textContent?.trim() || null,
    moreButton: !!document.querySelector('#mfx-more')
}))()`);

const blocksEmpty = await visibleBlocks();

// Ueber den Knopf in der Seitenleiste den ersten Speicherort anlegen
await evaluate(`document.querySelector('#mfx-side-first').click(); true;`);
await sleep(500);

const dialog = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-root-dialog');
    if (!box) return null;
    return {
        open: true,
        title: box.querySelector('h2')?.textContent,
        tiles: [...box.querySelectorAll('.mfx-type-tile .mfx-type-label')].map(e => e.textContent),
        activeTile: box.querySelector('.mfx-type-tile.is-active .mfx-type-label')?.textContent,
        iconChoices: box.querySelectorAll('.mfx-icon-choice').length,
        sections: [...box.querySelectorAll('.mfx-dialog-section')].filter(s => !s.hidden)
            .map(s => s.querySelector('h3 span')?.textContent),
        visibleFields: [...box.querySelectorAll('.mf-field')].filter(f => !f.hidden)
            .map(f => f.querySelector('label')?.textContent).filter(Boolean)
    };
})()`);

// Zum Vergleich: bei SMB muessen Server, Freigabe und Anmeldung erscheinen
const smbFields = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-root-dialog');
    [...box.querySelectorAll('.mfx-type-tile')].find(t => t.dataset.value === 'Smb').click();
    return {
        fields: [...box.querySelectorAll('.mf-field')].filter(f => !f.hidden)
            .map(f => f.querySelector('label')?.textContent).filter(Boolean),
        sections: [...box.querySelectorAll('.mfx-dialog-section')].filter(s => !s.hidden)
            .map(s => s.querySelector('h3 span')?.textContent)
    };
})()`);

await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-root-dialog');
    [...box.querySelectorAll('.mfx-type-tile')].find(t => t.dataset.value === 'Local').click();
    const fields = [...box.querySelectorAll('.mf-field')].filter(f => !f.hidden);
    fields[0].querySelector('input').value = 'Beispiele';
    fields.find(f => f.querySelector('input.mf-mono')).querySelector('input').value = '/storage/sample';
    box.querySelector('.mf-btn-primary').click();
    return true;
})()`);
await sleep(1800);

const afterCreate = await evaluate(`(() => ({
    sidebar: [...document.querySelectorAll('#mfx-roots .mfx-side-item span')].map(e => e.textContent),
    rows: [...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent),
    flash: document.querySelector('.mfx-flash')?.textContent || null,
    plusOnFolders: !!document.querySelector('#mfx-roots-group .mfx-side-add')
}))()`);

const blocksWithFolder = await visibleBlocks();

const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT, Buffer.from(shot.data, 'base64'));

// Rechtsklick auf den Eintrag: bearbeiten
await evaluate(`(() => {
    const item = document.querySelector('#mfx-roots .mfx-side-item');
    item.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, clientX: 120, clientY: 200 }));
    return true;
})()`);
await sleep(400);

const sidebarMenu = await evaluate(`[...document.querySelectorAll('#mfx-menu .mfx-menu-item span')].map(e => e.textContent)`);

await evaluate(`document.querySelectorAll('#mfx-menu .mfx-menu-item')[0].click(); true;`);
await sleep(1200);

await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-root-dialog');
    if (!box) return false;
    box.querySelectorAll('input')[0].value = 'Beispiele neu';
    box.querySelector('.mf-btn-primary').click();
    return true;
})()`);
await sleep(1800);

const afterEdit = await evaluate(`[...document.querySelectorAll('#mfx-roots .mfx-side-item span')].map(e => e.textContent)`);

// Rechtsklick: entfernen
await evaluate(`(() => {
    const item = document.querySelector('#mfx-roots .mfx-side-item');
    item.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, clientX: 120, clientY: 200 }));
    return true;
})()`);
await sleep(400);
await evaluate(`document.querySelectorAll('#mfx-menu .mfx-menu-item')[1].click(); true;`);
await sleep(500);
await evaluate(`(() => {
    const box = document.querySelector('dialog[open]');
    if (!box) return false;
    box.querySelector('.mf-btn-danger').click();
    return true;
})()`);
await sleep(1800);

const afterDelete = await evaluate(`(() => ({
    sidebar: [...document.querySelectorAll('#mfx-roots .mfx-side-item span')].map(e => e.textContent),
    message: document.querySelector('.mfx-message')?.textContent?.trim() || null
}))()`);

const blocksAfterDelete = await visibleBlocks();

console.log(JSON.stringify({
    before, blocksEmpty, dialog, smbFields, afterCreate, blocksWithFolder,
    sidebarMenu, afterEdit, afterDelete, blocksAfterDelete, consoleErrors
}, null, 2));

socket.close();
edge.kill();
process.exit(consoleErrors.length > 0 ? 1 : 0);
