// Prueft die erweiterte Suche als eigenen Tab: das Pluszeichen oeffnet sie,
// Fortschritt und Treffer erscheinen im Tab, der Sprung in den Ordner nutzt den Ordnertab.
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
    rows: [...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent),
    addTitle: document.querySelector('.mfx-tab-add')?.title,
    tabs: [...document.querySelectorAll('.mfx-tab .mfx-tab-title')].map(e => e.textContent)
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

// Das Pluszeichen oeffnet die erweiterte Suche in einem neuen Tab
await evaluate(`document.querySelector('.mfx-tab-add').click(); true;`);
await sleep(700);

const searchTab = await evaluate(`(() => {
    const view = document.querySelector('.mfx-search-view');
    return {
        opened: !!view,
        tabs: [...document.querySelectorAll('.mfx-tab .mfx-tab-title')].map(e => e.textContent),
        activeTab: document.querySelector('.mfx-tab.is-active .mfx-tab-title')?.textContent,
        breadcrumb: document.querySelector('#mfx-breadcrumb')?.textContent?.trim(),
        fields: [...document.querySelectorAll('.mfx-search-field > span')].map(e => e.textContent),
        options: [...document.querySelectorAll('.mfx-search-options .mf-check span')].map(e => e.textContent),
        scopeOptions: [...document.querySelectorAll('.mfx-search-field select option')].map(o => o.textContent),
        buttons: [...document.querySelectorAll('.mfx-search-actions .mf-btn')].map(b => b.textContent.trim()),
        stats: document.querySelector('.mfx-search-stats')?.textContent,
        status: document.querySelector('#mfx-status')?.textContent?.trim(),
        pinButton: !!document.querySelector('.mfx-tab.is-active .mfx-tab-pin'),
        upDisabled: document.querySelector('#mfx-up')?.disabled,
        uploadDisabled: document.querySelector('#mfx-upload')?.disabled
    };
})()`);

// Suchbegriff eintragen und suchen
await evaluate(`(() => {
    const term = document.querySelector('.mfx-search-term');
    term.value = 'zebra';
    term.dispatchEvent(new Event('input', { bubbles: true }));
    document.querySelector('.mfx-search-actions .mf-btn-primary').click();
    return true;
})()`);

let done = null;
for (let i = 0; i < 40; i++) {
    done = await evaluate(`(() => {
        const view = document.querySelector('.mfx-search-view');
        return {
            stats: view.querySelector('.mfx-search-stats')?.textContent,
            percent: view.querySelector('.mfx-progress-fill')?.style.width,
            status: document.querySelector('#mfx-status')?.textContent?.trim(),
            tabTitle: document.querySelector('.mfx-tab.is-active .mfx-tab-title')?.textContent,
            stopDisabled: [...view.querySelectorAll('.mfx-search-actions .mf-btn')][1].disabled,
            hits: [...view.querySelectorAll('.mfx-hit')].map(h => ({
                name: h.querySelector('.mfx-hit-name')?.textContent,
                path: h.querySelector('.mfx-hit-path')?.textContent,
                snippet: h.querySelector('.mfx-hit-snippet')?.textContent || null
            }))
        };
    })()`);
    if (done && done.stopDisabled) break;
    await sleep(250);
}

const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT, Buffer.from(shot.data, 'base64'));

// Inhaltssuche einschalten und erneut suchen
await evaluate(`(() => {
    document.querySelector('.mfx-search-options .mf-check input').click();
    document.querySelector('.mfx-search-actions .mf-btn-primary').click();
    return true;
})()`);

let withContent = null;
for (let i = 0; i < 40; i++) {
    withContent = await evaluate(`(() => {
        const view = document.querySelector('.mfx-search-view');
        return {
            finished: [...view.querySelectorAll('.mfx-search-actions .mf-btn')][1].disabled,
            status: document.querySelector('#mfx-status')?.textContent?.trim(),
            hits: [...view.querySelectorAll('.mfx-hit')].map(h => ({
                name: h.querySelector('.mfx-hit-name')?.textContent,
                snippet: h.querySelector('.mfx-hit-snippet')?.textContent || null,
                marked: h.querySelector('.mfx-hit-snippet mark')?.textContent || null
            }))
        };
    })()`);
    if (withContent && withContent.finished && withContent.hits.length > 0) break;
    await sleep(250);
}

const contentShot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT.replace(/\.png$/, '-content.png'), Buffer.from(contentShot.data, 'base64'));

// Erst ohne Treffer, dann mit Inhaltssuche: der Hinweis "Keine Treffer" darf
// nicht ueber den neuen Treffern stehen bleiben.
await evaluate(`(() => {
    const term = document.querySelector('.mfx-search-term');
    term.value = 'Suchbegriff';
    term.dispatchEvent(new Event('input', { bubbles: true }));

    // Inhaltssuche wirklich abschalten - ein Klick meldet die Aenderung.
    const content = document.querySelector('.mfx-search-options .mf-check input');
    if (content.checked) { content.click(); }

    document.querySelector('.mfx-search-actions .mf-btn-primary').click();
    return true;
})()`);

let empty = null;
for (let i = 0; i < 40; i++) {
    empty = await evaluate(`(() => {
        const view = document.querySelector('.mfx-search-view');
        return {
            finished: [...view.querySelectorAll('.mfx-search-actions .mf-btn')][1].disabled,
            note: view.querySelector('.mfx-search-empty')?.textContent || null,
            hits: view.querySelectorAll('.mfx-hit').length
        };
    })()`);
    if (empty && empty.finished) break;
    await sleep(250);
}

await evaluate(`(() => {
    document.querySelector('.mfx-search-options .mf-check input').click();
    document.querySelector('.mfx-search-actions .mf-btn-primary').click();
    return true;
})()`);

let afterContent = null;
for (let i = 0; i < 40; i++) {
    afterContent = await evaluate(`(() => {
        const view = document.querySelector('.mfx-search-view');
        return {
            finished: [...view.querySelectorAll('.mfx-search-actions .mf-btn')][1].disabled,
            note: view.querySelector('.mfx-search-empty')?.textContent || null,
            hits: view.querySelectorAll('.mfx-hit').length,
            status: document.querySelector('#mfx-status')?.textContent?.trim()
        };
    })()`);
    if (afterContent && afterContent.finished && afterContent.hits > 0) break;
    await sleep(250);
}

// Zurueck auf die Ausgangssuche fuer den naechsten Schritt
await evaluate(`(() => {
    const term = document.querySelector('.mfx-search-term');
    term.value = 'zebra';
    term.dispatchEvent(new Event('input', { bubbles: true }));
    document.querySelector('.mfx-search-actions .mf-btn-primary').click();
    return true;
})()`);
await sleep(1500);

// Im Ordner anzeigen: springt im Ordnertab, der Suchtab bleibt offen
await evaluate(`(() => {
    const hit = [...document.querySelectorAll('.mfx-hit')].find(h => h.querySelector('.mfx-hit-snippet'))
        || document.querySelector('.mfx-hit');
    hit.querySelector('.mfx-btn').click();
    return true;
})()`);
await sleep(1600);

const revealed = await evaluate(`(() => ({
    breadcrumb: [...document.querySelectorAll('#mfx-breadcrumb .mfx-crumb')].map(c => c.textContent),
    selected: [...document.querySelectorAll('.mfx-row.is-selected .mfx-name')].map(e => e.textContent),
    rows: [...document.querySelectorAll('.mfx-row .mfx-name')].map(e => e.textContent),
    tabs: [...document.querySelectorAll('.mfx-tab .mfx-tab-title')].map(e => e.textContent),
    activeTab: document.querySelector('.mfx-tab.is-active .mfx-tab-title')?.textContent,
    searchTabStillThere: [...document.querySelectorAll('.mfx-tab .mfx-tab-title')].some(e => e.textContent.startsWith('Suche'))
}))()`);

// Zurueck in den Suchtab: die Treffer stehen noch
await evaluate(`(() => {
    const tab = [...document.querySelectorAll('.mfx-tab')]
        .find(t => t.querySelector('.mfx-tab-title').textContent.startsWith('Suche'));
    tab.click();
    return true;
})()`);
await sleep(900);

const backInSearch = await evaluate(`(() => {
    const view = document.querySelector('.mfx-search-view');
    return {
        view: !!view,
        term: view?.querySelector('.mfx-search-term')?.value,
        hits: view ? view.querySelectorAll('.mfx-hit').length : 0,
        status: document.querySelector('#mfx-status')?.textContent?.trim(),
        toolbarField: document.querySelector('#mfx-search')?.value
    };
})()`);

console.log(JSON.stringify({
    toolbar, filtered, searchTab, done, withContent, empty, afterContent,
    revealed, backInSearch, consoleErrors
}, null, 2));

socket.close();
edge.kill();
process.exit(consoleErrors.length > 0 ? 1 : 0);
