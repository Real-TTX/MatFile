// Syntaxpruefung der ES-Module.
//
// "node --check datei.js" prueft .js-Dateien als CommonJS und uebersieht dabei
// Fehler, die nur im Modulkontext auffallen (etwa eine doppelte const-Deklaration).
// Deshalb wird jede Datei als .mjs kopiert und in dieser Form geprueft.
import { spawnSync } from 'node:child_process';
import { readdirSync, copyFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, basename } from 'node:path';

const roots = process.argv.slice(2);
const folders = roots.length > 0 ? roots : ['src/MatFile.Web/wwwroot/js', 'src/MatFile.Web/wwwroot/js/explorer'];

const temp = mkdtempSync(join(tmpdir(), 'mfjs-'));
let checked = 0;
let failed = 0;

for (const folder of folders) {
    let files;

    try {
        files = readdirSync(folder).filter(name => name.endsWith('.js'));
    } catch {
        console.log(`FAIL  ${folder} nicht gefunden`);
        failed++;
        continue;
    }

    for (const file of files) {
        const target = join(temp, basename(file, '.js') + '.mjs');
        copyFileSync(join(folder, file), target);

        const result = spawnSync(process.execPath, ['--check', target], { encoding: 'utf8' });
        checked++;

        if (result.status !== 0) {
            failed++;
            console.log(`FAIL  ${folder}/${file}`);
            console.log(result.stderr.split('\n').slice(0, 6).join('\n'));
        }
    }
}

rmSync(temp, { recursive: true, force: true });

console.log(`\n${checked - failed}/${checked} Module ohne Syntaxfehler`);
process.exit(failed > 0 ? 1 : 0);
