// Kleine Dialoge auf Basis von <dialog> - lokalisiert, ohne Bibliothek.
import { t } from './api.js';

function build(title) {
    const dialog = document.createElement('dialog');
    dialog.className = 'mfx-dialog';

    const heading = document.createElement('h2');
    heading.textContent = title;
    dialog.appendChild(heading);

    return dialog;
}

function actions(dialog, confirmLabel, danger, onConfirm) {
    const row = document.createElement('div');
    row.className = 'mf-actions';

    const confirm = document.createElement('button');
    confirm.type = 'button';
    confirm.className = 'mf-btn ' + (danger ? 'mf-btn-danger' : 'mf-btn-primary');
    confirm.textContent = confirmLabel;
    confirm.addEventListener('click', onConfirm);

    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.className = 'mf-btn';
    cancel.textContent = t('Cancel');
    cancel.addEventListener('click', () => dialog.close());

    if (danger) {
        row.appendChild(cancel);
        row.appendChild(document.createElement('span')).className = 'mf-actions-gap';
        row.appendChild(confirm);
    } else {
        row.appendChild(confirm);
        row.appendChild(cancel);
    }

    dialog.appendChild(row);
    return confirm;
}

/** Fragt einen Namen ab. Liefert null beim Abbrechen. */
export function askName(title, label, value) {
    return new Promise(resolve => {
        const dialog = build(title);
        let answered = null;

        const field = document.createElement('div');
        field.className = 'mf-field';

        const caption = document.createElement('label');
        caption.textContent = label;
        field.appendChild(caption);

        const input = document.createElement('input');
        input.type = 'text';
        input.value = value || '';
        field.appendChild(input);
        dialog.appendChild(field);

        const confirm = () => {
            const name = input.value.trim();
            if (name.length === 0) {
                input.focus();
                return;
            }
            answered = name;
            dialog.close();
        };

        actions(dialog, t('Save'), false, confirm);

        input.addEventListener('keydown', event => {
            if (event.key === 'Enter') {
                event.preventDefault();
                confirm();
            }
        });

        dialog.addEventListener('close', () => {
            dialog.remove();
            resolve(answered);
        });

        document.body.appendChild(dialog);
        dialog.showModal();

        input.focus();
        const dot = input.value.lastIndexOf('.');
        input.setSelectionRange(0, dot > 0 ? dot : input.value.length);
    });
}

/** Sicherheitsabfrage. Liefert true, wenn bestätigt wurde. */
export function askConfirm(title, message, confirmLabel, danger) {
    return new Promise(resolve => {
        const dialog = build(title);
        let answered = false;

        const text = document.createElement('p');
        text.className = 'mf-muted';
        text.textContent = message;
        dialog.appendChild(text);

        actions(dialog, confirmLabel, danger, () => {
            answered = true;
            dialog.close();
        });

        dialog.addEventListener('close', () => {
            dialog.remove();
            resolve(answered);
        });

        document.body.appendChild(dialog);
        dialog.showModal();
    });
}

/** Zeigt eine Liste aus Bezeichnung und Wert. */
export function showFacts(title, facts) {
    const dialog = build(title);

    const list = document.createElement('dl');
    list.className = 'mf-facts';

    facts.forEach(([label, value]) => {
        const dt = document.createElement('dt');
        dt.textContent = label;
        const dd = document.createElement('dd');
        dd.textContent = value;
        list.appendChild(dt);
        list.appendChild(dd);
    });

    dialog.appendChild(list);

    const row = document.createElement('div');
    row.className = 'mf-actions';

    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'mf-btn mf-btn-primary';
    close.textContent = t('Close');
    close.addEventListener('click', () => dialog.close());
    row.appendChild(close);
    dialog.appendChild(row);

    dialog.addEventListener('close', () => dialog.remove());
    document.body.appendChild(dialog);
    dialog.showModal();
}

/** Kurze Rückmeldung unten rechts. */
export function flash(message, isError) {
    const note = document.createElement('div');
    note.className = 'mfx-flash' + (isError ? ' is-error' : '');
    note.textContent = message;
    document.body.appendChild(note);
    setTimeout(() => note.remove(), isError ? 4000 : 2000);
}
