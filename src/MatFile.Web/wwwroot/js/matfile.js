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
