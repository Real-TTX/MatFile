// Übertragungen: Uploads in Teilstücken und laufende Jobs mit Fortschritt.
import { api, t, icon, errorKey } from './api.js';
import { askConfirm, flash } from './dialogs.js';

const CHUNK = 8 * 1024 * 1024;

const panel = document.createElement('div');
panel.className = 'mfx-transfers';
panel.hidden = true;

const head = document.createElement('div');
head.className = 'mfx-transfers-head';

const title = document.createElement('span');
title.textContent = t('Transfers');
head.appendChild(title);

const close = document.createElement('button');
close.type = 'button';
close.className = 'mfx-btn';
close.title = t('Close');
close.appendChild(icon('x', 14));
close.addEventListener('click', () => {
    items.clear();
    render();
});
head.appendChild(close);

const list = document.createElement('ul');
list.className = 'mfx-transfer-list';

panel.appendChild(head);
panel.appendChild(list);
document.body.appendChild(panel);

/** id -> { label, percent, state, cancel } */
const items = new Map();
let onFinished = () => { };
let pollTimer = null;

function render() {
    list.replaceChildren();

    items.forEach((item, id) => {
        const entry = document.createElement('li');
        entry.className = 'mfx-transfer' + (item.state === 'failed' ? ' is-failed' : '');

        const label = document.createElement('div');
        label.className = 'mfx-transfer-label';

        const name = document.createElement('span');
        name.className = 'mfx-transfer-name';
        name.textContent = item.label;
        label.appendChild(name);

        const info = document.createElement('span');
        info.className = 'mfx-transfer-info';
        info.textContent = item.state === 'running' || item.state === 'pending'
            ? `${item.percent}%`
            : t('State' + item.state.charAt(0).toUpperCase() + item.state.slice(1), item.state);
        label.appendChild(info);

        entry.appendChild(label);

        const bar = document.createElement('div');
        bar.className = 'mfx-progress';
        const fill = document.createElement('div');
        fill.className = 'mfx-progress-fill';
        fill.style.width = `${item.percent}%`;
        bar.appendChild(fill);
        entry.appendChild(bar);

        if (item.cancel && (item.state === 'running' || item.state === 'pending')) {
            const cancel = document.createElement('button');
            cancel.type = 'button';
            cancel.className = 'mfx-btn mfx-transfer-cancel';
            cancel.title = t('Cancel');
            cancel.appendChild(icon('x', 14));
            cancel.addEventListener('click', () => item.cancel());
            entry.appendChild(cancel);
        }

        list.appendChild(entry);
    });

    panel.hidden = items.size === 0;
}

function set(id, values) {
    const current = items.get(id) || { label: '', percent: 0, state: 'pending', cancel: null };
    items.set(id, { ...current, ...values });
    render();
}

function done(id, delay) {
    setTimeout(() => {
        items.delete(id);
        render();
    }, delay || 2500);
}

// ---------------------------------------------------------------- Uploads

/** Sagt, woran es lag - bei einer Größengrenze hilft „fehlgeschlagen" allein nicht weiter. */
function uploadError(error, file) {
    const code = error && error.code;

    if (code && code !== 'error') {
        const text = t(errorKey(code), '');
        if (text) {
            return text;
        }
    }

    return t('UploadFailed').replace('{0}', file.name);
}

async function uploadFile(file, rootId, path) {
    const id = `up-${file.name}-${Date.now()}-${Math.round(file.size)}`;
    set(id, { label: file.name, percent: 0, state: 'running' });

    let session;

    try {
        session = await api.uploadBegin(rootId, path, file.name, file.size, false);
    } catch (error) {
        if (error.status === 409) {
            const overwrite = await askConfirm(t('Overwrite'), t('OverwriteQuestion').replace('{0}', file.name), t('Overwrite'), false);
            if (!overwrite) {
                items.delete(id);
                render();
                return false;
            }

            session = await api.uploadBegin(rootId, path, file.name, file.size, true);
        } else {
            set(id, { state: 'failed', percent: 100 });
            done(id, 5000);
            flash(uploadError(error, file), true);
            return false;
        }
    }

    let offset = session.received || 0;

    try {
        while (offset < file.size) {
            const slice = file.slice(offset, Math.min(offset + CHUNK, file.size));
            const result = await api.uploadChunk(session.uploadId, offset, slice);
            offset = result.received;
            set(id, { percent: Math.round(offset * 100 / Math.max(1, file.size)) });
        }

        await api.uploadFinish(session.uploadId);
        set(id, { percent: 100, state: 'completed' });
        done(id);
        return true;
    } catch (error) {
        set(id, { state: 'failed' });
        done(id, 5000);
        flash(t('UploadFailed').replace('{0}', file.name), true);

        try {
            await api.uploadAbort(session.uploadId);
        } catch (ignored) {
            // Aufräumen ist Nebensache.
        }

        return false;
    }
}

/** Lädt mehrere Dateien nacheinander hoch. */
export async function uploadFiles(files, rootId, path) {
    let changed = false;

    for (const file of files) {
        const ok = await uploadFile(file, rootId, path);
        changed = changed || ok;
    }

    if (changed) {
        onFinished();
    }
}

// ---------------------------------------------------------------- Jobs

const jobLabels = {
    Copy: 'JobCopy',
    Move: 'JobMove',
    Delete: 'JobDelete',
    Zip: 'JobZip',
    Unzip: 'JobUnzip',
    Upload: 'JobUpload'
};

/** Nimmt einen frisch angelegten Job in die Anzeige auf und startet die Abfrage. */
export function trackJob(job) {
    const id = `job-${job.token}`;

    set(id, {
        label: t(jobLabels[job.jobType] || 'JobCopy'),
        percent: job.progress || 0,
        state: 'pending',
        cancel: () => api.cancelJob(job.token).catch(() => { })
    });

    startPolling();
}

function startPolling() {
    if (pollTimer) {
        return;
    }

    pollTimer = setInterval(async () => {
        let jobs;

        try {
            jobs = await api.jobs();
        } catch (error) {
            return;
        }

        let active = false;
        let finished = false;

        jobs.forEach(job => {
            const id = `job-${job.token}`;
            if (!items.has(id)) {
                return;
            }

            const state = job.status.toLowerCase();

            if (state === 'pending' || state === 'running') {
                active = true;
                set(id, { percent: job.progress, state });
                return;
            }

            set(id, { percent: 100, state: state === 'completed' ? 'completed' : state });
            done(id, state === 'completed' ? 1500 : 5000);
            finished = true;

            if (state === 'failed') {
                flash(t('JobFailed').replace('{0}', t(errorKey(job.error), job.error || '')), true);
            }
        });

        if (finished) {
            onFinished();
        }

        if (!active) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }, 1000);
}

/** Wird aufgerufen, sobald sich am Speicherort etwas geändert hat. */
export function onTransfersFinished(callback) {
    onFinished = callback;
}
