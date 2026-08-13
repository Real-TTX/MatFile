// Rekursive Suche in einem eigenen Fenster: Fortschritt, Treffer aus allen
// Unterordnern und der Weg zurück in den Ordner des Treffers.
import { api, t, icon, formatSize, formatDate, errorKey } from './api.js';

const POLL_MS = 400;

let win = null;
let elements = null;
let handlers = { open: () => { }, reveal: () => { } };

const run = {
    id: null,
    rootId: null,
    rootName: '',
    basePath: '',
    query: '',
    content: false,
    includeHidden: false,
    received: 0,
    timer: null
};

// ---------------------------------------------------------------- Fenster

function build() {
    if (win) {
        return;
    }

    win = document.createElement('section');
    win.className = 'mfx-window';
    win.setAttribute('role', 'dialog');
    win.setAttribute('aria-label', t('SearchResults'));
    win.hidden = true;

    const head = document.createElement('header');
    head.className = 'mfx-window-head';
    head.appendChild(icon('search', 16));

    const titles = document.createElement('div');
    titles.className = 'mfx-window-titles';

    const title = document.createElement('span');
    title.className = 'mfx-window-title';
    title.textContent = t('SearchResults');
    titles.appendChild(title);

    const sub = document.createElement('span');
    sub.className = 'mfx-window-sub';
    titles.appendChild(sub);

    head.appendChild(titles);

    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'mfx-btn';
    close.title = t('Close');
    close.setAttribute('aria-label', t('Close'));
    close.appendChild(icon('x', 14));
    close.addEventListener('click', () => closeWindow());
    head.appendChild(close);

    const tools = document.createElement('div');
    tools.className = 'mfx-window-tools';

    const content = checkbox(t('SearchContent'), () => restart());
    const hidden = checkbox(t('ShowHidden'), () => restart());
    tools.appendChild(content.label);
    tools.appendChild(hidden.label);

    const again = document.createElement('button');
    again.type = 'button';
    again.className = 'mf-btn mf-btn-quiet';
    again.appendChild(icon('refresh', 14));
    again.appendChild(document.createTextNode(t('Refresh')));
    again.addEventListener('click', () => restart());
    tools.appendChild(again);

    const progress = document.createElement('div');
    progress.className = 'mfx-search-progress';

    const bar = document.createElement('div');
    bar.className = 'mfx-progress';
    const fill = document.createElement('div');
    fill.className = 'mfx-progress-fill';
    bar.appendChild(fill);
    progress.appendChild(bar);

    const stats = document.createElement('div');
    stats.className = 'mfx-search-stats';
    progress.appendChild(stats);

    const current = document.createElement('div');
    current.className = 'mfx-search-current';
    progress.appendChild(current);

    const hits = document.createElement('ul');
    hits.className = 'mfx-search-hits';

    const foot = document.createElement('footer');
    foot.className = 'mfx-window-foot';

    const count = document.createElement('span');
    count.className = 'mfx-search-count';
    foot.appendChild(count);

    const spacer = document.createElement('span');
    spacer.className = 'mf-spacer';
    foot.appendChild(spacer);

    const stop = document.createElement('button');
    stop.type = 'button';
    stop.className = 'mf-btn';
    stop.textContent = t('Cancel');
    stop.addEventListener('click', () => {
        if (run.id) {
            api.cancelSearch(run.id).catch(() => { });
        }
    });
    foot.appendChild(stop);

    const done = document.createElement('button');
    done.type = 'button';
    done.className = 'mf-btn mf-btn-primary';
    done.textContent = t('Close');
    done.hidden = true;
    done.addEventListener('click', () => closeWindow());
    foot.appendChild(done);

    win.appendChild(head);
    win.appendChild(tools);
    win.appendChild(progress);
    win.appendChild(hits);
    win.appendChild(foot);
    document.body.appendChild(win);

    makeDraggable(head);

    elements = { sub, content: content.input, hidden: hidden.input, fill, stats, current, hits, count, stop, done };
}

function checkbox(label, onChange) {
    const wrapper = document.createElement('label');
    wrapper.className = 'mfx-window-option';

    const input = document.createElement('input');
    input.type = 'checkbox';
    input.addEventListener('change', onChange);

    wrapper.appendChild(input);
    wrapper.appendChild(document.createTextNode(label));

    return { label: wrapper, input };
}

/** Das Fenster lässt sich an der Kopfzeile verschieben. */
function makeDraggable(head) {
    head.addEventListener('pointerdown', event => {
        if (event.target.closest('button')) {
            return;
        }

        const rect = win.getBoundingClientRect();
        const offsetX = event.clientX - rect.left;
        const offsetY = event.clientY - rect.top;

        win.classList.add('is-dragging');
        head.setPointerCapture(event.pointerId);

        const onMove = moveEvent => {
            const left = Math.min(Math.max(4, moveEvent.clientX - offsetX), window.innerWidth - rect.width - 4);
            const top = Math.min(Math.max(4, moveEvent.clientY - offsetY), window.innerHeight - rect.height - 4);

            win.style.left = `${left}px`;
            win.style.top = `${top}px`;
            win.style.right = 'auto';
            win.style.bottom = 'auto';
        };

        const onUp = () => {
            win.classList.remove('is-dragging');
            head.removeEventListener('pointermove', onMove);
            head.removeEventListener('pointerup', onUp);
        };

        head.addEventListener('pointermove', onMove);
        head.addEventListener('pointerup', onUp);
    });
}

function closeWindow() {
    stopPolling();

    if (run.id) {
        api.cancelSearch(run.id).catch(() => { });
        run.id = null;
    }

    if (win) {
        win.hidden = true;
    }
}

export function isSearchOpen() {
    return !!win && !win.hidden;
}

// ---------------------------------------------------------------- Suche

/**
 * Startet eine rekursive Suche und öffnet das Ergebnisfenster.
 * @param context {{rootId, rootName, path, query, content, includeHidden}}
 */
export async function openSearch(context, callbacks) {
    build();

    if (callbacks) {
        handlers = { ...handlers, ...callbacks };
    }

    run.rootId = context.rootId;
    run.rootName = context.rootName || '';
    run.basePath = context.path || '';
    run.query = context.query;
    run.content = !!context.content;
    run.includeHidden = !!context.includeHidden;

    elements.content.checked = run.content;
    elements.hidden.checked = run.includeHidden;

    win.hidden = false;
    await start();
}

async function start() {
    stopPolling();

    if (run.id) {
        api.cancelSearch(run.id).catch(() => { });
        run.id = null;
    }

    run.received = 0;
    elements.hits.replaceChildren();
    elements.count.textContent = '';
    elements.current.textContent = '';
    elements.fill.style.width = '0%';
    elements.stop.hidden = false;
    elements.done.hidden = true;

    const scope = run.basePath ? `${run.rootName}/${run.basePath}` : run.rootName;
    elements.sub.textContent = `„${run.query}" · ${scope}`;
    elements.stats.textContent = t('SearchRunning');

    try {
        const started = await api.startSearch({
            rootId: run.rootId,
            path: run.basePath,
            query: run.query,
            content: run.content,
            includeHidden: run.includeHidden
        });

        run.id = started.searchId;
        poll();
        run.timer = setInterval(poll, POLL_MS);
    } catch (error) {
        elements.stats.textContent = t(errorKey(error && error.code), t('Error'));
        finished();
    }
}

function restart() {
    run.content = elements.content.checked;
    run.includeHidden = elements.hidden.checked;
    start();
}

function stopPolling() {
    if (run.timer) {
        clearInterval(run.timer);
        run.timer = null;
    }
}

function finished() {
    stopPolling();
    elements.stop.hidden = true;
    elements.done.hidden = false;
}

async function poll() {
    if (!run.id) {
        return;
    }

    let snapshot;

    try {
        snapshot = await api.searchStatus(run.id, run.received);
    } catch (error) {
        elements.stats.textContent = t(errorKey(error && error.code), t('Error'));
        finished();
        return;
    }

    snapshot.hits.forEach(hit => elements.hits.appendChild(hitRow(hit)));
    run.received += snapshot.hits.length;

    elements.fill.style.width = `${snapshot.percent}%`;
    elements.current.textContent = snapshot.current ? `${run.rootName}/${snapshot.current}` : '';

    const scanned = t('SearchProgress')
        .replace('{0}', snapshot.folders.toLocaleString())
        .replace('{1}', snapshot.files.toLocaleString());

    elements.stats.textContent = snapshot.state === 'running'
        ? `${t('SearchRunning')} · ${scanned}`
        : `${stateText(snapshot)} · ${scanned}`;

    elements.count.textContent = snapshot.total === 0 && snapshot.state !== 'running'
        ? t('SearchNoHits')
        : t('SearchHits').replace('{0}', snapshot.total.toLocaleString())
          + (snapshot.truncated ? ` · ${t('SearchTruncated')}` : '');

    if (snapshot.state !== 'running') {
        finished();
    }
}

function stateText(snapshot) {
    if (snapshot.state === 'completed') {
        return t('SearchDone');
    }

    if (snapshot.state === 'cancelled') {
        return t('SearchCancelled');
    }

    return t(errorKey(snapshot.error), t('Error'));
}

/** Schreibt Text in ein Element und hebt die Fundstelle hervor - ohne HTML aus dem Inhalt. */
function highlight(target, text, needle) {
    const index = needle ? text.toLowerCase().indexOf(needle.toLowerCase()) : -1;

    if (index < 0) {
        target.textContent = text;
        return;
    }

    target.appendChild(document.createTextNode(text.slice(0, index)));

    const mark = document.createElement('mark');
    mark.textContent = text.slice(index, index + needle.length);
    target.appendChild(mark);

    target.appendChild(document.createTextNode(text.slice(index + needle.length)));
}

function hitRow(hit) {
    const row = document.createElement('li');
    row.className = 'mfx-hit';
    row.tabIndex = 0;

    const symbol = document.createElement('span');
    symbol.className = 'mfx-hit-icon';
    symbol.appendChild(icon(hit.isDirectory ? 'folder' : 'file', 18));
    row.appendChild(symbol);

    const text = document.createElement('div');
    text.className = 'mfx-hit-text';

    const name = document.createElement('span');
    name.className = 'mfx-hit-name';
    name.textContent = hit.name;
    text.appendChild(name);

    const where = document.createElement('span');
    where.className = 'mfx-hit-path';
    where.textContent = hit.parentPath ? `${run.rootName}/${hit.parentPath}` : run.rootName;
    text.appendChild(where);

    // Fundstelle aus dem Dateiinhalt, mit der Suchzeichenfolge hervorgehoben.
    if (hit.snippet) {
        const snippet = document.createElement('span');
        snippet.className = 'mfx-hit-snippet';
        highlight(snippet, hit.snippet, run.query);
        text.appendChild(snippet);
    }

    row.appendChild(text);

    const meta = document.createElement('div');
    meta.className = 'mfx-hit-meta';

    const size = document.createElement('span');
    size.textContent = hit.isDirectory ? '' : formatSize(hit.size);
    meta.appendChild(size);

    const date = document.createElement('span');
    date.textContent = formatDate(hit.modified);
    meta.appendChild(date);

    row.appendChild(meta);

    const reveal = document.createElement('button');
    reveal.type = 'button';
    reveal.className = 'mfx-btn';
    reveal.title = t('ShowInFolder');
    reveal.setAttribute('aria-label', t('ShowInFolder'));
    reveal.appendChild(icon('folder', 14));
    reveal.addEventListener('click', event => {
        event.stopPropagation();
        handlers.reveal(hit);
    });
    row.appendChild(reveal);

    row.addEventListener('dblclick', () => handlers.open(hit));
    row.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            handlers.open(hit);
        }
    });

    return row;
}
