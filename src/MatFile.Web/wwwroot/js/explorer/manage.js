// Der Administrationsbereich des Explorers: ein Fenster mit einer Spalte links
// (Benutzer, Rollen, Speicherorte, System) und der jeweiligen Ansicht rechts.
// Jeder Bereich zeigt zuerst die Liste und wechselt zum Bearbeiten in eine eigene Ansicht.
import { api, t, icon, formatDate } from './api.js';
import { askConfirm, flash } from './dialogs.js';
import { openRootDialog, deleteRoot } from './rootdialog.js';

// ---------------------------------------------------------------- Bausteine

/**
 * Rahmen des Administrationsbereichs. `sections` sind die erlaubten Bereiche in
 * der Reihenfolge der Spalte; jeder bringt seine eigene Ansicht mit.
 */
function frame(sections, active) {
    const dialog = document.createElement('dialog');
    dialog.className = 'mfx-dialog mfx-manage mfx-admin';

    const head = document.createElement('header');
    head.className = 'mfx-dialog-head';
    head.appendChild(icon('shield', 20));

    const heading = document.createElement('h2');
    heading.textContent = t('Admin.Title');
    head.appendChild(heading);

    const spacer = document.createElement('span');
    spacer.className = 'mf-spacer';
    head.appendChild(spacer);

    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'mfx-btn mfx-dialog-close';
    close.title = t('Close');
    close.setAttribute('aria-label', t('Close'));
    close.appendChild(icon('x', 14));
    close.addEventListener('click', () => dialog.close());
    head.appendChild(close);

    const main = document.createElement('div');
    main.className = 'mfx-admin-main';

    const nav = document.createElement('nav');
    nav.className = 'mfx-admin-nav';
    nav.setAttribute('aria-label', t('Admin.Title'));

    const content = document.createElement('div');
    content.className = 'mfx-admin-content';

    main.append(nav, content);
    dialog.append(head, main);
    dialog.addEventListener('close', () => dialog.remove());
    document.body.appendChild(dialog);
    dialog.showModal();

    // Nach aussen zaehlt nur der Dialog; jeder Bereich bekommt eigene Elemente.
    const shared = { dialog };
    const buttons = new Map();

    const show = key => {
        const section = sections.find(item => item.key === key) || sections[0];
        if (!section) {
            return;
        }

        buttons.forEach((element, name) => element.classList.toggle('is-active', name === section.key));

        // Eigene Elemente je Bereich: laedt ein alter Bereich noch nach, schreibt
        // er in seine inzwischen abgehaengten Knoten und nicht in die neue Ansicht.
        const title = document.createElement('h3');
        title.className = 'mfx-admin-title';

        const body = document.createElement('div');
        body.className = 'mfx-manage-body';

        const foot = document.createElement('div');
        foot.className = 'mf-actions mfx-manage-foot';

        content.replaceChildren(title, body, foot);

        shared.section = section.key;
        shared.title = title;
        shared.body = body;
        shared.foot = foot;

        section.open({ ...shared, title, body, foot,
            get reopen() { return shared.reopen; },
            get onRootsChanged() { return shared.onRootsChanged; } });
    };

    sections.forEach(section => {
        const item = button(section.label, 'mfx-admin-item', () => show(section.key), section.icon);
        buttons.set(section.key, item);
        nav.appendChild(item);
    });

    show(active);
    return shared;
}

function button(label, className, onClick, iconName) {
    const element = document.createElement('button');
    element.type = 'button';
    element.className = className;

    if (iconName) {
        element.appendChild(icon(iconName, 14));
    }

    element.appendChild(document.createTextNode(label));
    element.addEventListener('click', onClick);
    return element;
}

function gap() {
    const spacer = document.createElement('span');
    spacer.className = 'mf-actions-gap';
    return spacer;
}

/** Formularfeld im gemeinsamen Raster. Breite Felder nehmen die ganze Zeile ein. */
function field(labelText, input, hint, wide) {
    const wrapper = document.createElement('div');
    wrapper.className = 'mf-field' + (wide ? ' mf-field-wide' : '');

    const label = document.createElement('label');
    label.textContent = labelText;
    wrapper.appendChild(label);
    wrapper.appendChild(input);

    if (hint) {
        const note = document.createElement('p');
        note.className = 'mf-field-hint';
        note.textContent = hint;
        wrapper.appendChild(note);
    }

    return wrapper;
}

/** Zweispaltiges Raster für die Formularansicht. */
function formGrid(...fields) {
    const grid = document.createElement('div');
    grid.className = 'mf-form-grid';
    fields.filter(Boolean).forEach(item => grid.appendChild(item));
    return grid;
}

/** Mehrfachauswahl als umrandeter Kasten mit mehreren Spalten. */
function checkBox(entries) {
    const box = document.createElement('div');
    box.className = 'mfx-manage-box mf-checklist-grid';
    entries.forEach(entry => box.appendChild(entry.row));
    return box;
}

/** Untereinander stehende Häkchen ohne Rahmen. */
function checkColumn(entries) {
    const column = document.createElement('div');
    column.className = 'mf-checklist';
    entries.forEach(entry => column.appendChild(entry.row));
    return column;
}

function textBox(value, type) {
    const input = document.createElement('input');
    input.type = type || 'text';
    input.value = value ?? '';
    return input;
}

function check(labelText, checked, disabled) {
    const wrapper = document.createElement('label');
    wrapper.className = 'mf-check';

    const input = document.createElement('input');
    input.type = 'checkbox';
    input.checked = !!checked;
    input.disabled = !!disabled;

    const text = document.createElement('span');
    text.textContent = labelText;

    wrapper.append(input, text);
    return { row: wrapper, input };
}

/** Tabelle aus Spaltenbeschreibungen und Zeilen. */
function tableOf(columns, rows, onRowClick, emptyText) {
    const wrap = document.createElement('div');
    wrap.className = 'mf-table-wrap';

    if (rows.length === 0) {
        const empty = document.createElement('p');
        empty.className = 'mf-muted mfx-manage-empty';
        empty.textContent = emptyText;
        wrap.appendChild(empty);
        return wrap;
    }

    const table = document.createElement('table');
    table.className = 'mf-table';

    const head = document.createElement('thead');
    const headRow = document.createElement('tr');

    columns.forEach(column => {
        const cell = document.createElement('th');
        cell.scope = 'col';
        cell.textContent = column.label || '';

        if (column.narrow) {
            cell.className = 'mf-cell-narrow';
        }

        headRow.appendChild(cell);
    });

    head.appendChild(headRow);
    table.appendChild(head);

    const bodyRows = document.createElement('tbody');

    rows.forEach(row => {
        const line = document.createElement('tr');

        if (onRowClick) {
            line.className = 'mf-row-clickable';
            line.tabIndex = 0;
            line.addEventListener('click', event => {
                if (!event.target.closest('button')) {
                    onRowClick(row);
                }
            });
            line.addEventListener('keydown', event => {
                if (event.key === 'Enter') {
                    onRowClick(row);
                }
            });
        }

        columns.forEach(column => {
            const cell = document.createElement('td');
            const content = column.cell(row);

            if (content instanceof Node) {
                cell.appendChild(content);
            } else {
                cell.textContent = content ?? '';
            }

            if (column.narrow) {
                cell.className = 'mf-cell-narrow';
            }

            line.appendChild(cell);
        });

        bodyRows.appendChild(line);
    });

    table.appendChild(bodyRows);
    wrap.appendChild(table);
    return wrap;
}

/** Blätterleiste direkt unter der Tabelle. */
function pager(data, onPage) {
    const bar = document.createElement('div');
    bar.className = 'mf-pagination mfx-manage-pager';

    const summary = document.createElement('span');
    summary.className = 'mf-pagination-info';
    summary.textContent = t('Pagination.Summary')
        .replace('{0}', data.page)
        .replace('{1}', data.pageCount)
        .replace('{2}', data.total);
    bar.appendChild(summary);

    if (data.pageCount > 1) {
        const previous = button('', 'mf-btn mf-btn-tiny', () => onPage(data.page - 1), 'chevron-left');
        previous.disabled = data.page <= 1;
        previous.title = t('Back');

        const next = button('', 'mf-btn mf-btn-tiny', () => onPage(data.page + 1), 'chevron');
        next.disabled = data.page >= data.pageCount;

        bar.append(previous, next);
    }

    return bar;
}

/** Suchfeld und Knöpfe über der Tabelle. */
function toolbar(onSearch, extras) {
    const bar = document.createElement('div');
    bar.className = 'mf-toolbar mfx-manage-toolbar';

    const box = document.createElement('div');
    box.className = 'mfx-search';
    box.appendChild(icon('search', 14));

    const input = document.createElement('input');
    input.type = 'search';
    input.placeholder = t('Common.Search');
    input.setAttribute('aria-label', t('Common.Search'));

    let timer = null;
    input.addEventListener('input', () => {
        clearTimeout(timer);
        timer = setTimeout(() => onSearch(input.value.trim()), 300);
    });

    box.appendChild(input);
    bar.appendChild(box);

    const spacer = document.createElement('span');
    spacer.className = 'mf-spacer';
    bar.appendChild(spacer);

    (extras || []).forEach(extra => bar.appendChild(extra));
    return bar;
}

function badge(text, kind) {
    const element = document.createElement('span');
    element.className = 'mf-badge' + (kind ? ' is-' + kind : '');
    element.textContent = text;
    return element;
}

/** Fehlermeldung der API anzeigen - sie kommt schon übersetzt vom Server. */
function showError(error) {
    flash(error && error.code && error.code !== 'error' ? error.code : t('Error'), true);
}

// ---------------------------------------------------------------- Benutzer

function usersSection(view) {
    let roles = [];
    let search = '';
    let page = 1;

    async function list() {
        view.title.textContent = t('Admin.Users');

        let data;

        try {
            data = await api.users({ search, page });
        } catch (error) {
            showError(error);
            return;
        }

        roles = data.roles;

        const columns = [
            { label: t('User.Username'), cell: row => row.username },
            { label: t('User.DisplayName'), cell: row => row.displayName },
            { label: t('User.Roles'), cell: row => (row.roles || []).join(', ') },
            {
                label: t('User.Status'),
                cell: row => row.isLocked
                    ? badge(t('User.Locked'), 'warn')
                    : badge(row.isActive ? t('User.Active') : t('User.Inactive'), row.isActive ? 'ok' : null)
            },
            { label: t('User.LastLogin'), cell: row => row.lastLoginDate ? formatDate(row.lastLoginDate) : t('Common.Never') },
            {
                label: '', narrow: true,
                cell: row => button('', 'mf-btn mf-btn-tiny mf-btn-danger', () => remove(row), 'trash')
            }
        ];

        view.body.replaceChildren(
            toolbar(value => { search = value; page = 1; list(); }),
            tableOf(columns, data.items, row => form(row.id), t('User.Empty')),
            pager(data, target => { page = target; list(); }));

        view.foot.replaceChildren(
            button(t('Common.New'), 'mf-btn mf-btn-primary', () => form(null), 'plus'),
            button(t('Close'), 'mf-btn', () => view.dialog.close()));
    }

    async function form(id) {
        let user = { isActive: true, mustChangePassword: false, roleIds: [] };

        if (id) {
            try {
                user = await api.user(id);
            } catch (error) {
                showError(error);
                return;
            }
        }

        view.title.textContent = id ? t('User.Edit.Title') : t('User.New.Title');

        const username = textBox(user.username);
        const displayName = textBox(user.displayName);
        const email = textBox(user.email, 'email');
        const password = textBox('', 'password');
        const active = check(t('User.IsActive'), user.isActive);
        const mustChange = check(t('User.MustChangePassword'), user.mustChangePassword);

        const roleBoxes = roles.map(role => {
            const entry = check(role.name, (user.roleIds || []).includes(role.id));
            entry.row.dataset.roleId = role.id;
            return { role, ...entry };
        });

        view.body.replaceChildren(formGrid(
            field(t('User.Username'), username),
            field(t('User.DisplayName'), displayName),
            field(t('User.Email'), email),
            field(t('User.Password'), password, id ? t('User.PasswordKeep') : null),
            field(t('User.Roles'), checkBox(roleBoxes), null, true),
            field(t('User.Status'), checkColumn([active, mustChange]), null, true)));

        const save = async () => {
            const payload = {
                username: username.value.trim(),
                displayName: displayName.value.trim(),
                email: email.value.trim(),
                isActive: active.input.checked,
                newPassword: password.value,
                mustChangePassword: mustChange.input.checked,
                roleIds: roleBoxes.filter(entry => entry.input.checked).map(entry => entry.role.id)
            };

            try {
                await (id ? api.updateUser(id, payload) : api.createUser(payload));
                flash(t('Common.Saved'));
                await list();
            } catch (error) {
                showError(error);
            }
        };

        const actions = [
            button(t('Common.Save'), 'mf-btn mf-btn-primary', save, 'save'),
            button(t('Common.Back'), 'mf-btn', () => list(), 'chevron-left')
        ];

        if (id && user.isLocked) {
            actions.push(button(t('User.Unlock'), 'mf-btn', async () => {
                try {
                    await api.unlockUser(id);
                    flash(t('User.Unlocked'));
                    await list();
                } catch (error) {
                    showError(error);
                }
            }, 'lock'));
        }

        if (id) {
            actions.push(gap());
            actions.push(button(t('Common.Delete'), 'mf-btn mf-btn-danger', () => remove(user), 'trash'));
        }

        view.foot.replaceChildren(...actions);
        username.focus();
    }

    async function remove(user) {
        const confirmed = await askConfirm(
            t('Common.Delete'), t('DeleteQuestionOne').replace('{0}', user.username), t('Common.Delete'), true);

        if (!confirmed) {
            return;
        }

        try {
            await api.deleteUser(user.id);
            await list();
        } catch (error) {
            showError(error);
        }
    }

    list();
}

// ---------------------------------------------------------------- Rollen

function rolesSection(view) {
    let catalog = [];
    let search = '';
    let page = 1;

    async function list() {
        view.title.textContent = t('Admin.Roles');

        let data;

        try {
            data = await api.roleList({ search, page });
        } catch (error) {
            showError(error);
            return;
        }

        catalog = data.catalog;

        const columns = [
            { label: t('Role.Name'), cell: row => row.name },
            { label: t('Role.Description'), cell: row => row.description },
            { label: t('Role.Permissions'), cell: row => String(row.permissions.length) },
            { label: t('Role.UserCount'), cell: row => String(row.userCount) },
            { label: '', narrow: true, cell: row => row.isSystem ? badge(t('Role.System')) : document.createTextNode('') },
            {
                label: '', narrow: true,
                cell: row => row.isSystem
                    ? document.createTextNode('')
                    : button('', 'mf-btn mf-btn-tiny mf-btn-danger', () => remove(row), 'trash')
            }
        ];

        view.body.replaceChildren(
            toolbar(value => { search = value; page = 1; list(); }),
            tableOf(columns, data.items, row => form(row.id), t('Role.Empty')),
            pager(data, target => { page = target; list(); }));

        view.foot.replaceChildren(
            button(t('Common.New'), 'mf-btn mf-btn-primary', () => form(null), 'plus'),
            button(t('Close'), 'mf-btn', () => view.dialog.close()));
    }

    async function form(id) {
        let role = { permissions: [], isSystem: false };

        if (id) {
            try {
                role = await api.role(id);
            } catch (error) {
                showError(error);
                return;
            }
        }

        view.title.textContent = id ? t('Role.Edit.Title') : t('Role.New.Title');

        const name = textBox(role.name);
        name.disabled = role.isSystem;

        const description = textBox(role.description);

        const boxes = catalog.map(permission => {
            const entry = check(permission.label, role.permissions.includes(permission.name), role.isSystem);
            return { permission, ...entry };
        });

        const grid = formGrid(
            field(t('Role.Name'), name),
            field(t('Role.Description'), description),
            field(t('Role.Permissions'), checkBox(boxes), null, true));

        const parts = [grid];

        if (role.isSystem) {
            const hint = document.createElement('p');
            hint.className = 'mfx-manage-note';
            hint.textContent = t('Role.SystemHint');
            parts.unshift(hint);
        }

        view.body.replaceChildren(...parts);

        const save = async () => {
            const payload = {
                name: name.value.trim(),
                description: description.value.trim(),
                permissions: boxes.filter(entry => entry.input.checked).map(entry => entry.permission.name)
            };

            try {
                await (id ? api.updateRole(id, payload) : api.createRole(payload));
                flash(t('Common.Saved'));
                await list();
            } catch (error) {
                showError(error);
            }
        };

        const actions = [
            button(t('Common.Save'), 'mf-btn mf-btn-primary', save, 'save'),
            button(t('Common.Back'), 'mf-btn', () => list(), 'chevron-left')
        ];

        if (id && !role.isSystem) {
            actions.push(gap());
            actions.push(button(t('Common.Delete'), 'mf-btn mf-btn-danger', () => remove(role), 'trash'));
        }

        view.foot.replaceChildren(...actions);

        if (!role.isSystem) {
            name.focus();
        }
    }

    async function remove(role) {
        const confirmed = await askConfirm(
            t('Common.Delete'), t('DeleteQuestionOne').replace('{0}', role.name), t('Common.Delete'), true);

        if (!confirmed) {
            return;
        }

        try {
            await api.deleteRole(role.id);
            await list();
        } catch (error) {
            showError(error);
        }
    }

    list();
}

// ---------------------------------------------------------------- Systemeinstellungen

/** Zahlenfeld mit Grenzen. */
function numberBox(value, min, max) {
    const input = document.createElement('input');
    input.type = 'number';
    input.value = value ?? 0;
    input.min = min;
    input.max = max;
    return input;
}

/**
 * Einstellungen aus matfile.json. Manche greifen sofort, andere erst nach
 * einem Neustart des Containers - das steht am jeweiligen Feld.
 */
function systemSection(view) {
    async function form() {
        view.title.textContent = t('System.Title');

        let data;

        try {
            data = await api.system();
        } catch (error) {
            showError(error);
            return;
        }

        const minLength = numberBox(data.minPasswordLength, 4, 128);
        const attempts = numberBox(data.loginMaxFailedAttempts, 1, 50);
        const lockout = numberBox(data.loginLockoutMinutes, 1, 1440);
        const lifetime = numberBox(data.sessionLifetimeDays, 1, 365);
        const perMinute = numberBox(data.loginAttemptsPerMinute, 3, 1000);
        const maxUpload = numberBox(data.maxUploadMegabytes, 0, 1048576);

        const secure = check(t('System.SecureCookie'), data.requireSecureCookie);
        const firstLogin = check(t('System.FirstLogin'), data.requirePasswordChangeOnFirstLogin);

        const note = document.createElement('p');
        note.className = 'mfx-manage-note';
        note.textContent = t('System.Hint');

        const facts = document.createElement('dl');
        facts.className = 'mf-facts';

        [[t('System.ConfigFile'), data.configFile],
         [t('System.Version'), `${data.version} · ${data.channel}`]].forEach(([label, value]) => {
            const term = document.createElement('dt');
            term.textContent = label;
            const detail = document.createElement('dd');
            detail.className = 'mf-mono';
            detail.textContent = value;
            facts.append(term, detail);
        });

        view.body.replaceChildren(
            note,
            formGrid(
                field(t('System.MinPasswordLength'), minLength, t('System.MinPasswordLengthHint')),
                field(t('System.SessionLifetime'), lifetime, t('System.SessionLifetimeHint')),
                field(t('System.MaxFailedAttempts'), attempts, t('System.MaxFailedAttemptsHint')),
                field(t('System.LockoutMinutes'), lockout, t('System.LockoutMinutesHint')),
                field(t('System.AttemptsPerMinute'), perMinute, t('System.AttemptsPerMinuteHint')),
                field(t('System.MaxUpload'), maxUpload, t('System.MaxUploadHint')),
                field(t('System.Security'), checkColumn([secure, firstLogin]), t('System.RestartHint'), true)),
            facts);

        const save = async () => {
            try {
                await api.saveSystem({
                    sessionLifetimeDays: Number(lifetime.value) || 14,
                    loginMaxFailedAttempts: Number(attempts.value) || 5,
                    loginLockoutMinutes: Number(lockout.value) || 15,
                    loginAttemptsPerMinute: Number(perMinute.value) || 20,
                    minPasswordLength: Number(minLength.value) || 8,
                    maxUploadMegabytes: Number(maxUpload.value) || 0,
                    requireSecureCookie: secure.input.checked,
                    requirePasswordChangeOnFirstLogin: firstLogin.input.checked
                });

                flash(t('Common.Saved'));
                await form();
            } catch (error) {
                showError(error);
            }
        };

        view.foot.replaceChildren(
            button(t('Common.Save'), 'mf-btn mf-btn-primary', save, 'save'),
            button(t('Close'), 'mf-btn', () => view.dialog.close()));
    }

    form();
}

// ---------------------------------------------------------------- Speicherorte

const RIGHTS = [
    ['canRead', 'Access.CanRead'],
    ['canDownload', 'Access.CanDownload'],
    ['canUpload', 'Access.CanUpload'],
    ['canWrite', 'Access.CanWrite'],
    ['canDelete', 'Access.CanDelete'],
    ['canShare', 'Access.CanShare']
];

/**
 * Speicherorte verwalten. Verbindungsdaten bearbeitet der bekannte Dialog,
 * hier stehen Übersicht und die Rechte je Rolle.
 */
function rootsSection(view) {
    let search = '';
    let page = 1;

    const changed = () => {
        if (view.onRootsChanged) {
            view.onRootsChanged();
        }
    };

    async function list() {
        view.title.textContent = t('Admin.Roots');

        let data;

        try {
            data = await api.rootList({ search, page });
        } catch (error) {
            showError(error);
            return;
        }

        const columns = [
            { label: t('Root.Name'), cell: row => row.name },
            { label: t('Root.Type'), cell: row => row.type },
            {
                label: t('Root.Path'),
                cell: row => {
                    const path = document.createElement('code');
                    path.className = 'mf-mono';
                    path.textContent = row.path;
                    return path;
                }
            },
            {
                label: t('Root.Status'),
                cell: row => !row.isEnabled
                    ? badge(t('Root.Disabled'))
                    : row.type === 'Local'
                        ? badge(row.isAvailable ? t('Root.Available') : t('Root.Missing'), row.isAvailable ? 'ok' : 'warn')
                        : document.createTextNode('')
            },
            { label: t('Root.Roles'), cell: row => String(row.roleCount) },
            {
                label: '', narrow: true,
                cell: row => button('', 'mf-btn mf-btn-tiny mf-btn-danger', () => remove(row), 'trash')
            }
        ];

        view.body.replaceChildren(
            toolbar(value => { search = value; page = 1; list(); }, [
                button(t('AddFolder'), 'mf-btn', () => add(false), 'folder'),
                button(t('AddConnection'), 'mf-btn', () => add(true), 'cloud')
            ]),
            tableOf(columns, data.items, row => form(row.id), t('Root.Empty')),
            pager(data, target => { page = target; list(); }));

        view.foot.replaceChildren(button(t('Close'), 'mf-btn', () => view.dialog.close()));
    }

    async function add(preferRemote) {
        // Der Dialog für Speicherorte ist selbst modal - der Verwaltungsbereich wartet.
        view.dialog.close();

        if (await openRootDialog(null, preferRemote)) {
            flash(t('LocationSaved'));
            changed();
        }

        view.reopen('roots');
    }

    async function form(id) {
        let detail;

        try {
            detail = await api.root(id);
        } catch (error) {
            showError(error);
            return;
        }

        view.title.textContent = detail.name;

        const hint = document.createElement('p');
        hint.className = 'mfx-manage-note';
        hint.textContent = t('Root.AccessHint');

        const heading = document.createElement('h3');
        heading.className = 'mfx-manage-heading';
        heading.textContent = t('Root.Access');

        const boxes = new Map();

        const columns = [{ label: t('Role.Name'), cell: row => row.roleName }].concat(
            RIGHTS.map(([key, label]) => ({
                label: t(label),
                narrow: true,
                cell: row => {
                    const input = document.createElement('input');
                    input.type = 'checkbox';
                    input.checked = !!row[key];
                    input.setAttribute('aria-label', `${row.roleName} · ${t(label)}`);
                    boxes.set(`${row.roleId}.${key}`, input);
                    return input;
                }
            })));

        view.body.replaceChildren(
            heading,
            hint,
            tableOf(columns, detail.access, null, t('Role.Empty')));

        const save = async () => {
            const access = detail.access.map(entry => {
                const values = { roleId: entry.roleId };
                RIGHTS.forEach(([key]) => {
                    values[key] = boxes.get(`${entry.roleId}.${key}`).checked;
                });
                return values;
            });

            try {
                await api.updateRoot(id, { ...toRequest(detail), access });
                flash(t('LocationSaved'));
                changed();
                await list();
            } catch (error) {
                showError(error);
            }
        };

        view.foot.replaceChildren(
            button(t('Common.Save'), 'mf-btn mf-btn-primary', save, 'save'),
            button(t('Common.Back'), 'mf-btn', () => list(), 'chevron-left'),
            button(t('EditLocation'), 'mf-btn', async () => {
                view.dialog.close();

                if (await openRootDialog(detail, false)) {
                    flash(t('LocationSaved'));
                    changed();
                }

                view.reopen('roots');
            }, 'pencil'),
            gap(),
            button(t('Common.Delete'), 'mf-btn mf-btn-danger', () => remove(detail), 'trash'));
    }

    /** Die Verbindungsdaten unverändert zurückschicken - ohne Kennwort bleibt es stehen. */
    function toRequest(detail) {
        return {
            name: detail.name,
            type: detail.type,
            path: detail.path,
            host: detail.host,
            port: detail.port,
            share: detail.share,
            url: detail.url,
            username: detail.username,
            password: null,
            useTls: detail.useTls,
            passive: detail.passive,
            isEnabled: detail.isEnabled,
            icon: detail.icon,
            sortOrder: detail.sortOrder
        };
    }

    async function remove(root) {
        const confirmed = await askConfirm(
            t('RemoveLocation'), t('RemoveLocationQuestion').replace('{0}', root.name), t('RemoveLocation'), true);

        if (!confirmed) {
            return;
        }

        if (await deleteRoot(root.id)) {
            flash(t('LocationRemoved'));
            changed();
            await list();
        }
    }

    list();
}

// ---------------------------------------------------------------- Protokoll

/**
 * Was ist wann passiert - über alle Speicherorte hinweg. Nur zum Lesen;
 * Einträge entstehen bei den Aktionen selbst.
 */
function activitySection(view) {
    let search = '';
    let action = '';
    let rootId = '';
    let page = 1;

    async function list() {
        view.title.textContent = t('Activity.Title');

        let data;

        try {
            data = await api.activityLog({ search, action, rootId, page });
        } catch (error) {
            showError(error);
            return;
        }

        const roots = new Map((data.roots || []).map(root => [root.id, root.name]));

        const columns = [
            { label: t('Activity.When'), cell: row => formatDate(row.date) },
            { label: t('Activity.Who'), cell: row => row.user || row.username || '–' },
            { label: t('Activity.What'), cell: row => t('Action' + row.action, row.action) },
            { label: t('Activity.Where'), cell: row => roots.get(row.rootId) || '–' },
            {
                label: t('Path'),
                cell: row => {
                    const path = document.createElement('code');
                    path.className = 'mf-mono';
                    path.textContent = row.path || '/';
                    return path;
                }
            }
        ];

        view.body.replaceChildren(
            toolbar(value => { search = value; page = 1; list(); }, [
                choice(data.actions, action, t('Activity.AllActions'), value => {
                    action = value;
                    page = 1;
                    list();
                }, value => t('Action' + value, value)),
                choice((data.roots || []).map(root => String(root.id)), rootId, t('Activity.AllRoots'), value => {
                    rootId = value;
                    page = 1;
                    list();
                }, value => roots.get(Number(value)) || value)
            ]),
            tableOf(columns, data.items, null, t('Activity.Empty')),
            pager(data, target => { page = target; list(); }));

        view.foot.replaceChildren(
            button(t('Refresh'), 'mf-btn', () => list(), 'refresh'),
            button(t('Close'), 'mf-btn', () => view.dialog.close()));
    }

    list();
}

/** Auswahlfeld für die Filterleiste - erster Eintrag steht für „alle". */
function choice(values, selected, allLabel, onChange, labelOf) {
    const select = document.createElement('select');
    select.setAttribute('aria-label', allLabel);

    const all = document.createElement('option');
    all.value = '';
    all.textContent = allLabel;
    select.appendChild(all);

    (values || []).forEach(value => {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = labelOf ? labelOf(value) : value;
        option.selected = String(selected) === String(value);
        select.appendChild(option);
    });

    select.addEventListener('change', () => onChange(select.value));
    return select;
}

// ---------------------------------------------------------------- Einstieg

const SECTIONS = [
    { key: 'users', right: 'manageUsers', icon: 'users', label: 'Admin.Users', open: usersSection },
    { key: 'roles', right: 'manageRoles', icon: 'shield', label: 'Admin.Roles', open: rolesSection },
    { key: 'roots', right: 'manageRoots', icon: 'hard', label: 'Admin.Roots', open: rootsSection },
    { key: 'system', right: 'manageSettings', icon: 'settings', label: 'System.Title', open: systemSection },
    { key: 'activity', right: 'viewActivityLog', icon: 'clock', label: 'Activity.Title', open: activitySection }
];

/**
 * Öffnet den Administrationsbereich.
 * `options.section` wählt den Bereich, `options.can` entscheidet über die Spalte,
 * `options.onRootsChanged` meldet geänderte Speicherorte an den Explorer.
 */
export function openAdmin(options) {
    const settings = options || {};
    const allowed = SECTIONS
        .filter(section => (settings.can || {})[section.right])
        .map(section => ({ ...section, label: t(section.label) }));

    if (allowed.length === 0) {
        return null;
    }

    const view = frame(allowed, settings.section);
    view.onRootsChanged = settings.onRootsChanged;
    view.reopen = section => openAdmin({ ...settings, section });
    return view;
}
