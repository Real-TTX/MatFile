// Vorschau als Vollbild-Overlay: Bild, Video, Audio, PDF und Text.
import { api, t, formatSize, icon } from './api.js';

let context = null;

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

function close() {
    stage.replaceChildren();
    overlay.hidden = true;
    context = null;
}

function step(direction) {
    if (!context || context.entries.length < 2) {
        return;
    }

    context.index = (context.index + direction + context.entries.length) % context.entries.length;
    render();
}

function message(text) {
    const box = document.createElement('div');
    box.className = 'mfx-preview-message';
    box.textContent = text;
    return box;
}

async function render() {
    const entry = current();

    title.textContent = entry.name;
    counter.textContent = context.entries.length > 1
        ? `${context.index + 1} / ${context.entries.length}`
        : formatSize(entry.size);

    downloadButton.hidden = !context.canDownload;
    previous.hidden = context.entries.length < 2;
    next.hidden = context.entries.length < 2;

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

            const block = document.createElement('pre');
            block.className = 'mfx-preview-text';
            block.textContent = result.truncated
                ? `${result.content}\n\n… ${t('Truncated')}`
                : result.content;

            stage.replaceChildren(block);
        } catch (error) {
            stage.replaceChildren(message(t('Error')));
        }

        return;
    }

    stage.appendChild(message(t('NotPreviewable')));
}

/** Öffnet die Vorschau für einen Eintrag; mit den übrigen Dateien lässt sich blättern. */
export function openPreview(rootId, entries, startPath, canDownload) {
    const previewable = entries.filter(entry => !entry.isDirectory && entry.preview !== 'none');
    const list = previewable.length > 0 ? previewable : entries.filter(entry => !entry.isDirectory);
    const index = Math.max(0, list.findIndex(entry => entry.path === startPath));

    if (list.length === 0) {
        return;
    }

    context = { rootId, entries: list, index, canDownload };
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

    if (event.key === 'Escape') {
        event.preventDefault();
        close();
    } else if (event.key === 'ArrowRight') {
        event.preventDefault();
        step(1);
    } else if (event.key === 'ArrowLeft') {
        event.preventDefault();
        step(-1);
    }
});
