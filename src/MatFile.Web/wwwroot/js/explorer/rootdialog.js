// Dialog zum Anlegen und Bearbeiten eines Speicherorts - direkt im Explorer.
import { api, t, icon } from './api.js';
import { flash } from './dialogs.js';

/** Auswahlkacheln: Symbol, Bezeichnung und eine Zeile Erklärung. */
const TYPES = [
    { value: 'Local', icon: 'folder', label: 'TypeLocal', sub: 'TypeLocalSub' },
    { value: 'Smb', icon: 'server', label: 'SMB', sub: 'TypeSmbSub', raw: true },
    { value: 'Sftp', icon: 'key', label: 'SFTP', sub: 'TypeSftpSub', raw: true },
    { value: 'Ftp', icon: 'exchange', label: 'FTP', sub: 'TypeFtpSub', raw: true },
    { value: 'WebDav', icon: 'cloud', label: 'WebDAV', sub: 'TypeWebDavSub', raw: true }
];

/** Symbole für die Seitenleiste. */
const ICONS = ['folder', 'hard', 'server', 'cloud', 'key', 'exchange', 'lock', 'users', 'shield'];

/** Vorschlag passend zum Typ, solange nichts eigenes gewählt wurde. */
function defaultIcon(type) {
    return (TYPES.find(entry => entry.value === type) || TYPES[0]).icon;
}

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

/** Abschnitt mit Symbol und Überschrift. */
function section(iconName, title) {
    const box = document.createElement('section');
    box.className = 'mfx-dialog-section';

    const heading = document.createElement('h3');
    heading.appendChild(icon(iconName, 14));
    heading.appendChild(Object.assign(document.createElement('span'), { textContent: title }));
    box.appendChild(heading);

    const body = document.createElement('div');
    body.className = 'mf-form-grid';
    box.appendChild(body);

    return { box, body };
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
        let selectedIcon = root?.icon || defaultIcon(selectedType);
        let iconChosen = !!root?.icon;

        // ---------------------------------------------------------------- Kopf
        const head = document.createElement('div');
        head.className = 'mfx-dialog-head';
        head.appendChild(icon(preferRemote || (root && root.type !== 'Local') ? 'cloud' : 'folder', 22));

        const title = document.createElement('h2');
        title.textContent = root ? t('EditLocation') : (preferRemote ? t('AddConnection') : t('AddFolder'));
        head.appendChild(title);
        dialog.appendChild(head);

        // Mittelteil scrollt, Kopf und Fuss bleiben stehen.
        const content = document.createElement('div');
        content.className = 'mfx-dialog-body';
        dialog.appendChild(content);

        // ---------------------------------------------------------------- Typ als Kacheln
        const typeSection = section('server', t('SectionType'));
        typeSection.body.className = 'mfx-type-grid';

        const tiles = TYPES.map(entry => {
            const tile = document.createElement('button');
            tile.type = 'button';
            tile.className = 'mfx-type-tile';
            tile.dataset.value = entry.value;
            tile.appendChild(icon(entry.icon, 24));

            const label = document.createElement('span');
            label.className = 'mfx-type-label';
            label.textContent = entry.raw ? entry.label : t(entry.label);
            tile.appendChild(label);

            const sub = document.createElement('span');
            sub.className = 'mfx-type-sub';
            sub.textContent = t(entry.sub);
            tile.appendChild(sub);

            tile.addEventListener('click', () => {
                selectedType = entry.value;

                // Ohne eigene Wahl folgt das Symbol dem Typ.
                if (!iconChosen) {
                    selectedIcon = defaultIcon(entry.value);
                }

                apply();
            });

            typeSection.body.appendChild(tile);
            return tile;
        });

        content.appendChild(typeSection.box);

        // ---------------------------------------------------------------- Anzeige
        const general = section('info', t('SectionGeneral'));
        const name = input('text', root?.name, '');
        const nameField = field(t('Name'), name);
        general.body.appendChild(nameField);

        const picker = document.createElement('div');
        picker.className = 'mfx-icon-picker';

        const iconButtons = ICONS.map(value => {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'mfx-icon-choice';
            button.dataset.value = value;
            button.title = value;
            button.appendChild(icon(value, 16));
            button.addEventListener('click', () => {
                selectedIcon = value;
                iconChosen = true;
                apply();
            });
            picker.appendChild(button);
            return button;
        });

        general.body.appendChild(field(t('Icon'), picker));
        content.appendChild(general.box);

        // ---------------------------------------------------------------- Ziel
        const target = section('hard', t('SectionTarget'));

        const host = input('text', root?.host, '192.168.1.100');
        const port = input('number', root?.port ?? 0);
        port.min = 0;
        port.max = 65535;
        const url = input('text', root?.url, 'https://nas.example.com/remote.php/dav/files/user');
        url.className = 'mf-mono';
        const share = input('text', root?.share, 'data');
        const path = input('text', root?.path, '/storage/tank');
        path.className = 'mf-mono';

        const blocks = {
            host: field(t('Server'), host),
            port: field(t('Port'), port, t('PortHint')),
            url: field(t('Address'), url),
            share: field(t('Share'), share),
            path: field(t('Path'), path, t('PathHint'))
        };

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

        const useTls = input('checkbox');
        useTls.checked = !!root?.useTls;
        const passive = input('checkbox');
        passive.checked = root ? !!root.passive : true;

        const options = document.createElement('div');
        options.className = 'mf-field mf-field-wide';

        const tlsLabel = document.createElement('label');
        tlsLabel.className = 'mf-check';
        tlsLabel.appendChild(useTls);
        tlsLabel.appendChild(Object.assign(document.createElement('span'), { textContent: t('UseTls') }));

        const passiveLabel = document.createElement('label');
        passiveLabel.className = 'mf-check';
        passiveLabel.appendChild(passive);
        passiveLabel.appendChild(Object.assign(document.createElement('span'), { textContent: t('Passive') }));

        options.appendChild(tlsLabel);
        options.appendChild(passiveLabel);
        login.body.appendChild(options);
        content.appendChild(login.box);

        // ---------------------------------------------------------------- Fuss
        const status = document.createElement('div');
        status.className = 'mfx-dialog-status';
        status.hidden = true;
        content.appendChild(status);

        const note = document.createElement('p');
        note.className = 'mf-muted mf-small';
        note.textContent = t('LocationHint');
        content.appendChild(note);

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
            iconButtons.forEach(button => button.classList.toggle('is-active', button.dataset.value === selectedIcon));

            blocks.host.hidden = !FIELDS.host.includes(selectedType);
            blocks.port.hidden = !FIELDS.port.includes(selectedType);
            blocks.url.hidden = !FIELDS.url.includes(selectedType);
            blocks.share.hidden = !FIELDS.share.includes(selectedType);

            usernameField.hidden = !FIELDS.username.includes(selectedType);
            passwordField.hidden = !FIELDS.password.includes(selectedType);
            tlsLabel.hidden = !FIELDS.useTls.includes(selectedType);
            passiveLabel.hidden = !FIELDS.passive.includes(selectedType);
            options.hidden = tlsLabel.hidden && passiveLabel.hidden;

            login.box.hidden = usernameField.hidden && passwordField.hidden && options.hidden;
            test.hidden = selectedType === 'Local';

            path.placeholder = selectedType === 'Local' ? '/storage/tank' : '/';
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
            isEnabled: root ? root.isEnabled : true,
            icon: selectedIcon,
            sortOrder: root?.sortOrder ?? 0
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
