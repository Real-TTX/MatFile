// Dialog zum Anlegen und Bearbeiten eines Speicherorts - direkt im Explorer.
import { api, t } from './api.js';
import { flash } from './dialogs.js';

const TYPES = ['Local', 'Smb', 'Sftp', 'Ftp', 'WebDav'];

/** Welche Verbindungsfelder gehören zu welchem Typ? */
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

/**
 * Öffnet den Dialog. Liefert true, wenn gespeichert wurde.
 * @param root vorhandener Speicherort (aus /api/roots/{id}) oder null für einen neuen
 * @param preferRemote bei einem neuen Eintrag mit einem Verbindungstyp starten
 */
export function openRootDialog(root, preferRemote) {
    return new Promise(resolve => {
        const dialog = document.createElement('dialog');
        dialog.className = 'mfx-dialog mfx-root-dialog';

        const title = document.createElement('h2');
        title.textContent = root ? t('EditLocation') : (preferRemote ? t('AddConnection') : t('AddFolder'));
        dialog.appendChild(title);

        const name = input('text', root?.name, '');
        const type = document.createElement('select');

        TYPES.forEach(value => {
            const option = document.createElement('option');
            option.value = value;
            option.textContent = value === 'Local' ? t('TypeLocal') : value;
            type.appendChild(option);
        });

        type.value = root?.type || (preferRemote ? 'Smb' : 'Local');

        const path = input('text', root?.path, '/storage/tank');
        path.className = 'mf-mono';

        const host = input('text', root?.host, '192.168.1.100');
        const port = input('number', root?.port ?? 0);
        port.min = 0;
        port.max = 65535;
        const url = input('text', root?.url, 'https://nas.example.com/remote.php/dav/files/user');
        url.className = 'mf-mono';
        const share = input('text', root?.share, 'data');
        const username = input('text', root?.username, '');
        const password = input('password', '', '');

        const useTls = input('checkbox');
        useTls.checked = !!root?.useTls;
        const passive = input('checkbox');
        passive.checked = root ? !!root.passive : true;

        const grid = document.createElement('div');
        grid.className = 'mf-form-grid';

        const blocks = {
            name: field(t('Name'), name),
            type: field(t('Type'), type),
            path: field(t('Path'), path, t('PathHint')),
            host: field(t('Server'), host),
            port: field(t('Port'), port, t('PortHint')),
            url: field(t('Address'), url),
            share: field(t('Share'), share),
            username: field(t('User'), username),
            password: field(t('Password'), password, root?.hasPassword ? t('PasswordStored') : null)
        };

        blocks.path.classList.add('mf-field-wide');
        blocks.url.classList.add('mf-field-wide');

        Object.values(blocks).forEach(block => grid.appendChild(block));

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
        grid.appendChild(options);

        dialog.appendChild(grid);

        const note = document.createElement('p');
        note.className = 'mf-muted mf-small';
        note.textContent = t('LocationHint');
        dialog.appendChild(note);

        // Felder je Typ ein- und ausblenden
        const apply = () => {
            const value = type.value;
            blocks.host.hidden = !FIELDS.host.includes(value);
            blocks.port.hidden = !FIELDS.port.includes(value);
            blocks.url.hidden = !FIELDS.url.includes(value);
            blocks.share.hidden = !FIELDS.share.includes(value);
            blocks.username.hidden = !FIELDS.username.includes(value);
            blocks.password.hidden = !FIELDS.password.includes(value);
            tlsLabel.hidden = !FIELDS.useTls.includes(value);
            passiveLabel.hidden = !FIELDS.passive.includes(value);
            options.hidden = tlsLabel.hidden && passiveLabel.hidden;
            test.hidden = value === 'Local';
        };

        const payload = () => ({
            name: name.value.trim(),
            type: type.value,
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
            icon: root?.icon || null,
            sortOrder: root?.sortOrder ?? 0
        });

        const actions = document.createElement('div');
        actions.className = 'mf-actions';

        const save = document.createElement('button');
        save.type = 'button';
        save.className = 'mf-btn mf-btn-primary';
        save.textContent = t('Save');

        const test = document.createElement('button');
        test.type = 'button';
        test.className = 'mf-btn';
        test.textContent = t('TestConnection');

        const cancel = document.createElement('button');
        cancel.type = 'button';
        cancel.className = 'mf-btn';
        cancel.textContent = t('Cancel');
        cancel.addEventListener('click', () => dialog.close());

        actions.appendChild(save);
        actions.appendChild(test);
        actions.appendChild(cancel);
        dialog.appendChild(actions);

        const error = document.createElement('div');
        error.className = 'mf-alert mf-alert-error';
        error.hidden = true;
        dialog.appendChild(error);

        const show = (message, ok) => {
            error.hidden = false;
            error.textContent = message;
            error.className = 'mf-alert ' + (ok ? 'mf-alert-success' : 'mf-alert-error');
        };

        test.addEventListener('click', async () => {
            test.disabled = true;

            try {
                const result = await api.testRoot(payload());
                show(result.ok ? t('TestOk') : (result.error || t('Error')), result.ok);
            } catch (exception) {
                show(t('Error'), false);
            } finally {
                test.disabled = false;
            }
        });

        let saved = false;

        save.addEventListener('click', async () => {
            const data = payload();

            if (data.name.length === 0) {
                show(t('NameRequired'), false);
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
                show(exception.code === 'error' ? t('Error') : (exception.code || t('Error')), false);
            } finally {
                save.disabled = false;
            }
        });

        type.addEventListener('change', apply);
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
