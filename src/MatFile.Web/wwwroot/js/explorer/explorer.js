// MatFile Explorer - Tabs, Navigation, Auswahl und Kontextmenü.
import { api, t, formatSize, formatDate, fileType, icon, rootIcon } from './api.js';

const el = {
    roots: document.getElementById('mfx-roots'),
    bookmarks: document.getElementById('mfx-bookmarks'),
    tabs: document.getElementById('mfx-tabs'),
    breadcrumb: document.getElementById('mfx-breadcrumb'),
    listing: document.getElementById('mfx-listing'),
    status: document.getElementById('mfx-status'),
    menu: document.getElementById('mfx-menu'),
    search: document.getElementById('mfx-search'),
    sidebar: document.getElementById('mfx-sidebar'),
    backdrop: document.getElementById('mfx-backdrop'),
    back: document.getElementById('mfx-back'),
    forward: document.getElementById('mfx-forward'),
    up: document.getElementById('mfx-up'),
    refresh: document.getElementById('mfx-refresh'),
    hidden: document.getElementById('mfx-hidden'),
    view: document.getElementById('mfx-view'),
    sidebarToggle: document.getElementById('mfx-sidebar-toggle')
};

const state = {
    roots: [],
    bookmarks: [],
    settings: { viewMode: 'list', sortKey: 'name', sortDir: 'asc', showHidden: false, sidebarWidth: 260 },
    tabs: [],
    active: 0,
    filter: ''
};

const activeTab = () => state.tabs[state.active];

function createTab(rootId, path, pinned) {
    return {
        rootId,
        rootName: '',
        path: path || '',
        parentPath: null,
        title: '',
        pinned: !!pinned,
        entries: [],
        permissions: null,
        selection: new Set(),
        anchor: null,
        history: [],
        historyIndex: -1,
        loading: false,
        error: null
    };
}

// ---------------------------------------------------------------- Start

async function init() {
    try {
        const [settings, roots, bookmarks, savedTabs] = await Promise.all([
            api.settings(), api.roots(), api.bookmarks(), api.tabs()
        ]);

        state.settings = { ...state.settings, ...settings };
        state.roots = roots;
        state.bookmarks = bookmarks;

        savedTabs.forEach(tab => state.tabs.push(createTab(tab.rootId, tab.path, true)));
    } catch (error) {
        showError(error);
        return;
    }

    if (state.tabs.length === 0 && state.roots.length > 0) {
        state.tabs.push(createTab(state.roots[0].id, ''));
    }

    applySettingsToToolbar();
    renderSidebar();

    if (state.tabs.length === 0) {
        renderTabs();
        el.listing.replaceChildren(message(t('NoRoots')));
        renderStatus();
        return;
    }

    await activate(0, false);
}

function showError(error) {
    el.listing.replaceChildren(message(errorText(error)));
}

function errorText(error) {
    if (error && error.status === 404) {
        return error.code === 'root_not_found' ? t('ErrorNotFound') : t('ErrorNotFound');
    }
    if (error && error.status === 403) {
        return t('ErrorAccess');
    }
    if (error && error.status === 503) {
        return t('ErrorUnavailable');
    }
    return t('Error');
}

// ---------------------------------------------------------------- Tabs

async function activate(index, push) {
    if (index < 0 || index >= state.tabs.length) {
        return;
    }

    state.active = index;
    const tab = activeTab();

    if (tab.entries.length === 0 && tab.historyIndex < 0) {
        await navigate(tab, tab.rootId, tab.path, push !== false);
    } else {
        renderAll();
    }
}

async function openTab(rootId, path, activateNew) {
    state.tabs.push(createTab(rootId, path));
    if (activateNew !== false) {
        await activate(state.tabs.length - 1);
    } else {
        renderTabs();
    }
}

async function closeTab(index) {
    if (state.tabs.length <= 1) {
        return;
    }

    state.tabs.splice(index, 1);
    if (state.active >= state.tabs.length) {
        state.active = state.tabs.length - 1;
    } else if (index < state.active) {
        state.active--;
    }

    persistTabs();
    await activate(state.active);
}

function togglePin(index) {
    state.tabs[index].pinned = !state.tabs[index].pinned;
    renderTabs();
    persistTabs();
}

let persistTimer = null;

function persistTabs() {
    clearTimeout(persistTimer);
    persistTimer = setTimeout(() => {
        const pinned = state.tabs
            .map((tab, index) => ({ tab, index }))
            .filter(item => item.tab.pinned)
            .map((item, order) => ({
                rootId: item.tab.rootId,
                path: item.tab.path,
                title: tabTitle(item.tab),
                sortOrder: order,
                isActive: item.index === state.active
            }));

        api.saveTabs(pinned).catch(() => { /* Nicht kritisch für die Bedienung. */ });
    }, 500);
}

let settingsTimer = null;

function persistSettings() {
    clearTimeout(settingsTimer);
    settingsTimer = setTimeout(() => {
        api.saveSettings(state.settings).catch(() => { });
    }, 400);
}

// ---------------------------------------------------------------- Navigation

async function navigate(tab, rootId, path, push) {
    tab.loading = true;
    tab.error = null;
    renderListing();

    try {
        const data = await api.list(rootId, path);

        tab.rootId = data.rootId;
        tab.rootName = data.rootName;
        tab.path = data.path;
        tab.parentPath = data.parentPath;
        tab.permissions = data.permissions;
        tab.entries = data.entries;
        tab.selection.clear();
        tab.anchor = null;

        if (push !== false) {
            tab.history = tab.history.slice(0, tab.historyIndex + 1);
            tab.history.push({ rootId: data.rootId, path: data.path });
            tab.historyIndex = tab.history.length - 1;
        }
    } catch (error) {
        tab.entries = [];
        tab.error = errorText(error);
    }

    tab.loading = false;
    state.filter = '';
    el.search.value = '';

    renderAll();
    persistTabs();
}

async function goBack() {
    const tab = activeTab();
    if (!tab || tab.historyIndex <= 0) {
        return;
    }

    tab.historyIndex--;
    const target = tab.history[tab.historyIndex];
    await navigate(tab, target.rootId, target.path, false);
}

async function goForward() {
    const tab = activeTab();
    if (!tab || tab.historyIndex >= tab.history.length - 1) {
        return;
    }

    tab.historyIndex++;
    const target = tab.history[tab.historyIndex];
    await navigate(tab, target.rootId, target.path, false);
}

async function goUp() {
    const tab = activeTab();
    if (!tab || tab.parentPath === null || tab.parentPath === undefined) {
        return;
    }

    await navigate(tab, tab.rootId, tab.parentPath);
}

async function openEntry(entry) {
    const tab = activeTab();

    if (entry.isDirectory) {
        await navigate(tab, tab.rootId, entry.path);
        return;
    }

    if (tab.permissions && tab.permissions.canDownload) {
        window.location.href = api.downloadUrl(tab.rootId, entry.path);
    }
}

// ---------------------------------------------------------------- Anzeige

function renderAll() {
    renderTabs();
    renderBreadcrumb();
    renderListing();
    renderStatus();
    renderToolbarState();
    highlightSidebar();
}

function tabTitle(tab) {
    if (tab.path) {
        return tab.path.split('/').pop();
    }

    return tab.rootName || (state.roots.find(r => r.id === tab.rootId) || {}).name || '/';
}

function button(className, iconName, title, onClick) {
    const element = document.createElement('button');
    element.type = 'button';
    element.className = className;
    element.title = title;
    element.setAttribute('aria-label', title);
    element.appendChild(icon(iconName, 14));
    element.addEventListener('click', onClick);
    return element;
}

function message(text) {
    const div = document.createElement('div');
    div.className = 'mfx-message';
    div.textContent = text;
    return div;
}

function renderSidebar() {
    el.roots.replaceChildren();

    if (state.roots.length === 0) {
        const empty = document.createElement('li');
        empty.className = 'mfx-side-empty';
        empty.textContent = t('NoRoots');
        el.roots.appendChild(empty);
    }

    state.roots.forEach(root => {
        const item = document.createElement('li');
        const link = document.createElement('button');
        link.type = 'button';
        link.className = 'mfx-side-item';
        link.dataset.rootId = root.id;
        link.dataset.path = '';
        link.appendChild(icon(rootIcon(root.icon), 16));

        const label = document.createElement('span');
        label.textContent = root.name;
        link.appendChild(label);

        link.addEventListener('click', () => navigate(activeTab(), root.id, ''));
        item.appendChild(link);
        el.roots.appendChild(item);
    });

    renderBookmarks();
    highlightSidebar();
}

function renderBookmarks() {
    el.bookmarks.replaceChildren();

    if (state.bookmarks.length === 0) {
        const empty = document.createElement('li');
        empty.className = 'mfx-side-empty';
        empty.textContent = t('NoBookmarks');
        el.bookmarks.appendChild(empty);
        return;
    }

    state.bookmarks.forEach(bookmark => {
        const item = document.createElement('li');

        const link = document.createElement('button');
        link.type = 'button';
        link.className = 'mfx-side-item';
        link.dataset.rootId = bookmark.rootId;
        link.dataset.path = bookmark.path;
        link.appendChild(icon(rootIcon(bookmark.icon), 16));

        const label = document.createElement('span');
        label.textContent = bookmark.name;
        link.appendChild(label);
        link.addEventListener('click', () => navigate(activeTab(), bookmark.rootId, bookmark.path));

        const remove = button('mfx-side-remove', 'x', t('RemoveBookmark'), async event => {
            event.stopPropagation();
            await api.deleteBookmark(bookmark.id);
            state.bookmarks = state.bookmarks.filter(b => b.id !== bookmark.id);
            renderBookmarks();
        });

        item.appendChild(link);
        item.appendChild(remove);
        el.bookmarks.appendChild(item);
    });
}

function highlightSidebar() {
    const tab = activeTab();

    el.sidebar.querySelectorAll('.mfx-side-item').forEach(item => {
        const matches = tab
            && Number(item.dataset.rootId) === tab.rootId
            && item.dataset.path === tab.path;

        item.classList.toggle('is-active', !!matches);
    });
}

function renderTabs() {
    el.tabs.replaceChildren();

    state.tabs.forEach((tab, index) => {
        const item = document.createElement('div');
        item.className = 'mfx-tab' + (index === state.active ? ' is-active' : '');
        item.setAttribute('role', 'tab');
        item.setAttribute('aria-selected', index === state.active ? 'true' : 'false');

        item.appendChild(icon('folder', 14));

        const label = document.createElement('span');
        label.className = 'mfx-tab-title';
        label.textContent = tabTitle(tab);
        item.appendChild(label);

        const pin = button('mfx-tab-pin', 'pin', tab.pinned ? t('Unpin') : t('Pin'), event => {
            event.stopPropagation();
            togglePin(index);
        });
        pin.classList.toggle('is-pinned', tab.pinned);
        item.appendChild(pin);

        if (state.tabs.length > 1) {
            item.appendChild(button('mfx-tab-close', 'x', t('CloseTab'), event => {
                event.stopPropagation();
                closeTab(index);
            }));
        }

        item.addEventListener('click', () => activate(index));
        item.addEventListener('auxclick', event => {
            if (event.button === 1) {
                event.preventDefault();
                closeTab(index);
            }
        });

        el.tabs.appendChild(item);
    });

    const add = button('mfx-tab-add', 'plus', t('NewTab'), () => {
        const tab = activeTab();
        openTab(tab ? tab.rootId : state.roots[0].id, tab ? tab.path : '');
    });

    el.tabs.appendChild(add);
}

function renderBreadcrumb() {
    el.breadcrumb.replaceChildren();

    const tab = activeTab();
    if (!tab) {
        return;
    }

    const crumb = (text, path) => {
        const link = document.createElement('button');
        link.type = 'button';
        link.className = 'mfx-crumb';
        link.textContent = text;
        link.addEventListener('click', () => navigate(tab, tab.rootId, path));
        return link;
    };

    el.breadcrumb.appendChild(crumb(tab.rootName || tabTitle(tab), ''));

    if (!tab.path) {
        return;
    }

    let current = '';
    tab.path.split('/').forEach(segment => {
        current = current ? `${current}/${segment}` : segment;
        el.breadcrumb.appendChild(icon('chevron', 12));
        el.breadcrumb.appendChild(crumb(segment, current));
    });
}

function visibleEntries(tab) {
    let entries = tab.entries || [];

    if (!state.settings.showHidden) {
        entries = entries.filter(entry => !entry.isHidden);
    }

    if (state.filter) {
        const needle = state.filter.toLowerCase();
        entries = entries.filter(entry => entry.name.toLowerCase().includes(needle));
    }

    const direction = state.settings.sortDir === 'desc' ? -1 : 1;
    const key = state.settings.sortKey;

    return entries.slice().sort((a, b) => {
        if (a.isDirectory !== b.isDirectory) {
            return a.isDirectory ? -1 : 1;
        }

        let result;
        if (key === 'size') {
            result = a.size - b.size;
        } else if (key === 'modified') {
            result = new Date(a.modified) - new Date(b.modified);
        } else {
            result = a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
        }

        return result * direction;
    });
}

function renderListing() {
    const tab = activeTab();
    el.listing.replaceChildren();

    if (!tab) {
        return;
    }

    if (tab.loading) {
        el.listing.appendChild(message(t('Loading')));
        return;
    }

    if (tab.error) {
        el.listing.appendChild(message(tab.error));
        return;
    }

    const entries = visibleEntries(tab);

    if (state.settings.viewMode === 'grid') {
        renderGrid(entries);
    } else {
        renderList(entries);
    }

    if (entries.length === 0) {
        el.listing.appendChild(message(t('EmptyFolder')));
    }
}

function sortHeader(key, label, extraClass) {
    const cell = document.createElement('button');
    cell.type = 'button';
    cell.className = 'mfx-head-cell' + (extraClass ? ' ' + extraClass : '');
    cell.textContent = label;

    if (state.settings.sortKey === key) {
        cell.classList.add('is-active');
        const arrow = document.createElement('span');
        arrow.className = 'mfx-head-arrow';
        arrow.textContent = state.settings.sortDir === 'desc' ? ' ▼' : ' ▲';
        cell.appendChild(arrow);
    }

    cell.addEventListener('click', () => {
        if (state.settings.sortKey === key) {
            state.settings.sortDir = state.settings.sortDir === 'asc' ? 'desc' : 'asc';
        } else {
            state.settings.sortKey = key;
            state.settings.sortDir = 'asc';
        }

        persistSettings();
        renderListing();
    });

    return cell;
}

function renderList(entries) {
    const head = document.createElement('div');
    head.className = 'mfx-list-head';
    head.appendChild(sortHeader('name', t('Name'), 'mfx-cell-name'));
    head.appendChild(sortHeader('size', t('Size'), 'mfx-cell-size'));
    head.appendChild(sortHeader('modified', t('Modified'), 'mfx-cell-date'));

    const type = document.createElement('span');
    type.className = 'mfx-head-cell mfx-cell-type';
    type.textContent = t('Type');
    head.appendChild(type);

    const rows = document.createElement('div');
    rows.className = 'mfx-rows';

    entries.forEach((entry, index) => {
        const row = document.createElement('div');
        row.className = 'mfx-row';
        row.dataset.path = entry.path;
        row.dataset.index = index;
        row.setAttribute('role', 'row');

        const name = document.createElement('span');
        name.className = 'mfx-cell mfx-cell-name';
        name.appendChild(icon(entry.isDirectory ? 'folder' : 'file', 16));

        const label = document.createElement('span');
        label.className = 'mfx-name';
        label.textContent = entry.name;
        name.appendChild(label);
        row.appendChild(name);

        const size = document.createElement('span');
        size.className = 'mfx-cell mfx-cell-size';
        size.textContent = entry.isDirectory ? '' : formatSize(entry.size);
        row.appendChild(size);

        const date = document.createElement('span');
        date.className = 'mfx-cell mfx-cell-date';
        date.textContent = formatDate(entry.modified);
        row.appendChild(date);

        const type = document.createElement('span');
        type.className = 'mfx-cell mfx-cell-type';
        type.textContent = entry.isDirectory ? t('Folder') : fileType(entry.name);
        row.appendChild(type);

        rows.appendChild(row);
    });

    const list = document.createElement('div');
    list.className = 'mfx-list';
    list.appendChild(head);
    list.appendChild(rows);
    el.listing.appendChild(list);

    applySelection();
}

function renderGrid(entries) {
    const grid = document.createElement('div');
    grid.className = 'mfx-grid mfx-rows';

    entries.forEach((entry, index) => {
        const tile = document.createElement('div');
        tile.className = 'mfx-tile mfx-row';
        tile.dataset.path = entry.path;
        tile.dataset.index = index;

        tile.appendChild(icon(entry.isDirectory ? 'folder' : 'file', 34));

        const label = document.createElement('span');
        label.className = 'mfx-name';
        label.textContent = entry.name;
        tile.appendChild(label);

        const meta = document.createElement('span');
        meta.className = 'mfx-tile-meta';
        meta.textContent = entry.isDirectory ? '' : formatSize(entry.size);
        tile.appendChild(meta);

        grid.appendChild(tile);
    });

    el.listing.appendChild(grid);
    applySelection();
}

function renderStatus() {
    const tab = activeTab();
    el.status.replaceChildren();

    if (!tab) {
        return;
    }

    const entries = visibleEntries(tab);
    const parts = [t('Items').replace('{0}', entries.length)];

    if (tab.selection.size > 0) {
        const selectedSize = entries
            .filter(entry => tab.selection.has(entry.path) && !entry.isDirectory)
            .reduce((sum, entry) => sum + entry.size, 0);

        let text = t('Selected').replace('{0}', tab.selection.size);
        if (selectedSize > 0) {
            text += ` · ${formatSize(selectedSize)}`;
        }

        parts.push(text);
    }

    const info = document.createElement('span');
    info.textContent = parts.join('  ·  ');
    el.status.appendChild(info);
}

function renderToolbarState() {
    const tab = activeTab();

    el.back.disabled = !tab || tab.historyIndex <= 0;
    el.forward.disabled = !tab || tab.historyIndex >= tab.history.length - 1;
    el.up.disabled = !tab || tab.parentPath === null || tab.parentPath === undefined;
}

function applySettingsToToolbar() {
    el.hidden.setAttribute('aria-pressed', state.settings.showHidden ? 'true' : 'false');
    el.hidden.classList.toggle('is-active', state.settings.showHidden);

    el.view.replaceChildren(icon(state.settings.viewMode === 'grid' ? 'list' : 'grid', 16));
    el.view.title = state.settings.viewMode === 'grid' ? t('ViewList') : t('ViewGrid');
}

// ---------------------------------------------------------------- Auswahl

function applySelection() {
    const tab = activeTab();
    if (!tab) {
        return;
    }

    el.listing.querySelectorAll('.mfx-row').forEach(row => {
        row.classList.toggle('is-selected', tab.selection.has(row.dataset.path));
    });
}

function selectSingle(path) {
    const tab = activeTab();
    tab.selection.clear();
    tab.selection.add(path);
    tab.anchor = path;
    applySelection();
    renderStatus();
}

function toggleSelection(path) {
    const tab = activeTab();

    if (tab.selection.has(path)) {
        tab.selection.delete(path);
    } else {
        tab.selection.add(path);
        tab.anchor = path;
    }

    applySelection();
    renderStatus();
}

function selectRange(path) {
    const tab = activeTab();
    const entries = visibleEntries(tab);
    const from = entries.findIndex(entry => entry.path === tab.anchor);
    const to = entries.findIndex(entry => entry.path === path);

    if (from < 0 || to < 0) {
        selectSingle(path);
        return;
    }

    tab.selection.clear();
    for (let i = Math.min(from, to); i <= Math.max(from, to); i++) {
        tab.selection.add(entries[i].path);
    }

    applySelection();
    renderStatus();
}

function selectAll() {
    const tab = activeTab();
    visibleEntries(tab).forEach(entry => tab.selection.add(entry.path));
    applySelection();
    renderStatus();
}

function clearSelection() {
    const tab = activeTab();
    if (!tab) {
        return;
    }

    tab.selection.clear();
    applySelection();
    renderStatus();
}

function selectedEntries() {
    const tab = activeTab();
    return tab ? (tab.entries || []).filter(entry => tab.selection.has(entry.path)) : [];
}

// ---------------------------------------------------------------- Kontextmenü

function closeMenu() {
    el.menu.hidden = true;
    el.menu.replaceChildren();
}

function openMenu(x, y, items) {
    el.menu.replaceChildren();

    items.forEach(item => {
        if (item === '-') {
            const separator = document.createElement('li');
            separator.className = 'mfx-menu-separator';
            el.menu.appendChild(separator);
            return;
        }

        const entry = document.createElement('li');
        const action = document.createElement('button');
        action.type = 'button';
        action.className = 'mfx-menu-item' + (item.danger ? ' is-danger' : '');
        action.disabled = !!item.disabled;

        if (item.icon) {
            action.appendChild(icon(item.icon, 14));
        }

        const label = document.createElement('span');
        label.textContent = item.label;
        action.appendChild(label);

        action.addEventListener('click', () => {
            closeMenu();
            item.action();
        });

        entry.appendChild(action);
        el.menu.appendChild(entry);
    });

    el.menu.hidden = false;

    const rect = el.menu.getBoundingClientRect();
    const left = Math.min(x, window.innerWidth - rect.width - 8);
    const top = Math.min(y, window.innerHeight - rect.height - 8);

    el.menu.style.left = `${Math.max(4, left)}px`;
    el.menu.style.top = `${Math.max(4, top)}px`;
}

async function copyPath(entry) {
    const tab = activeTab();
    const value = `${tab.rootName}/${entry ? entry.path : tab.path}`.replace(/\/+$/, '');

    try {
        await navigator.clipboard.writeText(value);
        flash(t('Copied'));
    } catch (ignored) {
        window.prompt(t('CopyPath'), value);
    }
}

function flash(text) {
    const note = document.createElement('div');
    note.className = 'mfx-flash';
    note.textContent = text;
    document.body.appendChild(note);
    setTimeout(() => note.remove(), 2000);
}

function showProperties(entry) {
    const tab = activeTab();
    const dialog = document.createElement('dialog');
    dialog.className = 'mfx-dialog';

    const title = document.createElement('h2');
    title.textContent = entry ? entry.name : tabTitle(tab);
    dialog.appendChild(title);

    const facts = document.createElement('dl');
    facts.className = 'mf-facts';

    const add = (label, value) => {
        const dt = document.createElement('dt');
        dt.textContent = label;
        const dd = document.createElement('dd');
        dd.textContent = value;
        facts.appendChild(dt);
        facts.appendChild(dd);
    };

    add(t('Type'), entry && !entry.isDirectory ? (fileType(entry.name) || t('File')) : t('Folder'));
    add(t('Path'), `${tab.rootName}/${entry ? entry.path : tab.path}`.replace(/\/+$/, ''));

    if (entry && !entry.isDirectory) {
        add(t('Size'), formatSize(entry.size));
    }

    if (entry) {
        add(t('Modified'), formatDate(entry.modified));
    }

    dialog.appendChild(facts);

    const actions = document.createElement('div');
    actions.className = 'mf-actions';

    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'mf-btn mf-btn-primary';
    close.textContent = t('Close');
    close.addEventListener('click', () => dialog.close());
    actions.appendChild(close);
    dialog.appendChild(actions);

    dialog.addEventListener('close', () => dialog.remove());
    document.body.appendChild(dialog);
    dialog.showModal();
}

async function addBookmark(entry) {
    const tab = activeTab();
    const path = entry ? entry.path : tab.path;
    const name = entry ? entry.name : tabTitle(tab);

    const bookmark = await api.addBookmark(tab.rootId, path, name);

    if (!state.bookmarks.some(b => b.id === bookmark.id)) {
        state.bookmarks.push(bookmark);
    }

    renderBookmarks();
    highlightSidebar();
}

function entryMenu(entry) {
    const tab = activeTab();
    const canDownload = tab.permissions && tab.permissions.canDownload;
    const items = [];

    if (entry.isDirectory) {
        items.push({ label: t('Open'), icon: 'folder', action: () => openEntry(entry) });
        items.push({ label: t('NewTab'), icon: 'plus', action: () => openTab(tab.rootId, entry.path) });
        items.push({ label: t('AddBookmark'), icon: 'star', action: () => addBookmark(entry) });
    } else {
        items.push({
            label: t('Download'),
            icon: 'download',
            disabled: !canDownload,
            action: () => openEntry(entry)
        });
    }

    items.push('-');
    items.push({ label: t('CopyPath'), icon: 'file', action: () => copyPath(entry) });
    items.push({ label: t('Properties'), icon: 'settings', action: () => showProperties(entry) });

    return items;
}

function folderMenu() {
    const tab = activeTab();

    return [
        { label: t('Refresh'), icon: 'settings', action: () => navigate(tab, tab.rootId, tab.path, false) },
        { label: t('SelectAll'), icon: 'list', action: () => selectAll() },
        '-',
        { label: t('AddBookmark'), icon: 'star', action: () => addBookmark(null) },
        { label: t('Properties'), icon: 'settings', action: () => showProperties(null) }
    ];
}

// ---------------------------------------------------------------- Ereignisse

function entryFromRow(row) {
    const tab = activeTab();
    return (tab.entries || []).find(entry => entry.path === row.dataset.path);
}

el.listing.addEventListener('mousedown', event => {
    const row = event.target.closest('.mfx-row');

    if (!row) {
        if (event.button === 0 && !event.ctrlKey && !event.shiftKey) {
            clearSelection();
            startRubberBand(event);
        }
        return;
    }

    if (event.button === 2 && activeTab().selection.has(row.dataset.path)) {
        return;
    }

    if (event.shiftKey) {
        selectRange(row.dataset.path);
    } else if (event.ctrlKey || event.metaKey) {
        toggleSelection(row.dataset.path);
    } else if (!activeTab().selection.has(row.dataset.path)) {
        selectSingle(row.dataset.path);
    }
});

el.listing.addEventListener('dblclick', event => {
    const row = event.target.closest('.mfx-row');
    if (row) {
        const entry = entryFromRow(row);
        if (entry) {
            openEntry(entry);
        }
    }
});

el.listing.addEventListener('contextmenu', event => {
    event.preventDefault();

    const row = event.target.closest('.mfx-row');

    if (row) {
        const entry = entryFromRow(row);
        if (!entry) {
            return;
        }

        if (!activeTab().selection.has(entry.path)) {
            selectSingle(entry.path);
        }

        openMenu(event.clientX, event.clientY, entryMenu(entry));
        return;
    }

    openMenu(event.clientX, event.clientY, folderMenu());
});

document.addEventListener('click', event => {
    if (!el.menu.hidden && !event.target.closest('.mfx-menu')) {
        closeMenu();
    }
});

document.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
        closeMenu();
        return;
    }

    if (event.target.matches('input, textarea, select')) {
        return;
    }

    const tab = activeTab();
    if (!tab) {
        return;
    }

    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') {
        event.preventDefault();
        selectAll();
        return;
    }

    if (event.key === 'Backspace') {
        event.preventDefault();
        goUp();
        return;
    }

    if (event.key === 'F5') {
        event.preventDefault();
        navigate(tab, tab.rootId, tab.path, false);
        return;
    }

    if (event.key === 'Enter') {
        const selected = selectedEntries();
        if (selected.length === 1) {
            event.preventDefault();
            openEntry(selected[0]);
        }
        return;
    }

    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        const entries = visibleEntries(tab);
        if (entries.length === 0) {
            return;
        }

        event.preventDefault();
        const current = entries.findIndex(entry => entry.path === tab.anchor);
        const next = event.key === 'ArrowDown'
            ? Math.min(entries.length - 1, current + 1)
            : Math.max(0, current <= 0 ? 0 : current - 1);

        if (event.shiftKey && tab.anchor) {
            selectRange(entries[next].path);
        } else {
            selectSingle(entries[next].path);
        }

        const row = el.listing.querySelector(`.mfx-row[data-path="${CSS.escape(entries[next].path)}"]`);
        if (row) {
            row.scrollIntoView({ block: 'nearest' });
        }
    }
});

// Rahmenauswahl mit gedrückter Maustaste
function startRubberBand(startEvent) {
    const container = el.listing;
    const band = document.createElement('div');
    band.className = 'mfx-band';
    container.appendChild(band);

    const bounds = container.getBoundingClientRect();
    const originX = startEvent.clientX;
    const originY = startEvent.clientY;

    const onMove = moveEvent => {
        const left = Math.min(originX, moveEvent.clientX);
        const top = Math.min(originY, moveEvent.clientY);
        const width = Math.abs(moveEvent.clientX - originX);
        const height = Math.abs(moveEvent.clientY - originY);

        band.style.left = `${left - bounds.left + container.scrollLeft}px`;
        band.style.top = `${top - bounds.top + container.scrollTop}px`;
        band.style.width = `${width}px`;
        band.style.height = `${height}px`;

        const tab = activeTab();
        tab.selection.clear();

        container.querySelectorAll('.mfx-row').forEach(row => {
            const rect = row.getBoundingClientRect();
            const hit = rect.right > left && rect.left < left + width
                && rect.bottom > top && rect.top < top + height;

            if (hit) {
                tab.selection.add(row.dataset.path);
            }
        });

        applySelection();
        renderStatus();
    };

    const onUp = () => {
        band.remove();
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
    };

    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
}

el.back.addEventListener('click', goBack);
el.forward.addEventListener('click', goForward);
el.up.addEventListener('click', goUp);
el.refresh.addEventListener('click', () => {
    const tab = activeTab();
    navigate(tab, tab.rootId, tab.path, false);
});

el.search.addEventListener('input', () => {
    state.filter = el.search.value.trim();
    renderListing();
    renderStatus();
});

el.hidden.addEventListener('click', () => {
    state.settings.showHidden = !state.settings.showHidden;
    applySettingsToToolbar();
    persistSettings();
    renderListing();
    renderStatus();
});

el.view.addEventListener('click', () => {
    state.settings.viewMode = state.settings.viewMode === 'grid' ? 'list' : 'grid';
    applySettingsToToolbar();
    persistSettings();
    renderListing();
});

el.sidebarToggle.addEventListener('click', () => {
    const open = el.sidebar.classList.toggle('is-open');
    el.backdrop.hidden = !open;
});

el.backdrop.addEventListener('click', () => {
    el.sidebar.classList.remove('is-open');
    el.backdrop.hidden = true;
});

init();
