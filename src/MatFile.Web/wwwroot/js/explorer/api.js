// Zugriff auf die MatFile-API und kleine Formatierungshelfer.

const bundle = window.matfileText || {};

/** Übersetzter Text aus dem Bundle der Seite. */
export function t(key, fallback) {
    return bundle[key] !== undefined ? bundle[key] : (fallback !== undefined ? fallback : key);
}

async function request(url, options) {
    const response = await fetch(url, {
        credentials: 'same-origin',
        headers: { 'Accept': 'application/json' },
        ...options
    });

    // Sitzung abgelaufen: neu laden, damit die Anmeldung erscheint.
    if (response.status === 401) {
        window.location.reload();
        throw Object.assign(new Error('unauthorized'), { status: 401, code: 'unauthorized' });
    }

    if (!response.ok) {
        let code = 'error';
        try {
            const body = await response.json();
            code = body.error || code;
        } catch (ignored) {
            // Antwort ohne JSON-Körper, z. B. eine Weiterleitung auf die Anmeldung.
        }

        const error = new Error(code);
        error.status = response.status;
        error.code = code;
        throw error;
    }

    return response.status === 204 ? null : response.json();
}

function send(url, method, body) {
    return request(url, {
        method,
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
        body: JSON.stringify(body)
    });
}

/** Baut eine Abfragezeichenfolge und lässt leere Werte weg. */
function queryString(values) {
    const parts = Object.entries(values || {})
        .filter(([, value]) => value !== null && value !== undefined && value !== '')
        .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`);

    return parts.length === 0 ? '' : '?' + parts.join('&');
}

export const api = {
    roots: () => request('/api/fs/roots'),

    list: (rootId, path) =>
        request(`/api/fs/list?rootId=${encodeURIComponent(rootId)}&path=${encodeURIComponent(path || '')}`),

    status: rootId => request(`/api/fs/status?rootId=${encodeURIComponent(rootId)}`),

    text: (rootId, path) =>
        request(`/api/fs/text?rootId=${encodeURIComponent(rootId)}&path=${encodeURIComponent(path || '')}`),

    saveText: (rootId, path, content, ifModified) =>
        send('/api/fs/text', 'PUT', { rootId, path, content, ifModified }),

    activity: (rootId, path) =>
        request(`/api/fs/activity?rootId=${encodeURIComponent(rootId)}&path=${encodeURIComponent(path || '')}`),

    previewUrl: (rootId, path) =>
        `/api/fs/preview?rootId=${encodeURIComponent(rootId)}&path=${encodeURIComponent(path || '')}`,

    downloadUrl: (rootId, path) =>
        `/api/fs/download?rootId=${encodeURIComponent(rootId)}&path=${encodeURIComponent(path || '')}`,

    settings: () => request('/api/settings'),
    saveSettings: settings => send('/api/settings', 'PUT', settings),

    bookmarks: () => request('/api/bookmarks'),
    addBookmark: (rootId, path, name) => send('/api/bookmarks', 'POST', { rootId, path, name }),
    deleteBookmark: id => request(`/api/bookmarks/${id}`, { method: 'DELETE' }),

    tabs: () => request('/api/tabs'),
    saveTabs: tabs => send('/api/tabs', 'PUT', tabs),

    // --- Dateioperationen ---
    createFolder: (rootId, path, name) => send('/api/fs/folder', 'POST', { rootId, path, name }),
    createFile: (rootId, path, name) => send('/api/fs/file', 'POST', { rootId, path, name }),
    rename: (rootId, path, newName) => send('/api/fs/rename', 'POST', { rootId, path, newName }),
    remove: (rootId, paths) => send('/api/fs/delete', 'POST', { rootId, paths }),

    transfer: (sourceRootId, paths, targetRootId, targetPath, move, overwrite) =>
        send('/api/fs/transfer', 'POST', { sourceRootId, paths, targetRootId, targetPath, move, overwrite }),

    compress: (rootId, paths, targetPath, name) =>
        send('/api/fs/compress', 'POST', { rootId, paths, targetPath, name }),

    extract: (rootId, path, targetPath) => send('/api/fs/extract', 'POST', { rootId, path, targetPath }),

    zipUrl: (rootId, paths) =>
        `/api/fs/zip?rootId=${encodeURIComponent(rootId)}&`
        + paths.map(p => `path=${encodeURIComponent(p)}`).join('&'),

    // --- Upload ---
    uploadBegin: (rootId, path, name, size, overwrite) =>
        send('/api/fs/upload/begin', 'POST', { rootId, path, name, size, overwrite }),

    uploadChunk: (uploadId, offset, blob) => request(
        `/api/fs/upload/chunk?uploadId=${encodeURIComponent(uploadId)}&offset=${offset}`,
        { method: 'POST', body: blob, headers: { 'Content-Type': 'application/octet-stream' } }),

    uploadFinish: uploadId => request(`/api/fs/upload/finish?uploadId=${encodeURIComponent(uploadId)}`, { method: 'POST' }),
    uploadAbort: uploadId => request(`/api/fs/upload/abort?uploadId=${encodeURIComponent(uploadId)}`, { method: 'POST' }),

    // --- Benutzer und Rollen verwalten ---
    users: query => request('/api/users' + queryString(query)),
    user: id => request(`/api/users/${id}`),
    createUser: data => send('/api/users', 'POST', data),
    updateUser: (id, data) => send(`/api/users/${id}`, 'PUT', data),
    deleteUser: id => request(`/api/users/${id}`, { method: 'DELETE' }),
    unlockUser: id => request(`/api/users/${id}/unlock`, { method: 'POST' }),

    permissions: (rootId, path) => request('/api/fs/permissions' + queryString({ rootId, path })),
    savePermissions: (rootId, path, mode) => send('/api/fs/permissions', 'PUT', { rootId, path, mode }),

    // Das ganze Protokoll; api.activity oben liefert nur den einzelnen Eintrag.
    activityLog: query => request('/api/activity' + queryString(query)),

    system: () => request('/api/system'),
    saveSystem: data => send('/api/system', 'PUT', data),

    roleList: query => request('/api/roles' + queryString(query)),
    role: id => request(`/api/roles/${id}`),
    createRole: data => send('/api/roles', 'POST', data),
    updateRole: (id, data) => send(`/api/roles/${id}`, 'PUT', data),
    deleteRole: id => request(`/api/roles/${id}`, { method: 'DELETE' }),

    // --- Speicherorte verwalten (nur mit dem Recht Root-Pfade verwalten) ---
    rootList: query => request('/api/roots' + queryString(query)),
    root: id => request(`/api/roots/${id}`),
    createRoot: data => send('/api/roots', 'POST', data),
    updateRoot: (id, data) => send(`/api/roots/${id}`, 'PUT', data),
    deleteRoot: id => request(`/api/roots/${id}`, { method: 'DELETE' }),
    testRoot: data => send('/api/roots/test', 'POST', data),

    browseRoot: (data, path) =>
        send(`/api/roots/browse?path=${encodeURIComponent(path || '')}`, 'POST', data),

    // --- Rekursive Suche ---
    startSearch: options => send('/api/search', 'POST', options),

    searchStatus: (id, from) =>
        request(`/api/search/${encodeURIComponent(id)}?from=${from || 0}`),

    cancelSearch: id => request(`/api/search/${encodeURIComponent(id)}/cancel`, { method: 'POST' }),

    // --- Jobs ---
    jobs: () => request('/api/jobs'),
    cancelJob: token => request(`/api/jobs/${encodeURIComponent(token)}/cancel`, { method: 'POST' })
};

/** Fehlercode der API auf einen Übersetzungsschlüssel abbilden: access_denied -> ErrorAccessDenied */
export function errorKey(code) {
    return 'Error' + String(code || '').replace(/(^|_)(\w)/g, (match, prefix, letter) => letter.toUpperCase());
}

/** Dateigröße in einer lesbaren Einheit. */
export function formatSize(bytes) {
    if (bytes === null || bytes === undefined) {
        return '';
    }

    if (bytes < 1024) {
        return `${bytes} B`;
    }

    const units = ['KB', 'MB', 'GB', 'TB', 'PB'];
    let value = bytes / 1024;
    let unit = 0;

    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }

    return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

/** Datum in der Sprache der Oberfläche. */
export function formatDate(value) {
    if (!value) {
        return '';
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
        return '';
    }

    return date.toLocaleString(document.documentElement.lang || undefined, {
        year: 'numeric', month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit'
    });
}

/** Endung als Typbezeichnung, z. B. "TXT". */
export function fileType(name) {
    const dot = name.lastIndexOf('.');
    return dot > 0 ? name.slice(dot + 1).toUpperCase() : '';
}

const icons = {
    folder: "<path d='M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z'/>",
    file: "<path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'/><polyline points='14 2 14 8 20 8'/>",
    'file-plus': "<path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'/><polyline points='14 2 14 8 20 8'/><line x1='12' y1='12' x2='12' y2='18'/><line x1='9' y1='15' x2='15' y2='15'/>",
    star: "<polygon points='12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2'/>",
    eye: "<path d='M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z'/><circle cx='12' cy='12' r='3'/>",
    info: "<circle cx='12' cy='12' r='10'/><line x1='12' y1='16' x2='12' y2='12'/><line x1='12' y1='8' x2='12.01' y2='8'/>",
    globe: "<circle cx='12' cy='12' r='10'/><line x1='2' y1='12' x2='22' y2='12'/><path d='M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z'/>",
    monitor: "<rect x='2' y='3' width='20' height='14' rx='2'/><line x1='8' y1='21' x2='16' y2='21'/><line x1='12' y1='17' x2='12' y2='21'/>",
    sun: "<circle cx='12' cy='12' r='5'/><line x1='12' y1='1' x2='12' y2='3'/><line x1='12' y1='21' x2='12' y2='23'/><line x1='4.22' y1='4.22' x2='5.64' y2='5.64'/><line x1='18.36' y1='18.36' x2='19.78' y2='19.78'/><line x1='1' y1='12' x2='3' y2='12'/><line x1='21' y1='12' x2='23' y2='12'/><line x1='4.22' y1='19.78' x2='5.64' y2='18.36'/><line x1='18.36' y1='5.64' x2='19.78' y2='4.22'/>",
    moon: "<path d='M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z'/>",
    pin: "<line x1='12' y1='17' x2='12' y2='22'/><path d='M9 2h6l-1 8 3 3v2H7v-2l3-3z'/>",
    x: "<line x1='18' y1='6' x2='6' y2='18'/><line x1='6' y1='6' x2='18' y2='18'/>",
    plus: "<line x1='12' y1='5' x2='12' y2='19'/><line x1='5' y1='12' x2='19' y2='12'/>",
    chevron: "<polyline points='9 18 15 12 9 6'/>",
    grid: "<rect x='3' y='3' width='7' height='7'/><rect x='14' y='3' width='7' height='7'/><rect x='14' y='14' width='7' height='7'/><rect x='3' y='14' width='7' height='7'/>",
    list: "<line x1='8' y1='6' x2='21' y2='6'/><line x1='8' y1='12' x2='21' y2='12'/><line x1='8' y1='18' x2='21' y2='18'/><line x1='3' y1='6' x2='3.01' y2='6'/><line x1='3' y1='12' x2='3.01' y2='12'/><line x1='3' y1='18' x2='3.01' y2='18'/>",
    download: "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><polyline points='7 10 12 15 17 10'/><line x1='12' y1='15' x2='12' y2='3'/>",
    upload: "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><polyline points='17 8 12 3 7 8'/><line x1='12' y1='3' x2='12' y2='15'/>",
    archive: "<polyline points='21 8 21 21 3 21 3 8'/><rect x='1' y='3' width='22' height='5'/><line x1='10' y1='12' x2='14' y2='12'/>",
    pencil: "<path d='M12 20h9'/><path d='M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4Z'/>",
    trash: "<polyline points='3 6 5 6 21 6'/><path d='M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2'/>",
    refresh: "<polyline points='23 4 23 10 17 10'/><polyline points='1 20 1 14 7 14'/><path d='M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15'/>",
    clock: "<circle cx='12' cy='12' r='10'/><polyline points='12 6 12 12 16 14'/>",
    hard: "<line x1='22' y1='12' x2='2' y2='12'/><path d='M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z'/>",
    server: "<rect x='2' y='2' width='20' height='8' rx='2'/><rect x='2' y='14' width='20' height='8' rx='2'/><line x1='6' y1='6' x2='6.01' y2='6'/><line x1='6' y1='18' x2='6.01' y2='18'/>",
    cloud: "<path d='M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z'/>",
    key: "<path d='M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3'/>",
    exchange: "<polyline points='17 1 21 5 17 9'/><path d='M3 11V9a4 4 0 0 1 4-4h14'/><polyline points='7 23 3 19 7 15'/><path d='M21 13v2a4 4 0 0 1-4 4H3'/>",
    lock: "<rect x='3' y='11' width='18' height='11' rx='2'/><path d='M7 11V7a5 5 0 0 1 10 0v4'/>",
    search: "<circle cx='11' cy='11' r='8'/><line x1='21' y1='21' x2='16.65' y2='16.65'/>",
    'search-deep': "<circle cx='11' cy='11' r='7'/><line x1='20' y1='20' x2='16' y2='16'/><line x1='11' y1='8' x2='11' y2='14'/><line x1='8' y1='11' x2='14' y2='11'/>",
    'log-out': "<path d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4'/><polyline points='16 17 21 12 16 7'/><line x1='21' y1='12' x2='9' y2='12'/>",
    // Dieselben Formen wie im MfIconTagHelper - sonst zeigt dasselbe Wort in
    // Razor und im JavaScript zwei verschiedene Symbole.
    save: "<path d='M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z'/><polyline points='17 21 17 13 7 13 7 21'/><polyline points='7 3 7 8 15 8'/>",
    'file-plus': "<path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'/><polyline points='14 2 14 8 20 8'/><line x1='12' y1='12' x2='12' y2='18'/><line x1='9' y1='15' x2='15' y2='15'/>",
    'folder-plus': "<path d='M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z'/><line x1='12' y1='11' x2='12' y2='17'/><line x1='9' y1='14' x2='15' y2='14'/>",
    users: "<path d='M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/>",
    shield: "<path d='M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z'/>",
    settings: "<circle cx='12' cy='12' r='3'/><path d='M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06A1.65 1.65 0 0 0 15 19.4a1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.6 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09A1.65 1.65 0 0 0 15 4.6a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z'/>"
};

/** Erzeugt ein Icon als SVG-Element. */
export function icon(name, size) {
    const path = icons[name] || icons.file;
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');

    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('width', size || 16);
    svg.setAttribute('height', size || 16);
    svg.setAttribute('fill', 'none');
    svg.setAttribute('stroke', 'currentColor');
    svg.setAttribute('stroke-width', '2');
    svg.setAttribute('stroke-linecap', 'round');
    svg.setAttribute('stroke-linejoin', 'round');
    svg.setAttribute('aria-hidden', 'true');
    svg.classList.add('mf-icon');
    svg.innerHTML = path.replace(/'/g, '"');

    return svg;
}

/** Symbolname eines Roots auf ein vorhandenes Icon abbilden. */
export function rootIcon(name) {
    if (name === 'hard-drive') {
        return 'hard';
    }

    return icons[name] ? name : 'folder';
}
