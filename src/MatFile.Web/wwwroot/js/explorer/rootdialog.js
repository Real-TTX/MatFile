// Dialog zum Anlegen und Bearbeiten eines Speicherorts - direkt im Explorer.
import { api, t, icon, errorKey } from './api.js';
import { flash } from './dialogs.js';

/**
 * Auswahlkacheln. Jeder Typ hat sein festes Symbol und seine Farbe - eine
 * eigene Symbolauswahl braucht es damit nicht mehr.
 */
const TYPES = [
    { value: 'Local', icon: 'folder', label: 'TypeLocal' },
    { value: 'Smb', icon: 'server', label: 'SMB', raw: true },
    { value: 'Sftp', icon: 'key', label: 'SFTP', raw: true },
    { value: 'Ftp', icon: 'exchange', label: 'FTP', raw: true },
    { value: 'WebDav', icon: 'cloud', label: 'WebDAV', raw: true }
];

/** Welche Felder gehören zu welchem Typ? */
const FIELDS = {
    host: ['Smb', 'Sftp', 'Ftp'],
    port: ['Smb', 'Sftp', 'Ftp'],
    url: ['WebDav'],
    share: ['Smb'],
    username: ['Smb', 'Sftp', 'Ftp', 'WebDav'],
    password: ['Smb', 'Sftp', 'Ftp', 'WebDav'],
    useTls: ['Ftp', 'WebDav'],
    passive: ['Ftp']
};

/** Schliessen-Kreuz für die Kopfzeile - wie in den übrigen Dialogen. */
function closeButton(dialog) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'mfx-btn mfx-dialog-close';
    button.title = t('Close');
    button.setAttribute('aria-label', t('Close'));
    button.appendChild(icon('x', 14));
    button.addEventListener('click', () => dialog.close());
    return button;
}

function field(label, control, hint) {
    const wrapper = document.createElement('div');
    wrapper.className = 'mf-field';

    if (label) {
        const caption = document.createElement('label');
        caption.textContent = label;
        wrapper.appendChild(caption);
    }

    wrapper.appendChild(control);

    if (hint) {
        const note = document.createElement('span');
        note.className = 'mf-field-hint';
        note.textContent = hint;
        wrapper.appendChild(note);
    }

    return wrapper;
}

function input(type, value, placeholder) {
    const element = document.createElement('input');
    element.type = type;
    element.value = value ?? '';

    if (placeholder) {
        element.placeholder = placeholder;
    }

    return element;
}

/** Abschnitt mit Symbol und Überschrift. Ohne Titel bleibt die Überschrift weg. */
function section(iconName, title) {
    const box = document.createElement('section');
    box.className = 'mfx-dialog-section';

    if (title) {
        const heading = document.createElement('h3');
        heading.appendChild(icon(iconName, 14));
        heading.appendChild(Object.assign(document.createElement('span'), { textContent: title }));
        box.appendChild(heading);
    }

    const body = document.createElement('div');
    body.className = 'mf-form-grid';
    box.appendChild(body);

    return { box, body };
}

/** Aufklappbarer Bereich für alles, was man selten braucht. */
function advancedSection(title) {
    const box = document.createElement('details');
    box.className = 'mfx-dialog-section mfx-advanced';

    const summary = document.createElement('summary');
    summary.appendChild(icon('settings', 14));
    summary.appendChild(Object.assign(document.createElement('span'), { textContent: title }));
    box.appendChild(summary);

    const body = document.createElement('div');
    body.className = 'mf-form-grid';
    box.appendChild(body);

    return { box, body };
}

/**
 * Ordnerauswahl auf der Gegenstelle - mit den gerade eingetragenen Zugangsdaten,
 * also schon bevor der Speicherort gespeichert ist.
 * Liefert { path, share } oder null beim Abbrechen.
 */
function browseRemote(connection, startPath) {
    return new Promise(resolve => {
        const dialog = document.createElement('dialog');
        dialog.className = 'mfx-dialog mfx-browse-dialog';

        const head = document.createElement('div');
        head.className = 'mfx-dialog-head';
        head.appendChild(icon('folder', 20));
        head.appendChild(Object.assign(document.createElement('h2'), { textContent: t('ChooseFolder') }));
        head.appendChild(Object.assign(document.createElement('span'), { className: 'mf-spacer' }));
        head.appendChild(closeButton(dialog));
        dialog.appendChild(head);

        const crumb = document.createElement('div');
        crumb.className = 'mfx-browse-path mf-mono';
        dialog.appendChild(crumb);

        const list = document.createElement('ul');
        list.className = 'mfx-browse-list';
        dialog.appendChild(list);

        const actions = document.createElement('div');
        actions.className = 'mf-actions';

        const take = document.createElement('button');
        take.type = 'button';
        take.className = 'mf-btn mf-btn-primary';
        take.textContent = t('Apply');

        const cancel = document.createElement('button');
        cancel.type = 'button';
        cancel.className = 'mf-btn';
        cancel.textContent = t('Cancel');
        cancel.addEventListener('click', () => dialog.close());

        actions.append(take, cancel);
        dialog.appendChild(actions);

        let share = connection.share || '';
        let path = startPath || '';
        let answer = null;

        const message = text => {
            const item = document.createElement('li');
            item.className = 'mfx-browse-message mf-muted';
            item.textContent = text;
            return item;
        };

        const row = (label, iconName, onOpen) => {
            const item = document.createElement('li');

            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'mfx-browse-item';
            button.appendChild(icon(iconName, 16));
            button.appendChild(Object.assign(document.createElement('span'), { textContent: label }));
            button.addEventListener('click', onOpen);

            item.appendChild(button);
            return item;
        };

        async function load(target) {
            list.replaceChildren(message(t('Loading')));

            let result;

            try {
                result = await api.browseRoot({ ...connection, share }, target);
            } catch (error) {
                list.replaceChildren(message(t(errorKey(error && error.code), t('Error'))));
                return;
            }

            path = result.path || '';
            crumb.textContent = result.mode === 'shares'
                ? t('ChooseShare')
                : `${share ? share + ' · ' : ''}/${path}`;

            list.replaceChildren();

            if (result.mode === 'shares') {
                if (result.entries.length === 0) {
                    list.appendChild(message(t('EmptyFolder')));
                }

                result.entries.forEach(entry => list.appendChild(row(entry.name, 'server', () => {
                    share = entry.name;
                    load('');
                })));

                return;
            }

            if (result.parentPath !== null && result.parentPath !== undefined) {
                list.appendChild(row('..', 'arrow-left', () => load(result.parentPath)));
            }

            if (result.entries.length === 0) {
                list.appendChild(message(t('EmptyFolder')));
            }

            result.entries.forEach(entry => list.appendChild(row(entry.name, 'folder', () => load(entry.path))));
        }

        take.addEventListener('click', () => {
            answer = { path, share };
            dialog.close();
        });

        dialog.addEventListener('close', () => {
            dialog.remove();
            resolve(answer);
        });

        document.body.appendChild(dialog);
        dialog.showModal();
        load(path);
    });
}

/**
 * Öffnet den Dialog. Liefert true, wenn gespeichert wurde.
 * @param root vorhandener Speicherort (aus /api/roots/{id}) oder null für einen neuen
 * @param preferRemote bei einem neuen Eintrag mit einem Verbindungstyp starten
 */
export function openRootDialog(root, preferRemote) {
    return new Promise(resolve => {
        const dialog = document.createElement('dialog');
        dialog.className = 'mfx-dialog mfx-root-dialog';

        let selectedType = root?.type || (preferRemote ? 'Smb' : 'Local');

        // ---------------------------------------------------------------- Kopf
        const head = document.createElement('div');
        head.className = 'mfx-dialog-head';

        const headIcon = document.createElement('span');
        headIcon.className = 'mfx-head-icon';
        head.appendChild(headIcon);

        const title = document.createElement('h2');
        head.appendChild(title);
        head.appendChild(Object.assign(document.createElement('span'), { className: 'mf-spacer' }));
        head.appendChild(closeButton(dialog));
        dialog.appendChild(head);

        // Mittelteil scrollt, Kopf und Fuss bleiben stehen.
        const content = document.createElement('div');
        content.className = 'mfx-dialog-body';
        dialog.appendChild(content);

        // ---------------------------------------------------------------- Typ als Kacheln
        // Ohne Überschrift und ohne Erklärzeile - das Symbol und der Name sagen genug.
        const typeSection = section(null, null);
        typeSection.body.className = 'mfx-type-grid';

        const tiles = TYPES.map(entry => {
            const tile = document.createElement('button');
            tile.type = 'button';
            tile.className = 'mfx-type-tile';
            tile.dataset.value = entry.value;

            const symbol = document.createElement('span');
            symbol.className = 'mfx-type-icon';
            symbol.appendChild(icon(entry.icon, 20));
            tile.appendChild(symbol);

            const label = document.createElement('span');
            label.className = 'mfx-type-label';
            label.textContent = entry.raw ? entry.label : t(entry.label);
            tile.appendChild(label);

            tile.addEventListener('click', () => {
                selectedType = entry.value;
                apply();
            });

            typeSection.body.appendChild(tile);
            return tile;
        });

        content.appendChild(typeSection.box);

        // ---------------------------------------------------------------- Anzeige
        const general = section(null, null);
        const name = input('text', root?.name, '');
        const nameField = field(t('Name'), name);
        nameField.classList.add('mf-field-wide');
        general.body.appendChild(nameField);
        content.appendChild(general.box);

        // ---------------------------------------------------------------- Ziel
        const target = section('hard', t('SectionTarget'));

        const host = input('text', root?.host, '192.168.1.100');
        const url = input('text', root?.url, 'https://nas.example.com/remote.php/dav/files/user');
        url.className = 'mf-mono';
        const share = input('text', root?.share, 'data');
        const path = input('text', root?.path, '/storage/tank');
        path.className = 'mf-mono';

        const pathHint = document.createElement('span');
        pathHint.className = 'mf-field-hint';

        // Pfad mit Auswahl: der Knopf öffnet den Ordnerbrowser der Gegenstelle.
        const pathRow = document.createElement('div');
        pathRow.className = 'mfx-path-row';

        const browse = document.createElement('button');
        browse.type = 'button';
        browse.className = 'mf-btn mfx-path-browse';
        browse.appendChild(icon('folder', 14));
        browse.appendChild(Object.assign(document.createElement('span'), { textContent: t('Browse') }));

        pathRow.append(path, browse);

        const blocks = {
            host: field(t('Server'), host),
            url: field(t('Address'), url),
            share: field(t('Share'), share),
            path: field(t('Path'), pathRow)
        };

        blocks.path.appendChild(pathHint);

        // Adresse und Pfad bekommen die ganze Zeile, der Rest steht zweispaltig.
        blocks.url.classList.add('mf-field-wide');
        blocks.path.classList.add('mf-field-wide');

        Object.values(blocks).forEach(block => target.body.appendChild(block));
        content.appendChild(target.box);

        // ---------------------------------------------------------------- Anmeldung
        const login = section('lock', t('SectionLogin'));

        const username = input('text', root?.username, '');
        const password = input('password', '', '');
        const usernameField = field(t('User'), username);
        const passwordField = field(t('Password'), password, root?.hasPassword ? t('PasswordStored') : null);

        login.body.appendChild(usernameField);
        login.body.appendChild(passwordField);

        // Der Hinweis zur Verschlüsselung gehört zu den Zugangsdaten - dort steht er auch.
        const note = document.createElement('p');
        note.className = 'mf-field-hint mf-field-wide';
        note.textContent = t('LocationHint');
        login.body.appendChild(note);

        content.appendChild(login.box);

        // ---------------------------------------------------------------- Erweitert
        // Alles, was Standardwerte hat und selten angefasst wird.
        const advanced = advancedSection(t('Advanced'));

        const port = input('number', root?.port ?? 0);
        port.min = 0;
        port.max = 65535;

        const sortOrder = input('number', root?.sortOrder ?? 0);
        sortOrder.min = 0;

        const useTls = input('checkbox');
        useTls.checked = !!root?.useTls;
        const passive = input('checkbox');
        passive.checked = root ? !!root.passive : true;
        const enabled = input('checkbox');
        enabled.checked = root ? !!root.isEnabled : true;

        const portField = field(t('Port'), port, t('PortHint'));
        advanced.body.appendChild(portField);
        advanced.body.appendChild(field(t('SortOrder'), sortOrder, t('SortOrderHint')));

        const options = document.createElement('div');
        options.className = 'mf-field mf-field-wide mfx-option-row';

        const check = (element, label) => {
            const wrapper = document.createElement('label');
            wrapper.className = 'mf-check';
            wrapper.appendChild(element);
            wrapper.appendChild(Object.assign(document.createElement('span'), { textContent: label }));
            options.appendChild(wrapper);
            return wrapper;
        };

        const enabledLabel = check(enabled, t('Enabled'));
        const tlsLabel = check(useTls, t('UseTls'));
        const passiveLabel = check(passive, t('Passive'));

        advanced.body.appendChild(options);
        content.appendChild(advanced.box);

        // ---------------------------------------------------------------- Fuss
        const status = document.createElement('div');
        status.className = 'mfx-dialog-status';
        status.hidden = true;
        content.appendChild(status);

        const actions = document.createElement('div');
        actions.className = 'mf-actions';

        const save = document.createElement('button');
        save.type = 'button';
        save.className = 'mf-btn mf-btn-primary';
        save.appendChild(icon('save', 14));
        save.appendChild(Object.assign(document.createElement('span'), { textContent: t('Save') }));

        const test = document.createElement('button');
        test.type = 'button';
        test.className = 'mf-btn';
        test.appendChild(icon('refresh', 14));
        test.appendChild(Object.assign(document.createElement('span'), { textContent: t('TestConnection') }));

        const cancel = document.createElement('button');
        cancel.type = 'button';
        cancel.className = 'mf-btn';
        cancel.textContent = t('Cancel');
        cancel.addEventListener('click', () => dialog.close());

        actions.appendChild(save);
        actions.appendChild(test);
        actions.appendChild(cancel);
        dialog.appendChild(actions);

        const show = (message, state) => {
            status.hidden = false;
            status.className = 'mfx-dialog-status is-' + state;
            status.replaceChildren(
                icon(state === 'ok' ? 'check' : 'alert', 14),
                Object.assign(document.createElement('span'), { textContent: message }));
        };

        // ---------------------------------------------------------------- Sichtbarkeit
        function apply() {
            tiles.forEach(tile => tile.classList.toggle('is-active', tile.dataset.value === selectedType));
            dialog.dataset.type = selectedType;

            // Kopfzeile folgt dem gewählten Typ.
            const entry = TYPES.find(item => item.value === selectedType) || TYPES[0];
            headIcon.replaceChildren(icon(entry.icon, 20));
            title.textContent = root
                ? t('EditLocation')
                : (selectedType === 'Local' ? t('AddFolder') : t('AddConnection'));

            blocks.host.hidden = !FIELDS.host.includes(selectedType);
            blocks.url.hidden = !FIELDS.url.includes(selectedType);
            blocks.share.hidden = !FIELDS.share.includes(selectedType);

            usernameField.hidden = !FIELDS.username.includes(selectedType);
            passwordField.hidden = !FIELDS.password.includes(selectedType);
            login.box.hidden = usernameField.hidden && passwordField.hidden;

            portField.hidden = !FIELDS.port.includes(selectedType);
            tlsLabel.hidden = !FIELDS.useTls.includes(selectedType);
            passiveLabel.hidden = !FIELDS.passive.includes(selectedType);

            test.hidden = selectedType === 'Local';

            // Beim lokalen Ordner ist der Pfad die Hauptsache, bei einer Verbindung nur ein Unterordner.
            path.placeholder = selectedType === 'Local' ? '/storage/tank' : '/';
            pathHint.textContent = selectedType === 'Local' ? t('PathHint') : t('PathHintRemote');
        }

        const payload = () => ({
            name: name.value.trim(),
            type: selectedType,
            path: path.value.trim(),
            host: host.value.trim(),
            port: Number(port.value) || 0,
            share: share.value.trim(),
            url: url.value.trim(),
            username: username.value.trim(),
            password: password.value,
            useTls: useTls.checked,
            passive: passive.checked,
            isEnabled: enabled.checked,
            // Das Symbol ergibt sich aus dem Typ - es gibt keine eigene Auswahl mehr.
            icon: null,
            sortOrder: Number(sortOrder.value) || 0
        });

        // Ordner oder Freigabe auf der Gegenstelle auswählen.
        browse.addEventListener('click', async () => {
            const chosen = await browseRemote(payload(), path.value.trim());

            if (chosen === null) {
                return;
            }

            if (chosen.share) {
                share.value = chosen.share;
            }

            path.value = chosen.path;
            path.focus();
        });

        test.addEventListener('click', async () => {
            test.disabled = true;
            show(t('Testing'), 'busy');

            try {
                const result = await api.testRoot(payload());
                show(result.ok ? t('TestOk') : (result.error || t('Error')), result.ok ? 'ok' : 'error');
            } catch (exception) {
                show(t('Error'), 'error');
            } finally {
                test.disabled = false;
            }
        });

        let saved = false;

        save.addEventListener('click', async () => {
            const data = payload();

            if (data.name.length === 0) {
                show(t('NameRequired'), 'error');
                name.focus();
                return;
            }

            save.disabled = true;

            try {
                if (root) {
                    await api.updateRoot(root.id, data);
                } else {
                    await api.createRoot(data);
                }

                saved = true;
                dialog.close();
            } catch (exception) {
                show(exception.code && exception.code !== 'error' ? exception.code : t('Error'), 'error');
            } finally {
                save.disabled = false;
            }
        });

        apply();

        dialog.addEventListener('close', () => {
            dialog.remove();
            resolve(saved);
        });

        document.body.appendChild(dialog);
        dialog.showModal();
        name.focus();
    });
}

/** Speicherort entfernen, nach Rückfrage durch den Aufrufer. */
export async function deleteRoot(id) {
    try {
        await api.deleteRoot(id);
        return true;
    } catch (exception) {
        flash(t('Error'), true);
        return false;
    }
}
