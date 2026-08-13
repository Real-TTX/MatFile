// Vorschau als Vollbild-Overlay: Bild, Video, Audio, PDF und Text.
// Textdateien lassen sich hier auch bearbeiten - mit Syntaxhervorhebung.
import { api, t, formatSize, icon, errorKey } from './api.js';
import { askConfirm, flash } from './dialogs.js';
import { createEditor } from './editor.js';

let context = null;

/** Zustand der geöffneten Textdatei. */
const text = {
    editor: null,
    loaded: '',
    modified: null,
    editable: false,
    editing: false,
    dirty: false
};

const overlay = document.createElement('div');
overlay.className = 'mfx-preview';
overlay.hidden = true;

const bar = document.createElement('div');
bar.className = 'mfx-preview-bar';

const title = document.createElement('span');
title.className = 'mfx-preview-title';
bar.appendChild(title);

const counter = document.createElement('span');
counter.className = 'mfx-preview-counter';
bar.appendChild(counter);

const spacer = document.createElement('span');
spacer.className = 'mf-spacer';
bar.appendChild(spacer);

function barButton(iconName, label, handler) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'mfx-btn';
    button.title = label;
    button.setAttribute('aria-label', label);
    button.appendChild(icon(iconName, 16));
    button.addEventListener('click', handler);
    bar.appendChild(button);
    return button;
}

const editButton = barButton('pencil', t('Edit'), () => startEditing());
const saveButton = barButton('save', t('Save'), () => save());
const revertButton = barButton('refresh', t('Revert'), () => revert());

const downloadButton = barButton('download', t('Download'), () => {
    if (context) {
        window.location.href = api.downloadUrl(context.rootId, current().path);
    }
});

barButton('x', t('Close'), () => close());

const stage = document.createElement('div');
stage.className = 'mfx-preview-stage';

const previous = document.createElement('button');
previous.type = 'button';
previous.className = 'mfx-preview-nav mfx-preview-prev';
previous.setAttribute('aria-label', t('Back'));
previous.appendChild(icon('chevron', 22));
previous.addEventListener('click', () => step(-1));

const next = document.createElement('button');
next.type = 'button';
next.className = 'mfx-preview-nav mfx-preview-next';
next.setAttribute('aria-label', t('Forward'));
next.appendChild(icon('chevron', 22));
next.addEventListener('click', () => step(1));

overlay.appendChild(bar);
overlay.appendChild(previous);
overlay.appendChild(stage);
overlay.appendChild(next);
document.body.appendChild(overlay);

overlay.addEventListener('mousedown', event => {
    if (event.target === overlay || event.target === stage) {
        close();
    }
});

const current = () => context.entries[context.index];

/** Ungespeicherte Änderungen nicht stillschweigend wegwerfen. */
async function mayLeave() {
    if (!text.dirty) {
        return true;
    }

    return askConfirm(t('UnsavedTitle'), t('UnsavedQuestion'), t('Discard'), true);
}

async function close() {
    if (!await mayLeave()) {
        return;
    }

    stage.replaceChildren();
    stage.classList.remove('is-text');
    overlay.hidden = true;
    context = null;
    resetText();
}

function resetText() {
    text.loaded = '';
    text.modified = null;
    text.editable = false;
    text.editing = false;
    text.dirty = false;
}

async function step(direction) {
    if (!context || context.entries.length < 2) {
        return;
    }

    if (!await mayLeave()) {
        return;
    }

    resetText();
    context.index = (context.index + direction + context.entries.length) % context.entries.length;
    render();
}

function message(text) {
    const box = document.createElement('div');
    box.className = 'mfx-preview-message';
    box.textContent = text;
    return box;
}

function updateBar() {
    const entry = current();

    title.textContent = entry.name + (text.dirty ? ' •' : '');
    counter.textContent = context.entries.length > 1
        ? `${context.index + 1} / ${context.entries.length}`
        : formatSize(entry.size);

    downloadButton.hidden = !context.canDownload;
    previous.hidden = context.entries.length < 2 || text.editing;
    next.hidden = context.entries.length < 2 || text.editing;

    editButton.hidden = !text.editable || text.editing;
    saveButton.hidden = !text.editing;
    revertButton.hidden = !text.editing;
}

async function render() {
    const entry = current();

    updateBar();
    stage.classList.remove('is-text');
    stage.replaceChildren();
    const url = `/api/fs/preview?rootId=${encodeURIComponent(context.rootId)}&path=${encodeURIComponent(entry.path)}`;

    if (entry.preview === 'image') {
        const image = document.createElement('img');
        image.className = 'mfx-preview-image';
        image.alt = entry.name;
        image.src = url;
        stage.appendChild(image);
        return;
    }

    if (entry.preview === 'video' || entry.preview === 'audio') {
        const media = document.createElement(entry.preview === 'video' ? 'video' : 'audio');
        media.className = 'mfx-preview-media';
        media.src = url;
        media.controls = true;
        media.autoplay = true;
        stage.appendChild(media);
        return;
    }

    if (entry.preview === 'pdf') {
        const frame = document.createElement('iframe');
        frame.className = 'mfx-preview-frame';
        frame.src = url;
        frame.title = entry.name;
        stage.appendChild(frame);
        return;
    }

    if (entry.preview === 'text') {
        stage.appendChild(message(t('Loading')));

        try {
            const result = await api.text(context.rootId, entry.path);

            text.loaded = result.content;
            text.modified = result.modified;
            text.editable = !!result.editable && !!context.canWrite;
            text.dirty = false;

            if (!text.editor) {
                text.editor = createEditor({
                    onChange: () => {
                        const dirty = text.editor.getValue() !== text.loaded;

                        if (dirty !== text.dirty) {
                            text.dirty = dirty;
                            updateBar();
                        }
                    },
                    onSave: () => save()
                });
            }

            text.editor.setValue(result.content, entry.name);
            text.editor.setReadOnly(!text.editing);

            // Auf breiten Schirmen bleibt der Text in lesbarer Breite stehen.
            const sheet = document.createElement('div');
            sheet.className = 'mfx-preview-doc';
            sheet.appendChild(text.editor.element);

            if (result.truncated) {
                const note = document.createElement('div');
                note.className = 'mfx-preview-note';
                note.textContent = `${t('Truncated')} · ${formatSize(result.size)}`;
                sheet.appendChild(note);
            }

            stage.classList.add('is-text');
            stage.replaceChildren(sheet);
            updateBar();

            if (context.startInEdit && text.editable) {
                context.startInEdit = false;
                startEditing();
            }
        } catch (error) {
            stage.replaceChildren(message(t(errorKey(error && error.code), t('Error'))));
        }

        return;
    }

    stage.appendChild(message(t('NotPreviewable')));
}

// ---------------------------------------------------------------- Bearbeiten

function startEditing() {
    if (!text.editable || !text.editor) {
        return;
    }

    text.editing = true;
    text.editor.setReadOnly(false);
    text.editor.focus();
    updateBar();
}

async function save() {
    if (!text.editing || !context) {
        return;
    }

    const entry = current();
    const content = text.editor.getValue();

    saveButton.disabled = true;

    try {
        const result = await api.saveText(context.rootId, entry.path, content, text.modified);

        text.loaded = content;
        text.modified = result.modified;
        text.dirty = false;
        entry.size = result.size;
        entry.modified = result.modified;

        flash(t('Common.Saved'));
        updateBar();

        if (context.onSaved) {
            context.onSaved();
        }
    } catch (error) {
        const code = error && error.code;
        flash(t(errorKey(code), t('Error')), true);
    } finally {
        saveButton.disabled = false;
    }
}

/** Zurück auf den zuletzt geladenen Stand - ohne die Datei erneut zu holen. */
async function revert() {
    if (text.dirty && !await askConfirm(t('UnsavedTitle'), t('UnsavedQuestion'), t('Discard'), true)) {
        return;
    }

    text.editor.setValue(text.loaded, current().name);
    text.editing = false;
    text.dirty = false;
    text.editor.setReadOnly(true);
    updateBar();
}

/**
 * Öffnet die Vorschau für einen Eintrag; mit den übrigen Dateien lässt sich blättern.
 * @param options {{canDownload, canWrite, edit, onSaved}}
 */
export function openPreview(rootId, entries, startPath, options) {
    const settings = options || {};
    const previewable = entries.filter(entry => !entry.isDirectory && entry.preview !== 'none');
    const list = previewable.length > 0 ? previewable : entries.filter(entry => !entry.isDirectory);
    const index = Math.max(0, list.findIndex(entry => entry.path === startPath));

    if (list.length === 0) {
        return;
    }

    resetText();

    context = {
        rootId,
        entries: list,
        index,
        canDownload: !!settings.canDownload,
        canWrite: !!settings.canWrite,
        startInEdit: !!settings.edit,
        onSaved: settings.onSaved
    };

    overlay.hidden = false;
    render();
}

export function isPreviewOpen() {
    return !overlay.hidden;
}

document.addEventListener('keydown', event => {
    if (overlay.hidden) {
        return;
    }

    // Im Editor gehören die Tasten dem Text.
    const inEditor = event.target instanceof Element && event.target.closest('.mfe');

    if (event.key === 'Escape') {
        event.preventDefault();
        close();
        return;
    }

    if (inEditor) {
        return;
    }

    if (event.key === 'ArrowRight') {
        event.preventDefault();
        step(1);
    } else if (event.key === 'ArrowLeft') {
        event.preventDefault();
        step(-1);
    }
});
