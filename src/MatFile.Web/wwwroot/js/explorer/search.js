// Rekursive Suche: Lauf starten, Fortschritt nachfragen, abbrechen.
// Die Anzeige liegt im Explorer - jede Suche gehört zu ihrem Tab.
import { api } from './api.js';

const POLL_MS = 400;

/** Frischer Zustand für einen Suchtab. */
export function newSearch(rootId, path) {
    return {
        // Eingaben
        term: '',
        content: false,
        hidden: false,
        scopeRootId: rootId ?? null,
        scopePath: path || '',

        // Lauf
        id: null,
        state: 'idle',
        percent: 0,
        folders: 0,
        files: 0,
        current: '',
        total: 0,
        truncated: false,
        error: null,
        hits: [],
        received: 0,
        timer: null
    };
}

export function isRunning(search) {
    return search.state === 'running';
}

/** Startet die Suche neu. Jede Änderung meldet sich über onUpdate. */
export async function startSearch(search, onUpdate) {
    stopPolling(search);

    if (search.id) {
        api.cancelSearch(search.id).catch(() => { });
    }

    Object.assign(search, {
        id: null, state: 'running', percent: 0, folders: 0, files: 0,
        current: '', total: 0, truncated: false, error: null, hits: [], received: 0
    });

    onUpdate();

    try {
        const started = await api.startSearch({
            rootId: search.scopeRootId,
            path: search.scopePath,
            query: search.term,
            content: search.content,
            includeHidden: search.hidden
        });

        search.id = started.searchId;
    } catch (error) {
        search.state = 'failed';
        search.error = error && error.code;
        onUpdate();
        return;
    }

    await poll(search, onUpdate);

    if (search.state === 'running') {
        search.timer = setInterval(() => poll(search, onUpdate), POLL_MS);
    }
}

async function poll(search, onUpdate) {
    if (!search.id) {
        return;
    }

    let snapshot;

    try {
        snapshot = await api.searchStatus(search.id, search.received);
    } catch (error) {
        search.state = 'failed';
        search.error = error && error.code;
        stopPolling(search);
        onUpdate();
        return;
    }

    // Treffer kommen stückweise nach, damit lange Läufe früh etwas zeigen.
    snapshot.hits.forEach(hit => search.hits.push(hit));
    search.received += snapshot.hits.length;

    search.percent = snapshot.percent;
    search.folders = snapshot.folders;
    search.files = snapshot.files;
    search.current = snapshot.current;
    search.total = snapshot.total;
    search.truncated = snapshot.truncated;
    search.state = snapshot.state;
    search.error = snapshot.error;

    if (snapshot.state !== 'running') {
        stopPolling(search);
    }

    onUpdate();
}

/** Bricht den Lauf auf der Gegenseite ab; der Zustand kommt über die Abfrage nach. */
export function cancelSearch(search) {
    if (search.id) {
        api.cancelSearch(search.id).catch(() => { });
    }
}

export function stopPolling(search) {
    if (search && search.timer) {
        clearInterval(search.timer);
        search.timer = null;
    }
}

/** Tab wird geschlossen: nicht weiter fragen und die Gegenseite entlasten. */
export function disposeSearch(search) {
    stopPolling(search);
    cancelSearch(search);
}
