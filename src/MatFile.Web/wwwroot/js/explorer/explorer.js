// MatFile Explorer - Tabs, Navigation, Auswahl, Kontextmenü und Dateioperationen.
import { api, t, formatSize, formatDate, fileType, icon, rootIcon, errorKey } from './api.js';
import { askName, askFileName, askConfirm, showFacts, flash } from './dialogs.js';
import { uploadFiles, trackJob, onTransfersFinished } from './transfers.js';
import { openPreview, isPreviewOpen } from './preview.js';
import { openRootDialog, deleteRoot } from './rootdialog.js';
import { newSearch, startSearch, cancelSearch, disposeSearch, isRunning } from './search.js';
import { openUsers, openRoles, openLocations } from './manage.js';

const can = window.matfileCan || {};

const el = {
    roots: document.getElementById('mfx-roots'),
    bookmarks: document.getElementById('mfx-bookmarks'),
    bookmarksGroup: document.getElementById('mfx-bookmarks-group'),
    sideFirst: document.getElementById('mfx-side-first'),
    rootsGroup: document.getElementById('mfx-roots-group'),
    connections: document.getElementById('mfx-connections'),
    connectionsGroup: document.getElementById('mfx-connections-group'),
    tabs: document.getElementById('mfx-tabs'),
    breadcrumb: document.getElementById('mfx-breadcrumb'),
    listing: document.getElementById('mfx-listing'),
    status: document.getElementById('mfx-status'),
    menu: document.getElementById('mfx-menu'),
    search: document.getElementById('mfx-search'),
    searchGo: document.getElementById('mfx-search-go'),
    sidebar: document.getElementById('mfx-sidebar'),
    backdrop: document.getElementById('mfx-backdrop'),
    back: document.getElementById('mfx-back'),
    forward: document.getElementById('mfx-forward'),
    up: document.getElementById('mfx-up'),
    refresh: document.getElementById('mfx-refresh'),
    upload: document.getElementById('mfx-upload'),
    newFolder: document.getElementById('mfx-newfolder'),
    newFile: document.getElementById('mfx-newfile'),
    hidden: document.getElementById('mfx-hidden'),
    view: document.getElementById('mfx-view'),
    details: document.getElementById('mfx-details'),
    detailsToggle: document.getElementById('mfx-details-toggle'),
    more: document.getElementById('mfx-more'),
    sidebarToggle: document.getElementById('mfx-sidebar-toggle')
};

const state = {
    roots: [],
    bookmarks: [],
    settings: {
        viewMode: 'list', sortKey: 'name', sortDir: 'asc',
        showHidden: false, showDetails: false, showSidebar: true, sidebarWidth: 260
    },
    tabs: [],
    active: 0,
    filter: '',
    clipboard: null
};

const activeTab = () => state.tabs[state.active];

function createTab(rootId, path, pinned) {
    return {
        kind: 'folder',
        search: null,
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
    // Ohne das Recht am Explorer bleibt die Fläche leer - die Menüs zur
    // Verwaltung stehen trotzdem, falls die Rolle sie erlaubt.
    if (can.useExplorer === false) {
        el.listing.replaceChildren(message(t('NoExplorerAccess')));
        return;
    }

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
        el.listing.replaceChildren(message(noRootsText()));
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

    // Ein Suchtab holt nichts nach - seine Treffer stehen schon im Zustand.
    if (tab.kind === 'search') {
        renderAll();
        return;
    }

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

    if (state.tabs[index].search) {
        disposeSearch(state.tabs[index].search);
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
        // Suchtabs sind flüchtig und werden nicht gemerkt.
        const pinned = state.tabs
            .map((tab, index) => ({ tab, index }))
            .filter(item => item.tab.pinned && item.tab.kind !== 'search')
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

    if (!permissions().canDownload) {
        flash(t('ErrorAccessDenied'), true);
        return;
    }

    // Anzeigbare Dateien öffnen die Vorschau, alles andere lädt herunter.
    if (entry.preview && entry.preview !== 'none') {
        showPreview(tab, entry, false);
        return;
    }

    window.location.href = api.downloadUrl(tab.rootId, entry.path);
}

/** Vorschau mit den Rechten des Speicherorts öffnen - auf Wunsch gleich im Editor. */
function showPreview(tab, entry, edit) {
    const rights = permissions();

    openPreview(tab.rootId, visibleEntries(tab), entry.path, {
        canDownload: rights.canDownload,
        canWrite: rights.canWrite,
        edit,
        onSaved: () => reload()
    });
}

// ---------------------------------------------------------------- Anzeige

function renderAll() {
    renderTabs();
    renderBreadcrumb();
    renderListing();
    renderStatus();
    renderDetails();
    renderToolbarState();
    syncSearchField();
    highlightSidebar();
}

/** Das Suchfeld filtert im Ordnertab und trägt im Suchtab den Suchbegriff. */
function syncSearchField() {
    const tab = activeTab();

    if (tab && tab.kind === 'search') {
        el.search.value = tab.search.term;
        el.search.placeholder = t('SearchTerm');
        return;
    }

    el.search.value = state.filter;
    el.search.placeholder = t('SearchPlaceholder');
}

function tabTitle(tab) {
    if (tab.kind === 'search') {
        return tab.search.term ? `${t('SearchTab')}: ${tab.search.term}` : t('SearchTab');
    }

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

/** Jeder Speichertyp hat sein Symbol - dasselbe wie auf den Kacheln im Dialog. */
function iconForRoot(root) {
    if (root.icon) {
        return rootIcon(root.icon);
    }

    switch (root.type) {
        case 'Smb': return 'server';
        case 'Sftp': return 'key';
        case 'Ftp': return 'exchange';
        case 'WebDav': return 'cloud';
        case 'Nfs': return 'hard';
        default: return 'folder';
    }
}

function rootItem(root, withStatus) {
    const item = document.createElement('li');

    const link = document.createElement('button');
    link.type = 'button';
    link.className = 'mfx-side-item';
    link.dataset.rootId = root.id;
    link.dataset.path = '';
    // Der Typ färbt das Symbol - dieselbe Farbe wie im Dialog.
    link.dataset.type = root.type;
    link.title = `${root.name} · ${root.type}`;

    const symbol = document.createElement('span');
    symbol.className = 'mfx-side-icon';
    symbol.appendChild(icon(iconForRoot(root), 16));
    link.appendChild(symbol);

    const label = document.createElement('span');
    label.className = 'mfx-side-label';
    label.textContent = root.name;
    link.appendChild(label);

    if (withStatus) {
        const dot = document.createElement('span');
        dot.className = 'mfx-side-dot';
        dot.dataset.rootId = root.id;
        dot.title = root.type;
        link.appendChild(dot);
    }

    link.addEventListener('click', () => openRoot(root.id));

    link.addEventListener('contextmenu', event => {
        event.preventDefault();
        event.stopPropagation();

        const items = [
            { label: t('Open'), icon: 'folder', action: () => openRoot(root.id) },
            { label: t('OpenInNewTab'), icon: 'plus', action: () => openTab(root.id, '') }
        ];

        if (can.manageRoots) {
            items.push('-');
            items.push({ label: t('EditLocation'), icon: 'pencil', action: () => editLocation(root.id) });
            items.push({
                label: t('RemoveLocation'), icon: 'trash', danger: true,
                action: () => removeLocation(root)
            });
        }

        openMenu(event.clientX, event.clientY, items);
    });

    item.appendChild(link);
    return item;
}

// ---------------------------------------------------------------- Speicherorte pflegen

/** Speicherorte neu laden und Seitenleiste auffrischen. */
async function reloadRoots() {
    state.roots = await api.roots();
    renderSidebar();

    if (state.roots.length === 0) {
        el.listing.replaceChildren(message(noRootsText()));
        renderStatus();
        return;
    }

    // Beim ersten Speicherort gibt es noch keinen Tab.
    if (state.tabs.length === 0) {
        state.tabs.push(createTab(state.roots[0].id, ''));
        state.active = 0;
        await activate(0, false);
        return;
    }

    const tab = activeTab();

    // Ein Suchtab hängt an keinem Ordner - er bleibt, wie er ist.
    if (tab.kind === 'search') {
        renderAll();
        return;
    }

    if (state.roots.some(root => root.id === tab.rootId)) {
        await reload();
    } else {
        await navigate(tab, state.roots[0].id, '');
    }
}

/** Speicherort öffnen - im aktuellen Tab, aus einem Suchtab heraus in einem neuen. */
async function openRoot(rootId) {
    const tab = activeTab();

    if (!tab || tab.kind === 'search') {
        await openTab(rootId, '');
        return;
    }

    await navigate(tab, rootId, '');
}

/** Hinweis bei leerer Seitenleiste - mit Recht gibt es einen Weg vorwärts. */
function noRootsText() {
    return can.manageRoots ? t('NoRootsAdmin') : t('NoRoots');
}

async function addLocation(preferRemote) {
    if (await openRootDialog(null, preferRemote)) {
        await reloadRoots();
        flash(t('LocationSaved'));
    }
}

async function editLocation(rootId) {
    try {
        const detail = await api.root(rootId);

        if (await openRootDialog(detail, false)) {
            await reloadRoots();
            flash(t('LocationSaved'));
        }
    } catch (error) {
        handleError(error);
    }
}

async function removeLocation(root) {
    const confirmed = await askConfirm(
        t('RemoveLocation'),
        t('RemoveLocationQuestion').replace('{0}', root.name),
        t('RemoveLocation'),
        true);

    if (!confirmed) {
        return;
    }

    if (await deleteRoot(root.id)) {
        await reloadRoots();
        flash(t('LocationRemoved'));
    }
}

/** Kleines Pluszeichen in der Kopfzeile eines Abschnitts. */
function sectionAdd(group, label, preferRemote) {
    if (!can.manageRoots || !group) {
        return;
    }

    const heading = group.querySelector('h2');

    if (!heading || heading.querySelector('.mfx-side-add')) {
        return;
    }

    const add = document.createElement('button');
    add.type = 'button';
    add.className = 'mfx-side-add';
    add.title = label;
    add.setAttribute('aria-label', label);
    add.appendChild(icon('plus', 14));
    add.addEventListener('click', () => addLocation(preferRemote));

    heading.appendChild(add);
}

function renderSidebar() {
    el.roots.replaceChildren();

    // Ordner und Verbindungen stehen in einer Liste; der Typ steckt im Symbol,
    // der Punkt daneben zeigt die Erreichbarkeit einer Gegenstelle.
    const remote = state.roots.filter(root => root.type !== 'Local');

    state.roots.forEach(root => el.roots.appendChild(rootItem(root, root.type !== 'Local')));

    // Ein Abschnitt ohne Einträge verschwindet ganz - samt Überschrift.
    el.rootsGroup.hidden = state.roots.length === 0;

    if (el.sideFirst) {
        el.sideFirst.hidden = state.roots.length > 0;
    }

    sectionAdd(el.rootsGroup, t('AddLocation'), false);

    renderBookmarks();
    highlightSidebar();
    refreshConnectionStatus(remote);
}

/** Erreichbarkeit der Freigaben nachladen und als Punkt anzeigen. */
function refreshConnectionStatus(remote) {
    remote.forEach(root => {
        api.status(root.id).then(result => {
            const dot = el.connections.querySelector(`.mfx-side-dot[data-root-id="${root.id}"]`);
            if (dot) {
                dot.classList.add(result.available ? 'is-online' : 'is-offline');
                dot.title = `${root.type} · ${result.available ? t('Online') : t('Offline')}`;
            }
        }).catch(() => { });
    });
}

function renderBookmarks() {
    el.bookmarks.replaceChildren();

    // Keine Favoriten, kein Favoritenblock.
    el.bookmarksGroup.hidden = state.bookmarks.length === 0;

    if (state.bookmarks.length === 0) {
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

        link.addEventListener('contextmenu', event => {
            event.preventDefault();
            event.stopPropagation();

            openMenu(event.clientX, event.clientY, [
                {
                    label: t('Open'), icon: 'folder',
                    action: () => navigate(activeTab(), bookmark.rootId, bookmark.path)
                },
                {
                    label: t('OpenInNewTab'), icon: 'plus',
                    action: () => openTab(bookmark.rootId, bookmark.path)
                },
                '-',
                {
                    label: t('RemoveBookmark'), icon: 'trash', danger: true,
                    action: async () => {
                        await api.deleteBookmark(bookmark.id);
                        state.bookmarks = state.bookmarks.filter(b => b.id !== bookmark.id);
                        renderBookmarks();
                    }
                }
            ]);
        });

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
            && tab.kind !== 'search'
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

        item.appendChild(icon(tab.kind === 'search' ? 'search-deep' : 'folder', 14));

        const label = document.createElement('span');
        label.className = 'mfx-tab-title';
        label.textContent = tabTitle(tab);
        item.appendChild(label);

        // Suchtabs lassen sich nicht anheften - sie werden nicht gemerkt.
        if (tab.kind !== 'search') {
            const pin = button('mfx-tab-pin', 'pin', tab.pinned ? t('Unpin') : t('Pin'), event => {
                event.stopPropagation();
                togglePin(index);
            });
            pin.classList.toggle('is-pinned', tab.pinned);
            item.appendChild(pin);
        }

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

    // Das Pluszeichen öffnet einen gewöhnlichen Tab; der Rechtsklick fragt nach der Art.
    const add = button('mfx-tab-add', 'plus', t('NewTab'), () => newTab());

    add.addEventListener('contextmenu', event => {
        event.preventDefault();
        event.stopPropagation();

        const rect = add.getBoundingClientRect();
        openMenu(rect.left, rect.bottom + 4, tabKindMenu());
    });

    el.tabs.appendChild(add);
}

function renderBreadcrumb() {
    el.breadcrumb.replaceChildren();

    const tab = activeTab();
    if (!tab) {
        return;
    }

    if (tab.kind === 'search') {
        const label = document.createElement('span');
        label.className = 'mfx-crumb-static';
        label.textContent = t('SearchTab');
        el.breadcrumb.appendChild(icon('search-deep', 14));
        el.breadcrumb.appendChild(label);
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

    if (tab && tab.kind === 'search') {
        renderSearchView(tab);
        return;
    }

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
    const tab = activeTab();

    const head = document.createElement('div');
    head.className = 'mfx-list-head';

    const selectAllCell = document.createElement('span');
    selectAllCell.className = 'mfx-head-cell mfx-cell-check';
    const selectAllBox = document.createElement('input');
    selectAllBox.type = 'checkbox';
    selectAllBox.setAttribute('aria-label', t('SelectAll'));
    selectAllBox.checked = entries.length > 0 && entries.every(entry => tab.selection.has(entry.path));
    selectAllBox.addEventListener('change', () => {
        if (selectAllBox.checked) {
            selectAll();
        } else {
            clearSelection();
        }
    });
    selectAllCell.appendChild(selectAllBox);
    head.appendChild(selectAllCell);

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

        const check = document.createElement('span');
        check.className = 'mfx-cell mfx-cell-check';
        const box = document.createElement('input');
        box.type = 'checkbox';
        box.checked = tab.selection.has(entry.path);
        box.setAttribute('aria-label', entry.name);
        box.addEventListener('mousedown', event => event.stopPropagation());
        box.addEventListener('change', () => toggleSelection(entry.path));
        check.appendChild(box);
        row.appendChild(check);

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

    if (tab.kind === 'search') {
        const info = document.createElement('span');
        info.textContent = tab.search.state === 'idle'
            ? t('SearchNothingYet')
            : t('SearchHits').replace('{0}', tab.search.total.toLocaleString());
        el.status.appendChild(info);

        if (tab.search.truncated) {
            const limit = document.createElement('span');
            limit.textContent = t('SearchTruncated');
            el.status.appendChild(limit);
        }

        return;
    }

    const entries = visibleEntries(tab);

    if (tab.selection.size > 0) {
        const selectedSize = entries
            .filter(entry => tab.selection.has(entry.path) && !entry.isDirectory)
            .reduce((sum, entry) => sum + entry.size, 0);

        const selection = document.createElement('span');
        selection.className = 'mfx-status-selection';
        selection.textContent = selectedSize > 0
            ? t('SelectedWithSize').replace('{0}', tab.selection.size).replace('{1}', formatSize(selectedSize))
            : t('Selected').replace('{0}', tab.selection.size);

        el.status.appendChild(selection);
    }

    const info = document.createElement('span');
    info.textContent = t('Items').replace('{0}', entries.length);
    el.status.appendChild(info);

    if (state.clipboard) {
        const clip = document.createElement('span');
        clip.className = 'mfx-status-clip';
        clip.textContent = (state.clipboard.move ? t('CutDone') : t('CopyDone'))
            .replace('{0}', state.clipboard.paths.length);
        el.status.appendChild(clip);
    }
}

// ---------------------------------------------------------------- Detailbereich

let detailsTab = 'details';

function renderDetails() {
    const tab = activeTab();

    // Im Suchtab gibt es keine Auswahl, zu der ein Detailbereich passen würde.
    const show = state.settings.showDetails && (!tab || tab.kind !== 'search');
    el.details.hidden = !show;

    if (!show) {
        return;
    }
    el.details.replaceChildren();

    if (!tab) {
        return;
    }

    const selected = selectedEntries();

    if (selected.length !== 1) {
        const empty = document.createElement('div');
        empty.className = 'mfx-details-empty';
        empty.textContent = selected.length === 0
            ? t('NoSelection')
            : t('Selected').replace('{0}', selected.length);
        el.details.appendChild(empty);
        return;
    }

    const entry = selected[0];

    // Vorschaubild oder großes Symbol
    const figure = document.createElement('div');
    figure.className = 'mfx-details-figure';

    if (entry.preview === 'image' && permissions().canDownload) {
        const image = document.createElement('img');
        image.src = api.previewUrl(tab.rootId, entry.path);
        image.alt = entry.name;
        figure.appendChild(image);
    } else {
        figure.appendChild(icon(entry.isDirectory ? 'folder' : 'file', 48));
    }

    el.details.appendChild(figure);

    const name = document.createElement('div');
    name.className = 'mfx-details-name';
    name.textContent = entry.name;
    el.details.appendChild(name);

    const meta = document.createElement('div');
    meta.className = 'mfx-details-meta';
    meta.textContent = entry.isDirectory
        ? t('Folder')
        : `${fileType(entry.name) || t('File')} · ${formatSize(entry.size)}`;
    el.details.appendChild(meta);

    // Reiter Details / Aktivität
    const tabs = document.createElement('div');
    tabs.className = 'mfx-details-tabs';

    [['details', t('Details')], ['activity', t('Activity')]].forEach(([key, label]) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'mfx-details-tab' + (detailsTab === key ? ' is-active' : '');
        button.textContent = label;
        button.addEventListener('click', () => {
            detailsTab = key;
            renderDetails();
        });
        tabs.appendChild(button);
    });

    el.details.appendChild(tabs);

    const body = document.createElement('div');
    body.className = 'mfx-details-body';
    el.details.appendChild(body);

    if (detailsTab === 'activity') {
        body.textContent = t('Loading');

        api.activity(tab.rootId, entry.path).then(entries => {
            body.replaceChildren();

            if (entries.length === 0) {
                body.textContent = t('NoActivity');
                return;
            }

            const list = document.createElement('ul');
            list.className = 'mfx-activity';

            entries.forEach(item => {
                const row = document.createElement('li');

                const action = document.createElement('span');
                action.className = 'mfx-activity-action';
                action.textContent = t('Action' + item.action, item.action);
                row.appendChild(action);

                const when = document.createElement('span');
                when.className = 'mfx-activity-date';
                when.textContent = `${formatDate(item.date)}${item.user ? ' · ' + item.user : ''}`;
                row.appendChild(when);

                list.appendChild(row);
            });

            body.appendChild(list);
        }).catch(() => {
            body.textContent = t('Error');
        });

        return;
    }

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

    add(t('Path'), `${tab.rootName}/${entry.path}`);
    add(t('Type'), entry.isDirectory ? t('Folder') : (fileType(entry.name) || t('File')));

    if (!entry.isDirectory) {
        add(t('Size'), formatSize(entry.size));
    }

    add(t('Modified'), formatDate(entry.modified));

    body.appendChild(facts);
}

function renderToolbarState() {
    const tab = activeTab();
    const isSearch = !!tab && tab.kind === 'search';

    el.back.disabled = isSearch || !tab || tab.historyIndex <= 0;
    el.forward.disabled = isSearch || !tab || tab.historyIndex >= tab.history.length - 1;
    el.up.disabled = isSearch || !tab || tab.parentPath === null || tab.parentPath === undefined;

    const rights = permissions();
    el.upload.disabled = isSearch || !rights.canUpload;
    el.newFolder.disabled = isSearch || !rights.canWrite;
    el.newFile.disabled = isSearch || !rights.canWrite;
    el.refresh.disabled = false;
    el.view.disabled = isSearch;
}

/** Seitenleiste ein- oder ausblenden - die Wahl bleibt am Benutzer. */
function toggleSidebar() {
    state.settings.showSidebar = !state.settings.showSidebar;
    applySidebarVisibility();
    persistSettings();
}

function applySidebarVisibility() {
    document.getElementById('mfx').classList.toggle('is-sidebar-hidden', state.settings.showSidebar === false);
}

function applySettingsToToolbar() {
    applySidebarVisibility();

    el.hidden.setAttribute('aria-pressed', state.settings.showHidden ? 'true' : 'false');
    el.hidden.classList.toggle('is-active', state.settings.showHidden);

    el.view.replaceChildren(icon(state.settings.viewMode === 'grid' ? 'list' : 'grid', 16));
    el.view.title = state.settings.viewMode === 'grid' ? t('ViewList') : t('ViewGrid');

    el.detailsToggle.setAttribute('aria-pressed', state.settings.showDetails ? 'true' : 'false');
    el.detailsToggle.classList.toggle('is-active', state.settings.showDetails);
}

// ---------------------------------------------------------------- Auswahl

/** Auswahl hat sich geändert: Markierung, Statuszeile und Detailbereich nachziehen. */
function selectionChanged() {
    applySelection();
    renderStatus();
    renderDetails();
}

function applySelection() {
    const tab = activeTab();
    if (!tab) {
        return;
    }

    el.listing.querySelectorAll('.mfx-row').forEach(row => {
        const selected = tab.selection.has(row.dataset.path);
        row.classList.toggle('is-selected', selected);

        const box = row.querySelector('.mfx-cell-check input');
        if (box) {
            box.checked = selected;
        }
    });

    const all = el.listing.querySelector('.mfx-list-head .mfx-cell-check input');
    if (all) {
        const entries = visibleEntries(tab);
        all.checked = entries.length > 0 && entries.every(entry => tab.selection.has(entry.path));
    }
}

function selectSingle(path) {
    const tab = activeTab();
    tab.selection.clear();
    tab.selection.add(path);
    tab.anchor = path;
    selectionChanged();
}

function toggleSelection(path) {
    const tab = activeTab();

    if (tab.selection.has(path)) {
        tab.selection.delete(path);
    } else {
        tab.selection.add(path);
        tab.anchor = path;
    }

    selectionChanged();
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

    selectionChanged();
}

function selectAll() {
    const tab = activeTab();
    visibleEntries(tab).forEach(entry => tab.selection.add(entry.path));
    selectionChanged();
}

function clearSelection() {
    const tab = activeTab();
    if (!tab) {
        return;
    }

    tab.selection.clear();
    selectionChanged();
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

function showProperties(entry) {
    const tab = activeTab();
    const facts = [
        [t('Type'), entry && !entry.isDirectory ? (fileType(entry.name) || t('File')) : t('Folder')],
        [t('Path'), `${tab.rootName}/${entry ? entry.path : tab.path}`.replace(/\/+$/, '')]
    ];

    if (entry && !entry.isDirectory) {
        facts.push([t('Size'), formatSize(entry.size)]);
    }

    if (entry) {
        facts.push([t('Modified'), formatDate(entry.modified)]);
    }

    showFacts(entry ? entry.name : tabTitle(tab), facts);
}

// ---------------------------------------------------------------- Dateioperationen

function permissions() {
    const tab = activeTab();
    return (tab && tab.permissions) || {};
}

async function reload() {
    const tab = activeTab();
    if (!tab) {
        return;
    }

    // Im Suchtab heißt aktualisieren: noch einmal suchen.
    if (tab.kind === 'search') {
        if (tab.search.term.trim().length >= 2) {
            runSearch(tab);
        }

        return;
    }

    await navigate(tab, tab.rootId, tab.path, false);
}

function handleError(error) {
    flash(t(errorKey(error && error.code), t('Error')), true);
}

async function newFolder() {
    const tab = activeTab();
    const name = await askName(t('NewFolder'), t('Name'), t('NewFolderName'));
    if (!name) {
        return;
    }

    try {
        await api.createFolder(tab.rootId, tab.path, name);
        await reload();
    } catch (error) {
        handleError(error);
    }
}

/** Legt eine leere Datei an und öffnet sie gleich im Editor, wenn es Text ist. */
async function newFile() {
    const tab = activeTab();
    const name = await askFileName(t('NewFile'), t('Name'), t('NewFileName'));

    if (!name) {
        return;
    }

    try {
        const entry = await api.createFile(tab.rootId, tab.path, name);
        await reload();

        const fresh = (activeTab().entries || []).find(item => item.path === entry.path);

        if (fresh) {
            selectSingle(fresh.path);

            if (fresh.preview === 'text' && permissions().canWrite) {
                showPreview(activeTab(), fresh, true);
            }
        }
    } catch (error) {
        handleError(error);
    }
}

async function renameEntry(entry) {
    const tab = activeTab();
    const name = await askName(t('Rename'), t('Name'), entry.name);
    if (!name || name === entry.name) {
        return;
    }

    try {
        await api.rename(tab.rootId, entry.path, name);
        await reload();
    } catch (error) {
        handleError(error);
    }
}

async function deleteSelected() {
    const tab = activeTab();
    const entries = selectedEntries();
    if (entries.length === 0) {
        return;
    }

    const message = entries.length === 1
        ? t('DeleteQuestionOne').replace('{0}', entries[0].name)
        : t('DeleteQuestionMany').replace('{0}', entries.length);

    if (!await askConfirm(t('Delete'), message, t('Delete'), true)) {
        return;
    }

    try {
        trackJob(await api.remove(tab.rootId, entries.map(e => e.path)));
    } catch (error) {
        handleError(error);
    }
}

function setClipboard(move) {
    const tab = activeTab();
    const entries = selectedEntries();
    if (entries.length === 0) {
        return;
    }

    state.clipboard = { rootId: tab.rootId, paths: entries.map(e => e.path), move };
    flash(move ? t('CutDone').replace('{0}', entries.length) : t('CopyDone').replace('{0}', entries.length));
    renderListing();
}

async function paste() {
    const tab = activeTab();
    if (!state.clipboard) {
        return;
    }

    try {
        const job = await api.transfer(
            state.clipboard.rootId, state.clipboard.paths, tab.rootId, tab.path, state.clipboard.move, false);

        trackJob(job);

        if (state.clipboard.move) {
            state.clipboard = null;
        }
    } catch (error) {
        handleError(error);
    }
}

async function compressSelected() {
    const tab = activeTab();
    const entries = selectedEntries();
    if (entries.length === 0) {
        return;
    }

    const suggestion = entries.length === 1 ? entries[0].name.replace(/\.[^.]+$/, '') : tabTitle(tab);
    const name = await askName(t('Compress'), t('Name'), `${suggestion}.zip`);
    if (!name) {
        return;
    }

    try {
        trackJob(await api.compress(tab.rootId, entries.map(e => e.path), tab.path, name));
    } catch (error) {
        handleError(error);
    }
}

async function extractEntry(entry) {
    const tab = activeTab();

    try {
        trackJob(await api.extract(tab.rootId, entry.path, tab.path));
    } catch (error) {
        handleError(error);
    }
}

function downloadZip() {
    const tab = activeTab();
    const entries = selectedEntries();
    if (entries.length === 0) {
        return;
    }

    window.location.href = api.zipUrl(tab.rootId, entries.map(e => e.path));
}

function chooseUpload() {
    const tab = activeTab();
    const input = document.createElement('input');
    input.type = 'file';
    input.multiple = true;
    input.addEventListener('change', () => {
        if (input.files.length > 0) {
            uploadFiles([...input.files], tab.rootId, tab.path);
        }
    });
    input.click();
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

// ---------------------------------------------------------------- Erweiterte Suche

/** Neuer Ordnertab - beim aktuellen Ordner, aus einem Suchtab heraus beim Speicherort. */
function newTab() {
    const tab = activeTab();

    if (!tab) {
        if (state.roots.length === 0) {
            flash(noRootsText(), true);
            return;
        }

        openTab(state.roots[0].id, '');
        return;
    }

    openTab(tab.rootId, tab.kind === 'search' ? '' : tab.path);
}

/**
 * Auswahl der Tabarten. Kommen später weitere dazu, stehen sie hier -
 * das Pluszeichen selbst bleibt der schnelle Weg zum Ordnertab.
 */
function tabKindMenu() {
    return [
        { label: t('NewTab'), icon: 'folder', action: () => newTab() },
        { label: t('SearchTab'), icon: 'search-deep', action: () => openSearchTab('', false) }
    ];
}

/** Ein Tab, der nicht einen Ordner zeigt, sondern eine Suche. */
function createSearchTab(rootId, path, term) {
    const tab = createTab(rootId, path, false);

    tab.kind = 'search';
    tab.search = newSearch(rootId, path);
    tab.search.term = term || '';
    tab.search.hidden = state.settings.showHidden;

    return tab;
}

/** Öffnet die erweiterte Suche in einem neuen Tab - der Bereich ist der aktuelle Ordner. */
async function openSearchTab(term, startNow) {
    const current = activeTab();
    const rootId = current ? current.rootId : (state.roots[0] || {}).id;

    if (rootId === undefined || rootId === null) {
        flash(noRootsText(), true);
        return;
    }

    const tab = createSearchTab(rootId, current && current.kind !== 'search' ? current.path : '', term);
    state.tabs.push(tab);
    state.active = state.tabs.length - 1;

    renderAll();

    if (startNow && tab.search.term.trim().length >= 2) {
        runSearch(tab);
    } else {
        const field = el.listing.querySelector('.mfx-search-term');
        (field || el.search).focus();
    }
}

/** Startet die Suche des Tabs, sofern die Eingabe lang genug ist. */
function runSearch(tab) {
    if (tab.search.term.trim().length < 2) {
        flash(t('SearchQueryTooShort').replace('{0}', 2), true);
        return;
    }

    // Jeder Lauf beginnt mit einer leeren Liste - sonst bleibt der Hinweis
    // "Keine Treffer" über den Treffern des neuen Laufs stehen.
    if (tab.view) {
        tab.view.hits.replaceChildren();
        tab.view.rendered = 0;
    }

    startSearch(tab.search, () => {
        if (activeTab() === tab) {
            updateSearchView(tab);
            renderStatus();
            renderTabs();
        }
    });
}

/** Rechte eines Speicherorts, auch wenn gerade ein anderer Tab aktiv ist. */
function rootRights(rootId) {
    return (state.roots.find(root => root.id === rootId) || {}).permissions || {};
}

/** Treffer öffnen: Ordner betreten, Datei anzeigen oder herunterladen. */
async function openHit(rootId, hit) {
    if (hit.isDirectory) {
        await revealInFolderTab(rootId, hit.path, null);
        return;
    }

    if (!rootRights(rootId).canDownload) {
        flash(t('ErrorAccessDenied'), true);
        return;
    }

    if (hit.preview && hit.preview !== 'none') {
        const rights = rootRights(rootId);

        openPreview(rootId, [hit], hit.path, {
            canDownload: rights.canDownload,
            canWrite: rights.canWrite
        });

        return;
    }

    window.location.href = api.downloadUrl(rootId, hit.path);
}

/**
 * Zeigt den Treffer in einem Ordnertab: der Suchtab bleibt mit seinen
 * Ergebnissen offen, gesprungen wird im nächstgelegenen Ordnertab.
 */
async function revealInFolderTab(rootId, path, selectPath) {
    let index = -1;

    for (let i = state.active - 1; i >= 0; i--) {
        if (state.tabs[i].kind !== 'search') {
            index = i;
            break;
        }
    }

    if (index < 0) {
        index = state.tabs.findIndex(tab => tab.kind !== 'search');
    }

    if (index < 0) {
        state.tabs.push(createTab(rootId, path));
        index = state.tabs.length - 1;
    }

    const target = state.tabs[index];
    state.active = index;

    await navigate(target, rootId, path);

    if (!selectPath) {
        return;
    }

    target.selection.clear();
    target.selection.add(selectPath);
    target.anchor = selectPath;
    selectionChanged();

    const row = el.listing.querySelector(`.mfx-row[data-path="${CSS.escape(selectPath)}"]`);
    if (row) {
        row.scrollIntoView({ block: 'center' });
    }
}

// ---------------------------------------------------------------- Anzeige der Suche

function searchStateText(search) {
    if (search.state === 'running') {
        return t('SearchRunning');
    }

    if (search.state === 'completed') {
        return t('SearchDone');
    }

    if (search.state === 'cancelled') {
        return t('SearchCancelled');
    }

    if (search.state === 'failed') {
        return t(errorKey(search.error), t('Error'));
    }

    return t('SearchNothingYet');
}

function renderSearchView(tab) {
    const search = tab.search;

    const view = document.createElement('div');
    view.className = 'mfx-search-view';

    // --- Suchformular
    const form = document.createElement('div');
    form.className = 'mfx-search-form';

    const term = document.createElement('input');
    term.type = 'search';
    term.className = 'mfx-search-term';
    term.value = search.term;
    term.placeholder = t('SearchTerm');
    term.setAttribute('aria-label', t('SearchTerm'));
    term.addEventListener('input', () => {
        search.term = term.value;
        el.search.value = term.value;
    });
    term.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            runSearch(tab);
        }
    });
    form.appendChild(labelled(t('SearchTerm'), term));

    const scope = document.createElement('select');
    state.roots.forEach(root => {
        const option = document.createElement('option');
        option.value = root.id;
        option.textContent = root.name;
        option.selected = root.id === search.scopeRootId;
        scope.appendChild(option);
    });
    scope.addEventListener('change', () => {
        search.scopeRootId = Number(scope.value);
        tab.rootId = search.scopeRootId;
    });
    form.appendChild(labelled(t('SearchScope'), scope));

    const folder = document.createElement('input');
    folder.type = 'text';
    folder.className = 'mf-mono';
    folder.value = search.scopePath;
    folder.placeholder = t('SearchWholeLocation');
    folder.addEventListener('input', () => {
        search.scopePath = folder.value.trim();
    });
    form.appendChild(labelled(t('SearchFolder'), folder));

    const options = document.createElement('div');
    options.className = 'mfx-search-options';
    options.appendChild(searchOption(t('SearchContent'), search.content, value => { search.content = value; }, 'search'));
    options.appendChild(searchOption(t('ShowHidden'), search.hidden, value => { search.hidden = value; }, 'eye'));
    form.appendChild(options);

    const actions = document.createElement('div');
    actions.className = 'mf-actions mfx-search-actions';

    const start = document.createElement('button');
    start.type = 'button';
    start.className = 'mf-btn mf-btn-primary';
    start.appendChild(icon('search-deep', 14));
    start.appendChild(document.createTextNode(t('SearchStart')));
    start.addEventListener('click', () => runSearch(tab));
    actions.appendChild(start);

    const stop = document.createElement('button');
    stop.type = 'button';
    stop.className = 'mf-btn';
    stop.textContent = t('Cancel');
    stop.disabled = !isRunning(search);
    stop.addEventListener('click', () => cancelSearch(search));
    actions.appendChild(stop);

    form.appendChild(actions);
    view.appendChild(form);

    // --- Fortschritt
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

    view.appendChild(progress);

    // --- Treffer
    const hits = document.createElement('ul');
    hits.className = 'mfx-search-hits';
    view.appendChild(hits);

    el.listing.replaceChildren(view);

    tab.view = { fill, stats, current, hits, stop, rendered: 0 };
    updateSearchView(tab);
}

function labelled(text, input) {
    const field = document.createElement('label');
    field.className = 'mf-field mfx-search-field';

    const caption = document.createElement('span');
    caption.textContent = text;
    field.appendChild(caption);
    field.appendChild(input);

    return field;
}

function searchOption(text, checked, onChange, iconName) {
    const wrapper = document.createElement('label');
    wrapper.className = 'mf-check';

    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = checked;
    box.addEventListener('change', () => onChange(box.checked));

    const caption = document.createElement('span');
    caption.className = 'mfx-option-text';

    if (iconName) {
        caption.appendChild(icon(iconName, 14));
    }

    caption.appendChild(document.createTextNode(text));

    wrapper.append(box, caption);
    return wrapper;
}

/** Fortschritt auffrischen und neue Treffer anhängen - ohne alles neu zu bauen. */
function updateSearchView(tab) {
    const view = tab.view;
    const search = tab.search;

    if (!view || !view.hits.isConnected) {
        return;
    }

    // Neuer Lauf: die Trefferliste beginnt wieder bei null.
    if (view.rendered > search.hits.length) {
        view.hits.replaceChildren();
        view.rendered = 0;
    }

    view.fill.style.width = `${search.percent}%`;
    view.stop.disabled = !isRunning(search);

    const scanned = t('SearchProgress')
        .replace('{0}', search.folders.toLocaleString())
        .replace('{1}', search.files.toLocaleString());

    view.stats.textContent = search.state === 'idle'
        ? t('SearchNothingYet')
        : `${searchStateText(search)} · ${scanned}`;

    view.current.textContent = search.current ? `${rootName(search.scopeRootId)}/${search.current}` : '';

    for (let i = view.rendered; i < search.hits.length; i++) {
        view.hits.appendChild(hitRow(tab, search.hits[i]));
    }

    view.rendered = search.hits.length;

    // Der Hinweis steht nur da, solange es wirklich nichts zu zeigen gibt.
    const note = view.hits.querySelector('.mfx-search-empty');
    const nothingFound = search.state !== 'running' && search.state !== 'idle' && search.hits.length === 0;

    if (note && !nothingFound) {
        note.remove();
    }

    if (!note && nothingFound) {
        const empty = document.createElement('li');
        empty.className = 'mfx-search-empty mf-muted';
        empty.textContent = t('SearchNoHits');
        view.hits.appendChild(empty);
    }
}

function rootName(rootId) {
    return (state.roots.find(root => root.id === rootId) || {}).name || '';
}

/** Schreibt Text und hebt die Fundstelle hervor - ohne HTML aus dem Inhalt. */
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

function hitRow(tab, hit) {
    const rootId = tab.search.scopeRootId;

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
    where.textContent = hit.parentPath ? `${rootName(rootId)}/${hit.parentPath}` : rootName(rootId);
    text.appendChild(where);

    if (hit.snippet) {
        const snippet = document.createElement('span');
        snippet.className = 'mfx-hit-snippet';
        highlight(snippet, hit.snippet, tab.search.term);
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

    const reveal = button('mfx-btn', 'folder', t('ShowInFolder'), event => {
        event.stopPropagation();
        revealInFolderTab(rootId, hit.parentPath || '', hit.path);
    });
    row.appendChild(reveal);

    row.addEventListener('dblclick', () => openHit(rootId, hit));
    row.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            openHit(rootId, hit);
        }
    });

    return row;
}

function entryMenu(entry) {
    const tab = activeTab();
    const rights = permissions();
    const multiple = tab.selection.size > 1;
    const isArchive = !entry.isDirectory && /\.zip$/i.test(entry.name);
    const items = [];

    if (entry.isDirectory) {
        items.push({ label: t('Open'), icon: 'folder', action: () => openEntry(entry) });
        items.push({ label: t('OpenInNewTab'), icon: 'plus', action: () => openTab(tab.rootId, entry.path) });
    } else if (entry.preview && entry.preview !== 'none') {
        items.push({
            label: t('Preview'),
            icon: 'eye',
            disabled: !rights.canDownload,
            action: () => showPreview(tab, entry, false)
        });

        // Textdateien lassen sich gleich im Editor öffnen.
        if (entry.preview === 'text') {
            items.push({
                label: t('Edit'),
                icon: 'pencil',
                disabled: !rights.canDownload || !rights.canWrite,
                action: () => showPreview(tab, entry, true)
            });
        }
    }

    items.push({
        label: entry.isDirectory || multiple ? t('DownloadZip') : t('Download'),
        icon: 'download',
        disabled: !rights.canDownload,
        action: () => (entry.isDirectory || multiple ? downloadZip() : openEntry(entry))
    });

    items.push('-');
    items.push({ label: t('Cut'), icon: 'file', disabled: !rights.canDelete, action: () => setClipboard(true) });
    items.push({ label: t('Copy'), icon: 'file', disabled: !rights.canRead, action: () => setClipboard(false) });
    items.push({ label: t('Paste'), icon: 'file', disabled: !state.clipboard || !rights.canWrite, action: () => paste() });

    items.push('-');
    items.push({
        label: t('Rename'),
        icon: 'pencil',
        disabled: !rights.canWrite || multiple,
        action: () => renameEntry(entry)
    });
    items.push({ label: t('Delete'), icon: 'trash', danger: true, disabled: !rights.canDelete, action: () => deleteSelected() });

    items.push('-');
    items.push({ label: t('Compress'), icon: 'archive', disabled: !rights.canWrite, action: () => compressSelected() });

    if (isArchive) {
        items.push({ label: t('Extract'), icon: 'archive', disabled: !rights.canWrite, action: () => extractEntry(entry) });
    }

    if (entry.isDirectory) {
        items.push({ label: t('AddBookmark'), icon: 'star', action: () => addBookmark(entry) });
    }

    items.push('-');
    items.push({ label: t('CopyPath'), icon: 'file', action: () => copyPath(entry) });
    items.push({ label: t('Properties'), icon: 'settings', action: () => showProperties(entry) });

    return items;
}

function folderMenu() {
    const rights = permissions();

    return [
        { label: t('Upload'), icon: 'upload', disabled: !rights.canUpload, action: () => chooseUpload() },
        { label: t('NewFolder'), icon: 'folder-plus', disabled: !rights.canWrite, action: () => newFolder() },
        { label: t('NewFile'), icon: 'file-plus', disabled: !rights.canWrite, action: () => newFile() },
        '-',
        { label: t('Paste'), icon: 'file', disabled: !state.clipboard || !rights.canWrite, action: () => paste() },
        { label: t('SelectAll'), icon: 'list', action: () => selectAll() },
        '-',
        { label: t('Refresh'), icon: 'refresh', action: () => reload() },
        { label: t('AddBookmark'), icon: 'star', action: () => addBookmark(null) },
        { label: t('Properties'), icon: 'settings', action: () => showProperties(null) }
    ];
}

// ---------------------------------------------------------------- Ereignisse

function entryFromRow(row) {
    const tab = activeTab();
    return (tab.entries || []).find(entry => entry.path === row.dataset.path);
}

/** Die Liste reagiert nur im Ordnertab - der Suchtab hat seine eigenen Zeilen. */
function isSearchTab() {
    const tab = activeTab();
    return !!tab && tab.kind === 'search';
}

el.listing.addEventListener('mousedown', event => {
    if (isSearchTab()) {
        return;
    }

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
    if (isSearchTab()) {
        return;
    }

    const row = event.target.closest('.mfx-row');
    if (row) {
        const entry = entryFromRow(row);
        if (entry) {
            openEntry(entry);
        }
    }
});

el.listing.addEventListener('contextmenu', event => {
    if (isSearchTab()) {
        return;
    }

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
    if (isPreviewOpen()) {
        return;
    }

    if (event.key === 'Escape') {
        closeMenu();
        return;
    }

    // Bei Eingaben nicht eingreifen. Ohne Fokus ist das Ziel das Dokument selbst.
    if (event.target instanceof Element && event.target.matches('input, textarea, select')) {
        return;
    }

    // Im Suchfenster gelten seine eigenen Tasten, nicht die der Dateiliste.
    if (event.target instanceof Element && event.target.closest('.mfx-window')) {
        return;
    }

    const tab = activeTab();
    if (!tab) {
        return;
    }

    if (document.querySelector('dialog[open]')) {
        return;
    }

    if (event.ctrlKey || event.metaKey) {
        const key = event.key.toLowerCase();

        if (key === 'a') {
            event.preventDefault();
            selectAll();
            return;
        }

        if (key === 'c') {
            event.preventDefault();
            setClipboard(false);
            return;
        }

        if (key === 'x') {
            event.preventDefault();
            setClipboard(true);
            return;
        }

        if (key === 'v') {
            event.preventDefault();
            paste();
            return;
        }

        if (key === 'f') {
            event.preventDefault();
            focusSearch(false);
            return;
        }
    }

    // Im Suchtab gibt es keine Dateiliste, auf die sich die Tasten beziehen könnten.
    if (tab.kind === 'search') {
        if (event.key === 'F5') {
            event.preventDefault();
            reload();
        }

        return;
    }

    if (event.key === 'F2') {
        const selected = selectedEntries();
        if (selected.length === 1) {
            event.preventDefault();
            renameEntry(selected[0]);
        }
        return;
    }

    if (event.key === 'Delete') {
        event.preventDefault();
        deleteSelected();
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

        selectionChanged();
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
el.refresh.addEventListener('click', () => reload());

el.search.addEventListener('input', () => {
    const tab = activeTab();

    // Im Suchtab ist das Feld der Suchbegriff, sonst der Filter des Ordners.
    if (tab && tab.kind === 'search') {
        tab.search.term = el.search.value;
        const field = el.listing.querySelector('.mfx-search-term');
        if (field) {
            field.value = el.search.value;
        }
        renderTabs();
        return;
    }

    state.filter = el.search.value.trim();
    renderListing();
    renderStatus();
});

// Enter verlässt den aktuellen Ordner und sucht in allen Unterordnern.
el.search.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
        event.preventDefault();
        startSearchFromField(false);
    }
});

el.searchGo.addEventListener('click', () => startSearchFromField(false));

/** Sucht im aktuellen Suchtab weiter oder öffnet einen neuen für die Eingabe. */
function startSearchFromField(withContent) {
    const tab = activeTab();
    const term = el.search.value.trim();

    if (tab && tab.kind === 'search') {
        tab.search.term = term;
        tab.search.content = tab.search.content || withContent;
        runSearch(tab);
        return;
    }

    openSearchTab(term, true).then(() => {
        if (!withContent) {
            return;
        }

        const fresh = activeTab();
        if (fresh && fresh.kind === 'search') {
            fresh.search.content = true;
            runSearch(fresh);
            renderListing();
        }
    });
}

/** Steht schon etwas im Feld, geht die Suche gleich los - sonst wartet der Fokus dort. */
function focusSearch(withContent) {
    if (el.search.value.trim().length >= 2) {
        startSearchFromField(withContent);
        return;
    }

    openSearchTab('', false);
}

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

if (el.sideFirst) {
    el.sideFirst.addEventListener('click', () => addLocation(false));
}

el.backdrop.addEventListener('click', () => {
    el.sidebar.classList.remove('is-open');
    el.backdrop.hidden = true;
});

// ---------------------------------------------------------------- Menüleiste

/** Einträge der Menüs über den Tabs. */
function menuItems(name) {
    const tab = activeTab();
    const rights = permissions();
    const selected = selectedEntries();
    const single = selected.length === 1;

    if (name === 'file') {
        return [
            { label: t('NewFolder'), icon: 'folder-plus', disabled: !rights.canWrite, action: () => newFolder() },
            { label: t('NewFile'), icon: 'file-plus', disabled: !rights.canWrite, action: () => newFile() },
            { label: t('Upload'), icon: 'upload', disabled: !rights.canUpload, action: () => chooseUpload() },
            '-',
            {
                label: t('Download'), icon: 'download',
                disabled: !rights.canDownload || selected.length === 0,
                action: () => (single && !selected[0].isDirectory ? openEntry(selected[0]) : downloadZip())
            },
            {
                label: t('DownloadZip'), icon: 'archive',
                disabled: !rights.canDownload || selected.length === 0,
                action: () => downloadZip()
            },
            {
                label: t('Compress'), icon: 'archive',
                disabled: !rights.canWrite || selected.length === 0,
                action: () => compressSelected()
            },
            '-',
            { label: t('NewTab'), icon: 'plus', action: () => newTab() },
            { label: t('SearchTab'), icon: 'search-deep', action: () => openSearchTab('', false) },
            {
                label: t('Properties'), icon: 'settings',
                disabled: !tab || tab.kind === 'search',
                action: () => showProperties(single ? selected[0] : null)
            }
        ];
    }

    if (name === 'edit') {
        return [
            { label: t('Cut'), icon: 'file', disabled: !rights.canDelete || selected.length === 0, action: () => setClipboard(true) },
            { label: t('Copy'), icon: 'file', disabled: selected.length === 0, action: () => setClipboard(false) },
            { label: t('Paste'), icon: 'file', disabled: !state.clipboard || !rights.canWrite, action: () => paste() },
            '-',
            { label: t('Rename'), icon: 'pencil', disabled: !rights.canWrite || !single, action: () => renameEntry(selected[0]) },
            { label: t('Delete'), icon: 'trash', danger: true, disabled: !rights.canDelete || selected.length === 0, action: () => deleteSelected() },
            '-',
            { label: t('SelectAll'), icon: 'list', action: () => selectAll() },
            { label: t('AddBookmark'), icon: 'star', action: () => addBookmark(single && selected[0].isDirectory ? selected[0] : null) },
            '-',
            { label: t('SearchRecursive'), icon: 'search-deep', action: () => focusSearch(false) },
            { label: t('SearchContent'), icon: 'search', action: () => focusSearch(true) }
        ];
    }

    if (name === 'view') {
        return [
            {
                label: state.settings.viewMode === 'grid' ? t('ViewList') : t('ViewGrid'),
                icon: state.settings.viewMode === 'grid' ? 'list' : 'grid',
                action: () => el.view.click()
            },
            {
                label: t('ShowHidden') + (state.settings.showHidden ? ' ✓' : ''),
                icon: 'eye',
                action: () => el.hidden.click()
            },
            {
                label: t('Details') + (state.settings.showDetails ? ' ✓' : ''),
                icon: 'info',
                action: () => el.detailsToggle.click()
            },
            {
                label: t('Sidebar') + (state.settings.showSidebar !== false ? ' ✓' : ''),
                icon: 'folder',
                action: () => toggleSidebar()
            },
            '-',
            { label: t('Refresh'), icon: 'refresh', action: () => reload() }
        ];
    }

    if (name === 'locations') {
        const items = [
            { label: t('AddFolder'), icon: 'folder', action: () => addLocation(false) },
            { label: t('AddConnection'), icon: 'cloud', action: () => addLocation(true) },
            '-',
            {
                label: t('EditLocation'), icon: 'pencil',
                disabled: !tab || tab.kind === 'search',
                action: () => editLocation(tab.rootId)
            },
            { label: t('Manage'), icon: 'settings', action: () => openLocations(() => reloadRoots()) }
        ];

        // Darunter die vorhandenen Speicherorte zum direkten Öffnen.
        if (state.roots.length > 0) {
            items.push('-');

            state.roots.forEach(root => items.push({
                label: root.name,
                icon: iconForRoot(root),
                action: () => openRoot(root.id)
            }));
        }

        return items;
    }

    // Einstellungen
    const theme = document.documentElement.getAttribute('data-theme') || 'auto';
    const setTheme = value => {
        document.documentElement.setAttribute('data-theme', value);
        document.cookie = 'matfile.theme=' + value + ';path=/;max-age=31536000;samesite=lax';
    };

    const settings = [
        {
            label: t('Language'), icon: 'globe',
            action: () => document.getElementById('mf-language-dialog')?.showModal()
        },
        '-',
        { label: t('ThemeSystem') + (theme === 'auto' ? ' ✓' : ''), icon: 'monitor', action: () => setTheme('auto') },
        { label: t('ThemeLight') + (theme === 'light' ? ' ✓' : ''), icon: 'sun', action: () => setTheme('light') },
        { label: t('ThemeDark') + (theme === 'dark' ? ' ✓' : ''), icon: 'moon', action: () => setTheme('dark') },
        '-',
        { label: t('ChangePassword'), icon: 'lock', action: () => { window.location.href = '/Account/ChangePassword'; } },
        {
            label: t('Logout'), icon: 'log-out',
            action: () => document.querySelector('.mfx-side-foot form[action="/Logout"]')?.submit()
        }
    ];

    // Verwaltung sitzt in denselben Menüs - jeder Punkt nur mit dem passenden Recht.
    const administration = [];

    if (can.manageUsers) {
        administration.push({ label: t('Admin.Users'), icon: 'users', action: () => openUsers() });
    }

    if (can.manageRoles) {
        administration.push({ label: t('Admin.Roles'), icon: 'shield', action: () => openRoles() });
    }

    if (can.manageRoots) {
        administration.push({ label: t('Admin.Roots'), icon: 'hard', action: () => openLocations(() => reloadRoots()) });
    }

    return administration.length === 0 ? settings : [...administration, '-', ...settings];
}

document.querySelectorAll('.mfx-menubar-item').forEach(button => {
    button.addEventListener('click', event => {
        const rect = button.getBoundingClientRect();
        openMenu(rect.left, rect.bottom + 2, menuItems(button.dataset.menu));
        event.stopPropagation();
    });
});

if (el.more) {
    el.more.addEventListener('click', event => {
        const rect = el.more.getBoundingClientRect();

        openMenu(rect.left, rect.bottom + 4, [
            { label: t('AddFolder'), icon: 'folder', action: () => addLocation(false) },
            { label: t('AddConnection'), icon: 'hard', action: () => addLocation(true) },
            '-',
            { label: t('Manage'), icon: 'settings', action: () => openLocations(() => reloadRoots()) }
        ]);

        event.stopPropagation();
    });
}

el.detailsToggle.addEventListener('click', () => {
    state.settings.showDetails = !state.settings.showDetails;
    applySettingsToToolbar();
    persistSettings();
    renderDetails();
});

el.upload.addEventListener('click', chooseUpload);
el.newFolder.addEventListener('click', newFolder);
el.newFile.addEventListener('click', newFile);

// Dateien per Drag & Drop in den aktuellen Ordner hochladen
['dragenter', 'dragover'].forEach(name => {
    el.listing.addEventListener(name, event => {
        if (!event.dataTransfer || !event.dataTransfer.types.includes('Files')) {
            return;
        }

        event.preventDefault();
        event.dataTransfer.dropEffect = 'copy';
        el.listing.classList.add('is-dropping');
    });
});

['dragleave', 'dragend'].forEach(name => {
    el.listing.addEventListener(name, event => {
        if (event.target === el.listing) {
            el.listing.classList.remove('is-dropping');
        }
    });
});

el.listing.addEventListener('drop', event => {
    if (!event.dataTransfer || event.dataTransfer.files.length === 0) {
        return;
    }

    event.preventDefault();
    el.listing.classList.remove('is-dropping');

    const tab = activeTab();
    if (!tab || !permissions().canUpload) {
        flash(t('ErrorAccessDenied'), true);
        return;
    }

    uploadFiles([...event.dataTransfer.files], tab.rootId, tab.path);
});

onTransfersFinished(() => reload());

init();
