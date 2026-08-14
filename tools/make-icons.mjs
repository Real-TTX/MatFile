// Erzeugt das Icon-Set aus wwwroot/icons/matfile.svg.
//
// Kein npm, keine Bildbibliothek: Edge rendert das SVG im Headless-Modus und
// liefert die PNGs, die favicon.ico setzt dieses Skript selbst zusammen.
// Aufruf:  node tools/make-icons.mjs
import { spawn } from 'node:child_process';
import { writeFileSync, readFileSync, mkdtempSync, existsSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const iconDir = join(here, '..', 'src', 'MatFile.Web', 'wwwroot', 'icons');
const PORT = 9380;

// Grösse -> Datei. Die ICO-Grössen landen zusätzlich in favicon.ico.
const PNGS = [
    { size: 16, file: 'favicon-16.png', ico: true },
    { size: 32, file: 'favicon-32.png', ico: true },
    { size: 48, file: 'favicon-48.png', ico: true },
    { size: 180, file: 'apple-touch-icon.png' },
    { size: 192, file: 'matfile-192.png' },
    { size: 512, file: 'matfile-512.png' },
    { size: 512, file: 'matfile-maskable-512.png', maskable: true }
];

const EDGE = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'
].find(p => existsSync(p));

if (!EDGE) {
    console.error('Microsoft Edge nicht gefunden - das Skript braucht ihn zum Rendern.');
    process.exit(1);
}

const sleep = ms => new Promise(r => setTimeout(r, ms));
const source = readFileSync(join(iconDir, 'matfile.svg'), 'utf8');

/**
 * Seite mit dem SVG in der gewünschten Kantenlänge.
 * Maskable-Icons brauchen Luft: der Kreis der Plattform schneidet rund 20 % weg,
 * deshalb füllt der Farbverlauf die Fläche und das Motiv sitzt kleiner darin.
 */
function page(size, maskable) {
    const inner = maskable
        ? source
            .replace('<rect width="512" height="512" rx="112"', '<rect width="512" height="512" rx="0"')
            .replace(/(<rect width="512" height="512" rx="0" fill="url\(#mf-bg\)" \/>)/,
                '$1<g transform="translate(256 256) scale(0.72) translate(-256 -256)">')
            .replace('</svg>', '</g></svg>')
        : source;

    return 'data:text/html;charset=utf-8,' + encodeURIComponent(
        `<!doctype html><meta charset="utf-8">
         <style>html,body{margin:0;padding:0;background:transparent}
         svg{display:block;width:${size}px;height:${size}px}</style>${inner}`);
}

const profile = mkdtempSync(join(tmpdir(), 'mficons-'));
const edge = spawn(EDGE, ['--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
    '--window-size=600,600', '--no-first-run', '--disable-gpu', '--hide-scrollbars', 'about:blank'], { stdio: 'ignore' });

let target = null;
for (let i = 0; i < 40 && !target; i++) {
    try {
        const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
        target = list.find(t => t.type === 'page');
    } catch { }
    if (!target) await sleep(250);
}

const socket = new WebSocket(target.webSocketDebuggerUrl);
await new Promise(r => socket.addEventListener('open', r));

let id = 1;
const pending = new Map();
let loads = 0;

socket.addEventListener('message', e => {
    const d = JSON.parse(e.data);
    if (d.id && pending.has(d.id)) { pending.get(d.id)(d.result); pending.delete(d.id); }
    if (d.method === 'Page.loadEventFired') loads++;
});

const send = (method, params = {}) => {
    const n = id++;
    socket.send(JSON.stringify({ id: n, method, params }));
    return new Promise(r => pending.set(n, r));
};

await send('Page.enable');

if (!existsSync(iconDir)) {
    mkdirSync(iconDir, { recursive: true });
}

const written = [];

for (const item of PNGS) {
    await send('Emulation.setDeviceMetricsOverride', {
        width: item.size, height: item.size, deviceScaleFactor: 1, mobile: false
    });

    // Ohne diesen Aufruf steht Weiss hinter den runden Ecken statt Transparenz.
    await send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: 0 } });

    const before = loads;
    await send('Page.navigate', { url: page(item.size, item.maskable) });
    for (let i = 0; i < 40 && loads === before; i++) await sleep(50);
    await sleep(150);

    const shot = await send('Page.captureScreenshot', {
        format: 'png',
        clip: { x: 0, y: 0, width: item.size, height: item.size, scale: 1 },
        captureBeyondViewport: true
    });

    const bytes = Buffer.from(shot.data, 'base64');
    writeFileSync(join(iconDir, item.file), bytes);
    written.push({ ...item, bytes });
    console.log(`${item.file.padEnd(28)} ${item.size}px  ${bytes.length} Bytes`);
}

socket.close();
edge.kill();

// --- favicon.ico -----------------------------------------------------------
// Eine ICO-Datei ist ein Verzeichnis mit Bildern; seit Vista dürfen es PNGs sein.
const parts = written.filter(item => item.ico);
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);            // reserviert
header.writeUInt16LE(1, 2);            // Typ 1 = Icon
header.writeUInt16LE(parts.length, 4);

let offset = 6 + parts.length * 16;
const entries = [];

for (const part of parts) {
    const entry = Buffer.alloc(16);
    entry.writeUInt8(part.size >= 256 ? 0 : part.size, 0);   // Breite (0 = 256)
    entry.writeUInt8(part.size >= 256 ? 0 : part.size, 1);   // Höhe
    entry.writeUInt8(0, 2);                                  // Farben in der Palette
    entry.writeUInt8(0, 3);                                  // reserviert
    entry.writeUInt16LE(1, 4);                               // Farbebenen
    entry.writeUInt16LE(32, 6);                              // Bits je Bildpunkt
    entry.writeUInt32LE(part.bytes.length, 8);
    entry.writeUInt32LE(offset, 12);
    entries.push(entry);
    offset += part.bytes.length;
}

const ico = Buffer.concat([header, ...entries, ...parts.map(p => p.bytes)]);
writeFileSync(join(iconDir, '..', 'favicon.ico'), ico);
console.log(`favicon.ico                  ${parts.map(p => p.size).join('/')}px  ${ico.length} Bytes`);

process.exit(0);
