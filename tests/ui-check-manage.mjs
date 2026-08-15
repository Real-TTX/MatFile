// Prueft die Verwaltungsdialoge: Benutzer, Rollen, Speicherorte.
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const BASE = process.argv[2] || 'http://localhost:2222';
const PASS = process.argv[3] || 'admin';
const OUT = process.argv[4] || 'manage.png';
const PORT = 9370;

const EDGE = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'
].find(p => existsSync(p));

const profile = mkdtempSync(join(tmpdir(), 'mfmanage-'));
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

// Der Administrationsbereich ist ein eigener Punkt in der Menueleiste.
const menu = await evaluate(`(() => {
    const bar = [...document.querySelectorAll('.mfx-menubar-item')].map(b => b.textContent.trim());
    document.querySelector('.mfx-menubar-item[data-menu="admin"]').click();
    return bar;
})()`);
await sleep(1200);

const sections = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-admin');
    return {
        open: !!box,
        head: box?.querySelector('.mfx-dialog-head h2')?.textContent,
        items: [...box.querySelectorAll('.mfx-admin-nav .mfx-admin-item')].map(b => b.textContent.trim()),
        active: box.querySelector('.mfx-admin-item.is-active')?.textContent.trim()
    };
})()`);

const users = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    if (!box) return null;
    return {
        title: box.querySelector('.mfx-admin-title')?.textContent,
        columns: [...box.querySelectorAll('.mf-table thead th')].map(e => e.textContent),
        rows: [...box.querySelectorAll('.mf-table tbody tr')].map(r => [...r.querySelectorAll('td')].map(c => c.textContent.trim())),
        pager: box.querySelector('.mfx-manage-pager')?.textContent?.trim(),
        footButtons: [...box.querySelectorAll('.mfx-manage-foot .mf-btn')].map(b => b.textContent.trim())
    };
})()`);

const shot1 = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT, Buffer.from(shot1.data, 'base64'));

// Neuen Benutzer anlegen
await evaluate(`document.querySelector('dialog.mfx-manage .mfx-manage-foot .mf-btn-primary').click(); true;`);
await sleep(600);

const formView = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    return {
        title: box.querySelector('.mfx-admin-title')?.textContent,
        fields: [...box.querySelectorAll('.mf-field label')].map(e => e.textContent),
        roleChecks: [...box.querySelectorAll('.mfx-manage-box .mf-check span')].map(e => e.textContent),
        checks: [...box.querySelectorAll('.mf-checklist .mf-check span')].map(e => e.textContent),
        footButtons: [...box.querySelectorAll('.mfx-manage-foot .mf-btn')].map(b => b.textContent.trim())
    };
})()`);

await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    const inputs = box.querySelectorAll('.mf-field input');
    inputs[0].value = 'dialogtester';
    inputs[1].value = 'Dialog Tester';
    inputs[3].value = 'Test-2026-abc';
    [...box.querySelectorAll('.mfx-manage-box .mf-check')].find(c => c.textContent.includes('User'))
        .querySelector('input').checked = true;
    box.querySelector('.mfx-manage-foot .mf-btn-primary').click();
    return true;
})()`);
await sleep(1500);

const afterCreate = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    return {
        title: box.querySelector('.mfx-admin-title')?.textContent,
        rows: [...box.querySelectorAll('.mf-table tbody tr')].map(r => r.querySelector('td')?.textContent.trim()),
        flash: document.querySelector('.mfx-flash')?.textContent || null
    };
})()`);

const shot2 = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT.replace(/\.png$/, '-users.png'), Buffer.from(shot2.data, 'base64'));

// Benutzer wieder loeschen: Zeile oeffnen, dann Loeschen bestaetigen
await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    const row = [...box.querySelectorAll('.mf-table tbody tr')].find(r => r.textContent.includes('dialogtester'));
    row.querySelector('.mf-btn-danger').click();
    return true;
})()`);
await sleep(600);
await evaluate(`(() => {
    const ask = [...document.querySelectorAll('dialog[open]')].pop();
    ask.querySelector('.mf-btn-danger').click();
    return true;
})()`);
await sleep(1500);

const afterDelete = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    return [...box.querySelectorAll('.mf-table tbody tr')].map(r => r.querySelector('td')?.textContent.trim());
})()`);

// Im selben Fenster auf Rollen wechseln
await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-admin');
    [...box.querySelectorAll('.mfx-admin-item')]
        .find(b => /^(Rollen|Roles)/.test(b.textContent.trim())).click();
    return true;
})()`);
await sleep(1200);

const roles = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    return {
        title: box.querySelector('.mfx-admin-title')?.textContent,
        rows: [...box.querySelectorAll('.mf-table tbody tr')].map(r => [...r.querySelectorAll('td')].map(c => c.textContent.trim())),
        systemBadges: [...box.querySelectorAll('.mf-table .mf-badge')].map(b => b.textContent)
    };
})()`);

// Systemrolle oeffnen: Name gesperrt, Hinweis sichtbar
await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    [...box.querySelectorAll('.mf-table tbody tr')].find(r => r.textContent.includes('Administrator')).click();
    return true;
})()`);
await sleep(900);

const systemRole = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    return {
        title: box.querySelector('.mfx-admin-title')?.textContent,
        hint: box.querySelector('.mfx-manage-note')?.textContent,
        nameDisabled: box.querySelector('.mf-field input')?.disabled,
        rightsDisabled: [...box.querySelectorAll('.mfx-manage-box input')].every(i => i.disabled),
        checkedRights: [...box.querySelectorAll('.mfx-manage-box .mf-check')].filter(c => c.querySelector('input').checked).length,
        footButtons: [...box.querySelectorAll('.mfx-manage-foot .mf-btn')].map(b => b.textContent.trim())
    };
})()`);

// Zurueck, Dialog schliessen, Speicherorte oeffnen
await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    [...box.querySelectorAll('.mfx-manage-foot .mf-btn')][1].click();
    return true;
})()`);
await sleep(800);
await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-admin');
    [...box.querySelectorAll('.mfx-admin-item')]
        .find(b => /^(Speicherorte|Storage locations)/.test(b.textContent.trim())).click();
    return true;
})()`);
await sleep(1200);

const locations = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    if (!box) return null;
    return {
        title: box.querySelector('.mfx-admin-title')?.textContent,
        columns: [...box.querySelectorAll('.mf-table thead th')].map(e => e.textContent),
        rows: [...box.querySelectorAll('.mf-table tbody tr')].map(r => [...r.querySelectorAll('td')].map(c => c.textContent.trim())),
        toolbar: [...box.querySelectorAll('.mfx-manage-toolbar .mf-btn')].map(b => b.textContent.trim())
    };
})()`);

// Rechte einer Rolle setzen
await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    [...box.querySelectorAll('.mf-table tbody tr')][0].click();
    return true;
})()`);
await sleep(1000);

const access = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    return {
        title: box.querySelector('.mfx-admin-title')?.textContent,
        heading: box.querySelector('.mfx-manage-heading')?.textContent,
        columns: [...box.querySelectorAll('.mf-table thead th')].map(e => e.textContent),
        rows: [...box.querySelectorAll('.mf-table tbody tr')].map(r => r.querySelector('td')?.textContent.trim()),
        boxes: box.querySelectorAll('.mf-table tbody input[type=checkbox]').length,
        footButtons: [...box.querySelectorAll('.mfx-manage-foot .mf-btn')].map(b => b.textContent.trim())
    };
})()`);

const shot3 = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT.replace(/\.png$/, '-access.png'), Buffer.from(shot3.data, 'base64'));

await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    const first = box.querySelector('.mf-table tbody input[type=checkbox]');
    first.checked = !first.checked;
    box.querySelector('.mfx-manage-foot .mf-btn-primary').click();
    return true;
})()`);
await sleep(1500);

const afterAccess = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    return {
        backToList: !!box.querySelector('.mfx-manage-toolbar'),
        rows: [...box.querySelectorAll('.mf-table tbody tr')].map(r => [...r.querySelectorAll('td')].map(c => c.textContent.trim())),
        flash: document.querySelector('.mfx-flash')?.textContent || null
    };
})()`);

// Systemeinstellungen: lesen, aendern, speichern, Bereich erneut aufrufen
await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-admin');
    [...box.querySelectorAll('.mfx-admin-item')]
        .find(b => /^(Systemeinstellungen|System settings)/.test(b.textContent.trim())).click();
    return true;
})()`);
await sleep(1200);

const systemDialog = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    if (!box) return null;
    const rect = box.getBoundingClientRect();
    return {
        title: box.querySelector('.mfx-admin-title')?.textContent,
        note: box.querySelector('.mfx-manage-note')?.textContent,
        fields: [...box.querySelectorAll('.mf-field > label')].map(e => e.textContent),
        values: [...box.querySelectorAll('.mf-field input[type=number]')].map(i => i.value),
        checks: [...box.querySelectorAll('.mf-checklist .mf-check span')].map(e => e.textContent),
        facts: [...box.querySelectorAll('.mf-facts dd')].map(e => e.textContent),
        footButtons: [...box.querySelectorAll('.mfx-manage-foot .mf-btn')].map(b => b.textContent.trim()),
        fitsScreen: rect.width <= innerWidth && rect.height <= innerHeight
    };
})()`);

const shot4 = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT.replace(/\.png$/, '-system.png'), Buffer.from(shot4.data, 'base64'));

await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    const days = box.querySelectorAll('.mf-field input[type=number]')[1];
    days.value = '21';
    days.dispatchEvent(new Event('input', { bubbles: true }));
    box.querySelector('.mfx-manage-foot .mf-btn-primary').click();
    return true;
})()`);
await sleep(1500);

const systemFlash = await evaluate(`document.querySelector('.mfx-flash')?.textContent || null`);

// Bereich wechseln und zurueck - so wird der Wert frisch geladen
await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-admin');
    const items = [...box.querySelectorAll('.mfx-admin-item')];
    items.find(b => /^(Rollen|Roles)/.test(b.textContent.trim())).click();
    items.find(b => /^(Systemeinstellungen|System settings)/.test(b.textContent.trim())).click();
    return true;
})()`);
await sleep(1200);

const systemAgain = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    return [...box.querySelectorAll('.mf-field input[type=number]')].map(i => i.value);
})()`);

// Ausgangswert wiederherstellen, damit die Suite nichts hinterlaesst
await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-manage');
    const days = box.querySelectorAll('.mf-field input[type=number]')[1];
    days.value = ${JSON.stringify(String(systemDialog?.values?.[1] ?? '14'))};
    days.dispatchEvent(new Event('input', { bubbles: true }));
    box.querySelector('.mfx-manage-foot .mf-btn-primary').click();
    return true;
})()`);
await sleep(1200);

// Protokoll: Tabelle, Filter, Blaetterleiste
await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-admin');
    [...box.querySelectorAll('.mfx-admin-item')]
        .find(b => /^(Protokoll|Activity log)/.test(b.textContent.trim())).click();
    return true;
})()`);
await sleep(1500);

const activity = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-admin');
    return {
        title: box.querySelector('.mfx-admin-title')?.textContent,
        columns: [...box.querySelectorAll('.mf-table thead th')].map(e => e.textContent),
        rows: box.querySelectorAll('.mf-table tbody tr').length,
        filters: [...box.querySelectorAll('.mfx-manage-toolbar select')].map(s => s.options.length),
        pager: box.querySelector('.mfx-manage-pager')?.textContent?.trim(),
        footButtons: [...box.querySelectorAll('.mfx-manage-foot .mf-btn')].map(b => b.textContent.trim())
    };
})()`);

// Nach einer Aktion filtern: es duerfen nur noch deren Zeilen stehen
const filtered = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-admin');
    const select = box.querySelector('.mfx-manage-toolbar select');
    const option = [...select.options].find(o => o.value === 'Login') || select.options[1];
    select.value = option.value;
    select.dispatchEvent(new Event('change', { bubbles: true }));
    return option.value;
})()`);
await sleep(1200);

const afterFilter = await evaluate(`(() => {
    const box = document.querySelector('dialog.mfx-admin');
    const actions = [...box.querySelectorAll('.mf-table tbody tr')].map(r => r.children[2]?.textContent.trim());
    return { rows: actions.length, distinct: [...new Set(actions)] };
})()`);

const shot5 = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT.replace(/\.png$/, '-activity.png'), Buffer.from(shot5.data, 'base64'));

console.log(JSON.stringify({ menu, sections, users, formView, afterCreate, afterDelete, roles, systemRole, locations, access, afterAccess, systemDialog, systemFlash, systemAgain, activity, filtered, afterFilter, consoleErrors }, null, 2));

socket.close();
edge.kill();
process.exit(consoleErrors.length > 0 ? 1 : 0);
