// Kleine Dialoge auf Basis von <dialog> - lokalisiert, ohne Bibliothek.
// Bauform wie bei den grossen: Kopfzeile, Inhalt, Fusszeile - jeweils bis zum Rand.
import { t, icon } from './api.js';

/**
 * Rahmen eines Dialogs. Zurück kommt der Dialog selbst und der Mittelteil,
 * in den der Aufrufer seinen Inhalt hängt.
 */
function build(title, iconName) {
    const dialog = document.createElement('dialog');
    dialog.className = 'mfx-dialog';

    const head = document.createElement('header');
    head.className = 'mfx-dialog-head';

    if (iconName) {
        head.appendChild(icon(iconName, 20));
    }

    const heading = document.createElement('h2');
    heading.textContent = title;
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

    const body = document.createElement('div');
    body.className = 'mfx-dialog-body mfx-dialog-pad';

    dialog.append(head, body);
    return { dialog, body };
}

function actions(dialog, confirmLabel, danger, onConfirm) {
    const row = document.createElement('div');
    row.className = 'mf-actions mfx-dialog-foot';

    const confirm = document.createElement('button');
    confirm.type = 'button';
    confirm.className = 'mf-btn ' + (danger ? 'mf-btn-danger' : 'mf-btn-primary');
    confirm.textContent = confirmLabel;
    // Ohne diese Vorgabe landet der Fokus beim Öffnen auf dem Schliessen-Kreuz.
    confirm.autofocus = true;
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
        const { dialog, body } = build(title, 'pencil');
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
        body.appendChild(field);

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

/** Gängige Endungen für eine neue Datei. */
const FILE_TYPES = ['.txt', '.md', '.json', '.xml', '.yml', '.csv', '.log', '.sh'];

/**
 * Fragt einen Dateinamen ab und bietet die üblichen Endungen zum Anklicken an.
 * Liefert null beim Abbrechen.
 */
export function askFileName(title, label, value) {
    return new Promise(resolve => {
        const { dialog, body } = build(title, 'file-plus');
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
        body.appendChild(field);

        // Endung anklicken: der Name davor bleibt stehen.
        const chips = document.createElement('div');
        chips.className = 'mf-chips';

        FILE_TYPES.forEach(extension => {
            const chip = document.createElement('button');
            chip.type = 'button';
            chip.className = 'mf-chip';
            chip.textContent = extension;
            chip.addEventListener('click', () => {
                const current = input.value.trim();
                const dot = current.lastIndexOf('.');
                const base = dot > 0 ? current.slice(0, dot) : current;

                input.value = (base || 'neu') + extension;
                input.focus();
                input.setSelectionRange(0, input.value.length - extension.length);

                chips.querySelectorAll('.mf-chip').forEach(other =>
                    other.classList.toggle('is-active', other === chip));
            });

            chips.appendChild(chip);
        });

        body.appendChild(chips);

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
        // Bewusst dasselbe Symbol wie bei Angaben: die Farbe des Knopfes sagt,
        // wie ernst es ist - ein Warndreieck gibt es im Zeichensatz nicht.
        const { dialog, body } = build(title, 'info');
        let answered = false;

        const text = document.createElement('p');
        text.className = 'mf-muted';
        text.style.margin = '0';
        text.textContent = message;
        body.appendChild(text);

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

/**
 * Zeigt Angaben als Liste. Über `options.extra` kommt ein weiterer Abschnitt darunter,
 * über `options.action` ein zusätzlicher Knopf neben dem Schließen.
 */
export function showFacts(title, facts, options) {
    const settings = options || {};
    const { dialog, body } = build(title, settings.icon || 'info');

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

    body.appendChild(list);

    if (settings.extra) {
        body.appendChild(settings.extra);
    }

    const row = document.createElement('div');
    row.className = 'mf-actions mfx-dialog-foot';

    if (settings.action) {
        const primary = document.createElement('button');
        primary.type = 'button';
        primary.className = 'mf-btn mf-btn-primary';
        primary.textContent = settings.action.label;
        primary.autofocus = true;
        primary.addEventListener('click', () => settings.action.onClick(dialog));
        row.appendChild(primary);
    }

    const close = document.createElement('button');
    close.type = 'button';
    close.className = settings.action ? 'mf-btn' : 'mf-btn mf-btn-primary';
    close.textContent = t('Close');
    close.autofocus = !settings.action;
    close.addEventListener('click', () => dialog.close());
    row.appendChild(close);
    dialog.appendChild(row);

    dialog.addEventListener('close', () => dialog.remove());
    document.body.appendChild(dialog);
    dialog.showModal();

    return dialog;
}

/**
 * Zeigt mehrere Abschnitte aus Bezeichnung und Wert.
 * `sections` ist eine Liste aus [Überschrift, [[links, rechts], ...]].
 */
export function showSections(title, sections, iconName) {
    const { dialog, body } = build(title, iconName || 'info');

    sections.forEach(([heading, rows]) => {
        const caption = document.createElement('h3');
        caption.className = 'mfx-manage-heading';
        caption.textContent = heading;
        body.appendChild(caption);

        const list = document.createElement('dl');
        list.className = 'mf-facts mfx-keys';

        rows.forEach(([left, right]) => {
            const dt = document.createElement('dt');
            dt.className = 'mfx-key';
            dt.textContent = left;

            const dd = document.createElement('dd');
            dd.textContent = right;

            list.append(dt, dd);
        });

        body.appendChild(list);
    });

    const row = document.createElement('div');
    row.className = 'mf-actions mfx-dialog-foot';

    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'mf-btn mf-btn-primary';
    close.textContent = t('Close');
    close.autofocus = true;
    close.addEventListener('click', () => dialog.close());
    row.appendChild(close);
    dialog.appendChild(row);

    dialog.addEventListener('close', () => dialog.remove());
    document.body.appendChild(dialog);
    dialog.showModal();

    return dialog;
}

/** Kurze Rückmeldung unten rechts. */
export function flash(message, isError) {
    const note = document.createElement('div');
    note.className = 'mfx-flash' + (isError ? ' is-error' : '');
    note.textContent = message;
    document.body.appendChild(note);
    setTimeout(() => note.remove(), isError ? 4000 : 2000);
}
