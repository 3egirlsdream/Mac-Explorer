// Native drag-and-drop support for FKFinder
// - dragstart: injects file:// URLs into dataTransfer so WKWebView's native drag
//   carries file references that Finder, chat apps, browsers etc. can accept.
// - Drop highlighting: hit-testing and visual feedback for native UIDropInteraction.

(function () {
    'use strict';

    // Convert a local file path to a properly encoded file:// URL
    function pathToFileUrl(path) {
        return 'file://' + path.split('/').map(encodeURIComponent).join('/');
    }

    // Capture-phase dragstart listener — runs before Blazor's handler.
    // Sets text/uri-list with file:// URLs so WKWebView's built-in drag
    // session includes the real file references on the pasteboard.
    document.addEventListener('dragstart', function (event) {
        var target = event.target;
        if (!target || !target.closest) return;

        var row = target.closest('[data-path]');
        if (!row) return;

        // Collect paths from all selected items
        var urls = [];
        var selected = document.querySelectorAll('.selected[data-path]');
        selected.forEach(function (el) {
            var path = el.getAttribute('data-path');
            if (path) urls.push(pathToFileUrl(path));
        });

        // If dragged item is not in selection, use it alone
        if (urls.length === 0) {
            var path = row.getAttribute('data-path');
            if (path) urls.push(pathToFileUrl(path));
        }

        if (urls.length > 0) {
            try {
                event.dataTransfer.setData('text/uri-list', urls.join('\r\n'));
                event.dataTransfer.setData('text/plain', urls.join('\n'));
                event.dataTransfer.effectAllowed = 'copyMove';
                console.log('[FKFinder/Drag] Set ' + urls.length + ' file URL(s) in dataTransfer');
            } catch (e) {
                console.warn('[FKFinder/Drag] Failed to set dataTransfer:', e);
            }
        }
    }, true); // capture phase

    // Expose hit-testing and visual feedback for native UIDropInteraction
    window.fkfinderNativeDrag = {
        // Given coordinates, return the folder path at that point (or null)
        getDropTargetAtPoint: function (x, y) {
            var el = document.elementFromPoint(x, y);
            if (!el) return null;
            var row = el.closest('[data-path][data-is-directory="true"]');
            return row ? row.getAttribute('data-path') : null;
        },

        // Set drop highlight on the folder element at the given coordinates
        setDropHighlight: function (x, y) {
            // Clear previous highlights
            document.querySelectorAll('.external-drag-over').forEach(function (el) {
                el.classList.remove('external-drag-over');
            });

            var el = document.elementFromPoint(x, y);
            if (el) {
                var row = el.closest('[data-path][data-is-directory="true"]');
                if (row) {
                    row.classList.add('external-drag-over');
                }
            }

            // Add overall drop indicator to the content area
            var area = document.querySelector('.file-content-area');
            if (area) area.classList.add('external-drag-active');
        },

        // Clear all drop highlights
        clearDropHighlight: function () {
            document.querySelectorAll('.external-drag-over').forEach(function (el) {
                el.classList.remove('external-drag-over');
            });
            document.querySelectorAll('.external-drag-active').forEach(function (el) {
                el.classList.remove('external-drag-active');
            });
        }
    };
})();
