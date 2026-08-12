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

export const api = {
    roots: () => request('/api/fs/roots'),

    list: (rootId, path) =>
        request(`/api/fs/list?rootId=${encodeURIComponent(rootId)}&path=${encodeURIComponent(path || '')}`),

    downloadUrl: (rootId, path) =>
        `/api/fs/download?rootId=${encodeURIComponent(rootId)}&path=${encodeURIComponent(path || '')}`,

    settings: () => request('/api/settings'),
    saveSettings: settings => send('/api/settings', 'PUT', settings),

    bookmarks: () => request('/api/bookmarks'),
    addBookmark: (rootId, path, name) => send('/api/bookmarks', 'POST', { rootId, path, name }),
    deleteBookmark: id => request(`/api/bookmarks/${id}`, { method: 'DELETE' }),

    tabs: () => request('/api/tabs'),
    saveTabs: tabs => send('/api/tabs', 'PUT', tabs)
};

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
    star: "<polygon points='12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2'/>",
    pin: "<line x1='12' y1='17' x2='12' y2='22'/><path d='M9 2h6l-1 8 3 3v2H7v-2l3-3z'/>",
    x: "<line x1='18' y1='6' x2='6' y2='18'/><line x1='6' y1='6' x2='18' y2='18'/>",
    plus: "<line x1='12' y1='5' x2='12' y2='19'/><line x1='5' y1='12' x2='19' y2='12'/>",
    chevron: "<polyline points='9 18 15 12 9 6'/>",
    grid: "<rect x='3' y='3' width='7' height='7'/><rect x='14' y='3' width='7' height='7'/><rect x='14' y='14' width='7' height='7'/><rect x='3' y='14' width='7' height='7'/>",
    list: "<line x1='8' y1='6' x2='21' y2='6'/><line x1='8' y1='12' x2='21' y2='12'/><line x1='8' y1='18' x2='21' y2='18'/><line x1='3' y1='6' x2='3.01' y2='6'/><line x1='3' y1='12' x2='3.01' y2='12'/><line x1='3' y1='18' x2='3.01' y2='18'/>",
    download: "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><polyline points='7 10 12 15 17 10'/><line x1='12' y1='15' x2='12' y2='3'/>",
    hard: "<line x1='22' y1='12' x2='2' y2='12'/><path d='M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z'/>",
    lock: "<rect x='3' y='11' width='18' height='11' rx='2'/><path d='M7 11V7a5 5 0 0 1 10 0v4'/>",
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
