// Startet Edge headless, meldet sich an, oeffnet den Explorer und meldet
// Konsolenfehler plus einen Screenshot. Ohne npm-Abhaengigkeiten (CDP ueber WebSocket).
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const BASE = process.argv[2] || 'http://localhost:2222';
const USER = process.argv[3] || 'admin';
const PASS = process.argv[4] || 'MatFile-Test-2026';
const OUT = process.argv[5] || 'explorer.png';
const PORT = 9333;

const EDGE = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'
].find(p => existsSync(p));

const profile = mkdtempSync(join(tmpdir(), 'mfedge-'));

const edge = spawn(EDGE, [
    '--headless=new',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${profile}`,
    '--window-size=1440,900',
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-gpu',
    'about:blank'
], { stdio: 'ignore' });

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function targets() {
    for (let i = 0; i < 40; i++) {
        try {
            const response = await fetch(`http://127.0.0.1:${PORT}/json/list`);
            const list = await response.json();
            const page = list.find(t => t.type === 'page');
            if (page) return page;
        } catch { /* Browser startet noch */ }
        await sleep(250);
    }
    throw new Error('Edge hat den Debug-Port nicht geoeffnet');
}

const page = await targets();
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise(resolve => socket.addEventListener('open', resolve));

let nextId = 1;
const pending = new Map();
const consoleErrors = [];
const failedRequests = [];
let loads = 0;

socket.addEventListener('message', event => {
    const data = JSON.parse(event.data);

    if (data.id && pending.has(data.id)) {
        pending.get(data.id)(data.result);
        pending.delete(data.id);
        return;
    }

    if (data.method === 'Runtime.consoleAPICalled' && data.params.type === 'error') {
        consoleErrors.push(data.params.args.map(a => a.value || a.description || '').join(' '));
    }
    if (data.method === 'Runtime.exceptionThrown') {
        consoleErrors.push(data.params.exceptionDetails.exception?.description
            || data.params.exceptionDetails.text);
    }
    if (data.method === 'Network.responseReceived' && data.params.response.status >= 400) {
        failedRequests.push(`${data.params.response.status} ${data.params.response.url}`);
    }
    if (data.method === 'Page.loadEventFired') {
        loads++;
    }
});

function send(method, params = {}) {
    const id = nextId++;
    socket.send(JSON.stringify({ id, method, params }));
    return new Promise(resolve => pending.set(id, resolve));
}

async function evaluate(expression) {
    const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    return result.result?.value;
}

async function navigate(url) {
    const before = loads;
    await send('Page.navigate', { url });
    for (let i = 0; i < 60 && loads === before; i++) {
        await sleep(150);
    }
    await sleep(400);
}

await send('Page.enable');
await send('Runtime.enable');
await send('Network.enable');

await navigate(`${BASE}/Login`);
await evaluate(`
    document.querySelector('#Input_Username').value = ${JSON.stringify(USER)};
    document.querySelector('#Input_Password').value = ${JSON.stringify(PASS)};
    document.querySelector('form.mf-form').submit();
    true;
`);
await sleep(1200);

await navigate(`${BASE}/`);

// Auf das erste gerenderte Listing warten
for (let i = 0; i < 40; i++) {
    const rows = await evaluate(`document.querySelectorAll('.mfx-row').length`);
    if (rows > 0) break;
    await sleep(200);
}

const report = await evaluate(`(() => {
    const rows = [...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent);
    return {
        url: location.pathname,
        roots: [...document.querySelectorAll('#mfx-roots .mfx-side-item span')].map(e => e.textContent),
        tabs: [...document.querySelectorAll('.mfx-tab .mfx-tab-title')].map(e => e.textContent),
        crumbs: [...document.querySelectorAll('.mfx-crumb')].map(e => e.textContent),
        rows,
        status: document.querySelector('#mfx-status')?.textContent?.trim(),
        message: document.querySelector('.mfx-message')?.textContent?.trim() || null
    };
})()`);

// Ordner oeffnen (Doppelklick auf die erste Zeile)
await evaluate(`(() => {
    const row = document.querySelector('.mfx-row');
    row.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    row.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    return true;
})()`);
await sleep(900);

const afterOpen = await evaluate(`(() => ({
    crumbs: [...document.querySelectorAll('.mfx-crumb')].map(e => e.textContent),
    rows: [...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent),
    status: document.querySelector('#mfx-status')?.textContent?.trim()
}))()`);

// Zurueck nach oben, damit fuer das Kontextmenue Zeilen da sind
await evaluate(`document.querySelector('#mfx-up').click(); true;`);
await sleep(900);

// Auswahl und Kontextmenue
await evaluate(`(() => {
    const row = document.querySelector('.mfx-row');
    if (!row) return false;
    row.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, button: 0 }));
    row.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, clientX: 320, clientY: 260 }));
    return true;
})()`);
await sleep(400);

const menu = await evaluate(`(() => ({
    open: !document.querySelector('#mfx-menu').hidden,
    items: [...document.querySelectorAll('#mfx-menu .mfx-menu-item span')].map(e => e.textContent),
    selected: document.querySelectorAll('.mfx-row.is-selected').length
}))()`);

// Ordner ueber die Oberflaeche anlegen und wieder loeschen
await evaluate(`document.body.click(); document.querySelector('#mfx-newfolder').click(); true;`);
await sleep(500);

const folderName = 'UI-Check-Ordner';
const dialog = await evaluate(`(() => {
    const box = document.querySelector('dialog[open]');
    if (!box) return null;
    const input = box.querySelector('input[type=text]');
    input.value = ${JSON.stringify(folderName)};
    box.querySelector('.mf-btn-primary').click();
    return true;
})()`);
await sleep(1200);

const created = await evaluate(`[...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent)`);

// Wieder aufraeumen: Zeile auswaehlen, Entf, bestaetigen
await evaluate(`(() => {
    const row = [...document.querySelectorAll('.mfx-row')]
        .find(r => r.querySelector('.mfx-name')?.textContent === ${JSON.stringify(folderName)});
    if (!row) return false;
    row.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, button: 0 }));
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }));
    return true;
})()`);
await sleep(500);

await evaluate(`(() => {
    const box = document.querySelector('dialog[open]');
    if (!box) return false;
    box.querySelector('.mf-btn-danger').click();
    return true;
})()`);
await sleep(2500);

const afterDelete = await evaluate(`[...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent)`);
const operations = {
    dialogOpened: dialog === true,
    createdFolder: created.includes(folderName),
    deletedFolder: !afterDelete.includes(folderName),
    entries: afterDelete
};

// Zurueck auf die oberste Ebene des Roots
await evaluate(`document.querySelector('.mfx-crumb').click(); true;`);
await sleep(900);

// Detailbereich einblenden (der Zustand kommt aus den gespeicherten Einstellungen)
await evaluate(`(() => {
    const toggle = document.querySelector('#mfx-details-toggle');
    if (toggle.getAttribute('aria-pressed') !== 'true') { toggle.click(); }
    return true;
})()`);
await sleep(400);

const detailsPanel = await evaluate(`(() => {
    const row = [...document.querySelectorAll('.mfx-row')]
        .find(r => /\\.(png|jpe?g|txt|md)$/i.test(r.querySelector('.mfx-name')?.textContent || ''));
    if (!row) return { found: false };
    row.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, button: 0 }));
    const panel = document.querySelector('#mfx-details');
    return {
        found: true,
        pressed: document.querySelector('#mfx-details-toggle').getAttribute('aria-pressed'),
        visible: !panel.hidden,
        name: panel.querySelector('.mfx-details-name')?.textContent || null,
        tabs: [...panel.querySelectorAll('.mfx-details-tab')].map(e => e.textContent),
        facts: [...panel.querySelectorAll('.mfx-details-body dt')].map(e => e.textContent)
    };
})()`);

// Vorschau oeffnen
await evaluate(`(() => {
    const row = [...document.querySelectorAll('.mfx-row')]
        .find(r => /\\.(png|jpe?g|txt|md)$/i.test(r.querySelector('.mfx-name')?.textContent || ''));
    if (!row) return false;
    row.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    row.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    return true;
})()`);
await sleep(1200);

const previewState = await evaluate(`(() => {
    const box = document.querySelector('.mfx-preview');
    return {
        open: !box.hidden,
        title: box.querySelector('.mfx-preview-title')?.textContent || null,
        content: box.querySelector('.mfx-preview-stage')?.firstElementChild?.tagName || null
    };
})()`);

const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT, Buffer.from(shot.data, 'base64'));

// Vorschau mit Escape schliessen
await evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); true;`);
await sleep(300);
const previewClosed = await evaluate(`document.querySelector('.mfx-preview').hidden`);

console.log(JSON.stringify({
    report, afterOpen, menu, operations,
    detailsPanel, preview: { ...previewState, closedByEscape: previewClosed },
    consoleErrors, failedRequests
}, null, 2));

socket.close();
edge.kill();
process.exit(consoleErrors.length > 0 ? 1 : 0);
