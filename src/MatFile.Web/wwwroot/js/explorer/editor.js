// Texteditor mit Syntaxhervorhebung - ohne Fremdbibliothek.
// Ein Eingabefeld mit durchsichtiger Schrift liegt deckungsgleich über dem
// hervorgehobenen Text; beide teilen sich Schrift, Zeilenhöhe und Innenabstand.

/** Ab dieser Größe wird nicht mehr eingefärbt - sonst wird das Tippen zäh. */
const HIGHLIGHT_LIMIT = 200000;

const CLIKE_KEYWORDS = 'abstract|as|async|await|base|break|case|catch|class|const|continue|default|delegate|do|else|enum|'
    + 'export|extends|finally|fn|for|foreach|from|func|function|get|go|if|impl|implements|import|in|instanceof|interface|'
    + 'internal|let|lock|match|mod|namespace|new|null|operator|out|override|package|private|protected|public|pub|readonly|'
    + 'record|ref|return|sealed|set|static|struct|super|switch|this|throw|throws|trait|try|type|typeof|use|using|var|'
    + 'virtual|void|while|with|yield';

const SHELL_KEYWORDS = 'and|as|assert|break|case|class|continue|def|del|do|done|elif|else|elsif|end|esac|except|exec|'
    + 'export|fi|finally|for|from|function|global|if|import|in|is|lambda|local|module|nonlocal|not|or|param|pass|raise|'
    + 'return|source|then|try|unless|until|while|with|yield';

const SQL_KEYWORDS = 'add|all|alter|and|as|asc|between|by|case|cast|column|constraint|create|cross|delete|desc|distinct|'
    + 'drop|else|end|exists|foreign|from|full|group|having|in|index|inner|insert|into|is|join|key|left|like|limit|not|'
    + 'null|offset|on|or|order|outer|primary|references|right|select|set|table|then|union|unique|update|values|view|when|where|with';

/**
 * Regeln je Sprache. Innerhalb der Muster nur Gruppen ohne Fang (?: ... ),
 * damit die Zuordnung Treffer -> Regel eindeutig bleibt.
 */
const GRAMMARS = {
    json: {
        flags: 'gm',
        rules: [
            ['key', '"(?:[^"\\\\]|\\\\.)*"(?=\\s*:)'],
            ['string', '"(?:[^"\\\\]|\\\\.)*"'],
            ['literal', '\\b(?:true|false|null)\\b'],
            ['number', '-?\\b\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?\\b'],
            ['punct', '[{}\\[\\],:]']
        ]
    },

    xml: {
        flags: 'gm',
        rules: [
            ['comment', '<!--[\\s\\S]*?-->'],
            ['comment', '<!\\[CDATA\\[[\\s\\S]*?\\]\\]>'],
            ['meta', '<[!?][^>]*>'],
            ['tag', '</?[A-Za-z][\\w:.-]*'],
            ['string', '"[^"]*"|\'[^\']*\''],
            ['attr', '[\\w:.-]+(?=\\s*=)'],
            ['tag', '/?>'],
            ['entity', '&[#\\w]+;']
        ]
    },

    markdown: {
        flags: 'gm',
        rules: [
            ['comment', '^```[^\\n]*\\n[\\s\\S]*?^```'],
            ['heading', '^#{1,6}[ \\t].*$'],
            ['heading', '^[^\\n]+\\n(?:={3,}|-{3,})$'],
            ['literal', '^\\s*>.*$'],
            ['punct', '^\\s*(?:[-*+]|\\d+\\.)[ \\t]'],
            ['punct', '^(?:---|\\*\\*\\*|___)[ \\t]*$'],
            ['string', '`[^`\\n]+`'],
            ['keyword', '\\*\\*[^*\\n]+\\*\\*|__[^_\\n]+__'],
            ['attr', '\\*[^*\\n]+\\*|_[^_\\n]+_'],
            ['link', '!?\\[[^\\]\\n]*\\]\\([^)\\n]*\\)'],
            ['link', '\\bhttps?://\\S+']
        ]
    },

    hash: {
        flags: 'gm',
        rules: [
            ['comment', '(?:^|\\s)#[^\\n]*'],
            ['string', '"(?:[^"\\\\\\n]|\\\\.)*"|\'[^\'\\n]*\''],
            ['key', '^\\s*[\\w.\\-/]+(?=\\s*[:=])'],
            ['section', '^\\s*\\[[^\\]\\n]+\\]\\s*$'],
            ['variable', '\\$\\{[^}\\n]*\\}|\\$\\w+'],
            ['literal', '\\b(?:true|false|yes|no|on|off|null|None|True|False)\\b'],
            ['keyword', `\\b(?:${SHELL_KEYWORDS})\\b`],
            ['number', '\\b\\d+(?:\\.\\d+)?\\b']
        ]
    },

    sql: {
        flags: 'gmi',
        rules: [
            ['comment', '--[^\\n]*|/\\*[\\s\\S]*?\\*/'],
            ['string', '\'(?:[^\'\\n]|\'\')*\''],
            ['keyword', `\\b(?:${SQL_KEYWORDS})\\b`],
            ['number', '\\b\\d+(?:\\.\\d+)?\\b'],
            ['punct', '[(),;]']
        ]
    },

    css: {
        flags: 'gm',
        rules: [
            ['comment', '/\\*[\\s\\S]*?\\*/'],
            ['string', '"[^"\\n]*"|\'[^\'\\n]*\''],
            ['literal', '#[0-9a-fA-F]{3,8}\\b'],
            ['key', '[-\\w]+(?=\\s*:)'],
            ['attr', '@[-\\w]+'],
            ['number', '\\b\\d+(?:\\.\\d+)?(?:px|rem|em|%|vh|vw|s|ms|fr|deg)?\\b'],
            ['punct', '[{};]']
        ]
    },

    clike: {
        flags: 'gm',
        rules: [
            ['comment', '//[^\\n]*|/\\*[\\s\\S]*?\\*/'],
            ['string', '"(?:[^"\\\\\\n]|\\\\.)*"|\'(?:[^\'\\\\\\n]|\\\\.)*\'|`(?:[^`\\\\]|\\\\.)*`'],
            ['literal', '\\b(?:true|false|null|nil|undefined|NaN)\\b'],
            ['keyword', `\\b(?:${CLIKE_KEYWORDS})\\b`],
            ['number', '\\b\\d[\\d_]*(?:\\.\\d+)?(?:[eE][+-]?\\d+)?\\b'],
            ['attr', '@[\\w.]+'],
            ['punct', '[{}()\\[\\];]']
        ]
    }
};

const BY_EXTENSION = {
    json: 'json', jsonc: 'json', map: 'json', webmanifest: 'json',
    xml: 'xml', html: 'xml', htm: 'xml', svg: 'xml', xaml: 'xml', csproj: 'xml',
    props: 'xml', targets: 'xml', config: 'xml', resx: 'xml', xsd: 'xml', plist: 'xml',
    md: 'markdown', markdown: 'markdown',
    yml: 'hash', yaml: 'hash', ini: 'hash', conf: 'hash', cfg: 'hash', toml: 'hash',
    env: 'hash', properties: 'hash', sh: 'hash', bash: 'hash', ps1: 'hash', psm1: 'hash',
    py: 'hash', rb: 'hash', gitignore: 'hash', dockerignore: 'hash', editorconfig: 'hash',
    sql: 'sql',
    css: 'css', scss: 'css', less: 'css',
    cs: 'clike', js: 'clike', mjs: 'clike', cjs: 'clike', ts: 'clike', tsx: 'clike',
    jsx: 'clike', java: 'clike', go: 'clike', rs: 'clike', php: 'clike', c: 'clike',
    h: 'clike', cpp: 'clike', hpp: 'clike', kt: 'clike', swift: 'clike'
};

const BY_NAME = {
    dockerfile: 'hash',
    makefile: 'hash',
    '.gitignore': 'hash',
    '.dockerignore': 'hash',
    '.editorconfig': 'hash'
};

/** Sprache aus dem Dateinamen ableiten. */
export function languageOf(fileName) {
    const name = (fileName || '').toLowerCase();

    if (BY_NAME[name]) {
        return BY_NAME[name];
    }

    const dot = name.lastIndexOf('.');
    const extension = dot >= 0 ? name.slice(dot + 1) : name;

    return BY_EXTENSION[extension] || 'plain';
}

const compiled = new Map();

function grammarFor(language) {
    if (compiled.has(language)) {
        return compiled.get(language);
    }

    const definition = GRAMMARS[language];
    const value = definition
        ? {
            regex: new RegExp(definition.rules.map(rule => `(${rule[1]})`).join('|'), definition.flags),
            types: definition.rules.map(rule => rule[0])
        }
        : null;

    compiled.set(language, value);
    return value;
}

function escapeHtml(text) {
    return text.replace(/[&<>]/g, character => (character === '&' ? '&amp;' : character === '<' ? '&lt;' : '&gt;'));
}

/** Färbt den Text ein und liefert HTML - der Inhalt selbst wird dabei maskiert. */
export function highlight(text, language) {
    const grammar = text.length <= HIGHLIGHT_LIMIT ? grammarFor(language) : null;

    if (!grammar) {
        return escapeHtml(text);
    }

    let result = '';
    let last = 0;
    let match;

    grammar.regex.lastIndex = 0;

    while ((match = grammar.regex.exec(text)) !== null) {
        if (match[0].length === 0) {
            grammar.regex.lastIndex++;
            continue;
        }

        if (match.index > last) {
            result += escapeHtml(text.slice(last, match.index));
        }

        // Welche Regel hat getroffen? Die erste gefüllte Gruppe.
        let type = 'plain';

        for (let i = 1; i < match.length; i++) {
            if (match[i] !== undefined) {
                type = grammar.types[i - 1];
                break;
            }
        }

        result += `<span class="tok-${type}">${escapeHtml(match[0])}</span>`;
        last = match.index + match[0].length;
    }

    return result + escapeHtml(text.slice(last));
}

/**
 * Baut einen Editor. Der Aufrufer hängt `element` irgendwo ein und steuert
 * den Rest über die zurückgegebenen Funktionen.
 */
export function createEditor(options) {
    const settings = options || {};

    const element = document.createElement('div');
    element.className = 'mfe';

    const gutter = document.createElement('div');
    gutter.className = 'mfe-gutter';

    const numbers = document.createElement('div');
    numbers.className = 'mfe-numbers';
    gutter.appendChild(numbers);

    const body = document.createElement('div');
    body.className = 'mfe-body';

    const highlighted = document.createElement('pre');
    highlighted.className = 'mfe-highlight';
    highlighted.setAttribute('aria-hidden', 'true');

    const code = document.createElement('code');
    highlighted.appendChild(code);

    const input = document.createElement('textarea');
    input.className = 'mfe-input';
    input.spellcheck = false;
    input.autocapitalize = 'off';
    input.autocomplete = 'off';
    input.setAttribute('wrap', 'off');

    body.append(highlighted, input);
    element.append(gutter, body);

    let language = 'plain';
    let lineCount = 0;
    let pending = null;

    function paint() {
        pending = null;
        code.innerHTML = highlight(input.value, language);

        const lines = input.value.split('\n').length;

        if (lines !== lineCount) {
            lineCount = lines;
            numbers.textContent = Array.from({ length: lines }, (value, index) => index + 1).join('\n');
        }
    }

    function schedule() {
        if (pending === null) {
            pending = requestAnimationFrame(paint);
        }
    }

    function syncScroll() {
        const x = input.scrollLeft;
        const y = input.scrollTop;

        highlighted.style.transform = `translate(${-x}px, ${-y}px)`;
        numbers.style.transform = `translateY(${-y}px)`;
    }

    input.addEventListener('input', () => {
        schedule();

        if (settings.onChange) {
            settings.onChange();
        }
    });

    input.addEventListener('scroll', syncScroll);

    input.addEventListener('keydown', event => {
        // Tabulator gehört in den Text, nicht zum nächsten Bedienelement.
        if (event.key === 'Tab' && !event.ctrlKey && !event.altKey) {
            event.preventDefault();

            const start = input.selectionStart;
            const end = input.selectionEnd;
            const value = input.value;

            if (event.shiftKey) {
                const lineStart = value.lastIndexOf('\n', start - 1) + 1;
                const prefix = value.slice(lineStart, lineStart + 4);
                const remove = prefix.startsWith('    ') ? 4 : (prefix.match(/^ +/) || [''])[0].length;

                if (remove > 0) {
                    input.value = value.slice(0, lineStart) + value.slice(lineStart + remove);
                    input.selectionStart = input.selectionEnd = Math.max(lineStart, start - remove);
                }
            } else {
                input.value = value.slice(0, start) + '    ' + value.slice(end);
                input.selectionStart = input.selectionEnd = start + 4;
            }

            schedule();

            if (settings.onChange) {
                settings.onChange();
            }

            return;
        }

        if (event.key === 's' && (event.ctrlKey || event.metaKey)) {
            event.preventDefault();

            if (settings.onSave) {
                settings.onSave();
            }
        }
    });

    return {
        element,
        input,

        setValue(text, fileName) {
            language = languageOf(fileName);
            element.dataset.language = language;
            input.value = text;
            lineCount = 0;
            paint();
            input.scrollTop = 0;
            input.scrollLeft = 0;
            syncScroll();
        },

        getValue: () => input.value,

        setReadOnly(readOnly) {
            input.readOnly = !!readOnly;
            element.classList.toggle('is-readonly', !!readOnly);
        },

        focus() {
            input.focus();
        }
    };
}
