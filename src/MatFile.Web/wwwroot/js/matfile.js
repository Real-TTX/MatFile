// MatFile - kleine Helfer ohne Framework.
(function () {
    'use strict';

    // Sicherheitsabfrage vor Aktionen mit data-confirm.
    document.addEventListener('click', function (event) {
        var trigger = event.target.closest('[data-confirm]');
        if (!trigger) {
            return;
        }

        if (!window.confirm(trigger.getAttribute('data-confirm'))) {
            event.preventDefault();
            event.stopPropagation();
        }
    });

    // Filter mit data-auto-submit schicken das Formular direkt ab.
    document.addEventListener('change', function (event) {
        var control = event.target.closest('[data-auto-submit]');
        if (control && control.form) {
            control.form.requestSubmit ? control.form.requestSubmit() : control.form.submit();
        }
    });

    // Dialoge oeffnen und schliessen ueber data-dialog-open / data-dialog-close.
    document.addEventListener('click', function (event) {
        var opener = event.target.closest('[data-dialog-open]');
        if (opener) {
            var dialog = document.getElementById(opener.getAttribute('data-dialog-open'));
            if (dialog) {
                dialog.showModal();
            }
            return;
        }

        var closer = event.target.closest('[data-dialog-close]');
        if (closer) {
            var open = closer.closest('dialog');
            if (open) {
                open.close();
            }
        }
    });

    // Erscheinungsbild umschalten: System -> hell -> dunkel -> System.
    var THEMES = ['auto', 'light', 'dark'];

    document.addEventListener('click', function (event) {
        var toggle = event.target.closest('[data-theme-toggle]');
        if (!toggle) {
            return;
        }

        var root = document.documentElement;
        var current = root.getAttribute('data-theme') || 'auto';
        var next = THEMES[(THEMES.indexOf(current) + 1) % THEMES.length];

        root.setAttribute('data-theme', next);
        document.cookie = 'matfile.theme=' + next + ';path=/;max-age=31536000;samesite=lax';
    });

    // Felder mit data-visible-for erscheinen nur bei passendem Wert der Quelle.
    function applyVisibility() {
        var source = document.querySelector('[data-visibility-source]');
        if (!source) {
            return;
        }

        var value = source.value;

        document.querySelectorAll('[data-visible-for]').forEach(function (element) {
            var allowed = element.getAttribute('data-visible-for').split(',');
            element.hidden = allowed.indexOf(value) < 0;
        });
    }

    document.addEventListener('change', function (event) {
        if (event.target.matches && event.target.matches('[data-visibility-source]')) {
            applyVisibility();
        }
    });

    document.addEventListener('DOMContentLoaded', applyVisibility);

    // Kopfzeilen-Checkbox wählt alle Zeilen der Tabelle aus.
    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('table.mf-table').forEach(function (table) {
            var head = table.querySelector('thead th.mf-col-select');
            if (!head || head.querySelector('input')) {
                return;
            }

            var boxes = table.querySelectorAll('tbody td.mf-col-select input[type="checkbox"]');
            if (boxes.length === 0) {
                return;
            }

            var toggle = document.createElement('input');
            toggle.type = 'checkbox';
            toggle.setAttribute('aria-label', 'Alle auswählen / Select all');
            toggle.addEventListener('change', function () {
                boxes.forEach(function (box) { box.checked = toggle.checked; });
            });

            head.appendChild(toggle);
        });
    });
})();
