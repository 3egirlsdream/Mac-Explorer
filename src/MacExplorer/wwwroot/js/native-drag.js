// Native drag-and-drop support for MacExplorer
// - Drag OUT: injects file:// URLs into dataTransfer during dragstart so WKWebView's
//   built-in drag carries file references that Finder, chat apps, browsers etc. accept.
// - Drop IN: intercepts drop events for external drags, reads file URLs from multiple
//   data sources, and sends file paths to C# via WKScriptMessageHandler.
// - Internal drag (file → subfolder in same window): handled by Blazor's own handlers.

(function () {
    'use strict';

    var _isInternalDrag = false;

    function pathToFileUrl(path) {
        return 'file://' + path.split('/').map(encodeURIComponent).join('/');
    }

    function fileUrlToPath(url) {
        try { return decodeURIComponent(new URL(url).pathname); }
        catch (e) { return null; }
    }

    function extractFilePathsFromString(str) {
        if (!str) return [];
        return str.split(/[\r\n]+/)
            .filter(function (u) { return u.indexOf('file://') === 0 && u.length > 7; })
            .map(fileUrlToPath)
            .filter(Boolean);
    }

    // ── Drag OUT ──

    document.addEventListener('dragstart', function (event) {
        _isInternalDrag = true;
        var target = event.target;
        if (!target || !target.closest) return;
        var row = target.closest('[data-path]');
        if (!row) return;

        var urls = [];
        var selected = document.querySelectorAll('.selected[data-path]');
        selected.forEach(function (el) {
            var path = el.getAttribute('data-path');
            if (path) urls.push(pathToFileUrl(path));
        });
        if (urls.length === 0) {
            var path = row.getAttribute('data-path');
            if (path) urls.push(pathToFileUrl(path));
        }

        if (urls.length > 0) {
            try {
                event.dataTransfer.setData('text/uri-list', urls.join('\r\n'));
                event.dataTransfer.setData('text/plain', urls.join('\n'));
                event.dataTransfer.effectAllowed = 'copyMove';
                console.log('[MacExplorer/Drag] Set ' + urls.length + ' file URL(s)');
            } catch (e) {
                console.warn('[MacExplorer/Drag] setData failed:', e);
            }
        }

        // Notify native layer about internal drag state
        if (window.webkit && window.webkit.messageHandlers &&
            window.webkit.messageHandlers.fkfinderDragState) {
            window.webkit.messageHandlers.fkfinderDragState.postMessage({started: true});
        }
    }, true);

    document.addEventListener('dragend', function () {
        _isInternalDrag = false;

        // Notify native layer about internal drag state
        if (window.webkit && window.webkit.messageHandlers &&
            window.webkit.messageHandlers.fkfinderDragState) {
            window.webkit.messageHandlers.fkfinderDragState.postMessage({started: false});
        }
    }, true);

    // ── Drop IN ──

    document.addEventListener('dragenter', function (event) {
        if (_isInternalDrag) return;
        event.preventDefault();
    }, true);

    document.addEventListener('dragover', function (event) {
        if (_isInternalDrag) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        if (window.fkfinderNativeDrag) {
            window.fkfinderNativeDrag.setDropHighlight(event.clientX, event.clientY);
        }
    }, true);

    document.addEventListener('dragleave', function (event) {
        if (_isInternalDrag) return;
        if (!event.relatedTarget || !document.contains(event.relatedTarget)) {
            if (window.fkfinderNativeDrag) {
                window.fkfinderNativeDrag.clearDropHighlight();
            }
        }
    }, true);

    document.addEventListener('drop', function (event) {
        if (window.fkfinderNativeDrag) {
            window.fkfinderNativeDrag.clearDropHighlight();
        }
        if (_isInternalDrag) return;

        // Always prevent default for external drops
        event.preventDefault();
        event.stopPropagation();

        console.log('[MacExplorer/Drop] External drop. types:', Array.from(event.dataTransfer.types));

        var paths = [];

        // Source 1: text/uri-list
        if (paths.length === 0) {
            try {
                var d = event.dataTransfer.getData('text/uri-list');
                if (d) { console.log('[MacExplorer/Drop] text/uri-list:', d.substring(0, 300)); }
                paths = extractFilePathsFromString(d);
            } catch (e) { }
        }

        // Source 2: text/plain
        if (paths.length === 0) {
            try {
                var d = event.dataTransfer.getData('text/plain');
                if (d) { console.log('[MacExplorer/Drop] text/plain:', d.substring(0, 300)); }
                paths = extractFilePathsFromString(d);
            } catch (e) { }
        }

        // Source 3: URL
        if (paths.length === 0) {
            try {
                var d = event.dataTransfer.getData('URL');
                if (d) { console.log('[MacExplorer/Drop] URL:', d); }
                paths = extractFilePathsFromString(d);
            } catch (e) { }
        }

        // Source 4: public.file-url (macOS pasteboard type)
        if (paths.length === 0) {
            try {
                var d = event.dataTransfer.getData('public.file-url');
                if (d) { console.log('[MacExplorer/Drop] public.file-url:', d); }
                paths = extractFilePathsFromString(d);
            } catch (e) { }
        }

        // Source 5: scan all types for file:// URLs
        if (paths.length === 0) {
            for (var i = 0; i < event.dataTransfer.types.length; i++) {
                var type = event.dataTransfer.types[i];
                if (type === 'Files') continue;
                try {
                    var d = event.dataTransfer.getData(type);
                    if (d && d.indexOf('file://') >= 0) {
                        console.log('[MacExplorer/Drop] Found file:// in "' + type + '":', d.substring(0, 300));
                        paths = extractFilePathsFromString(d);
                        if (paths.length > 0) break;
                    }
                } catch (e) { }
            }
        }

        // Source 6: File objects with non-standard .path property
        if (paths.length === 0 && event.dataTransfer.files.length > 0) {
            for (var i = 0; i < event.dataTransfer.files.length; i++) {
                var f = event.dataTransfer.files[i];
                var p = f.path || f.webkitRelativePath;
                console.log('[MacExplorer/Drop] File: ' + f.name + ' path=' + (p || 'N/A') + ' size=' + f.size);
                if (p && p.charAt(0) === '/') paths.push(p);
            }
        }

        // Determine target
        var targetDir = null;
        var el = document.elementFromPoint(event.clientX, event.clientY);
        if (el) {
            var folder = el.closest('[data-path][data-is-directory="true"]');
            if (folder) targetDir = folder.getAttribute('data-path');
        }

        if (paths.length > 0) {
            if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.fkfinderDrop) {
                window.webkit.messageHandlers.fkfinderDrop.postMessage({
                    paths: paths,
                    target: targetDir
                });
                console.log('[MacExplorer/Drop] Sent ' + paths.length + ' path(s) via messageHandler');
            } else {
                console.error('[MacExplorer/Drop] fkfinderDrop handler not available!');
            }
        } else {
            console.warn('[MacExplorer/Drop] No file paths extracted. Dump all data:');
            for (var i = 0; i < event.dataTransfer.types.length; i++) {
                var type = event.dataTransfer.types[i];
                if (type === 'Files') { console.log('[MacExplorer/Drop]   Files count:', event.dataTransfer.files.length); continue; }
                try {
                    var d = event.dataTransfer.getData(type);
                    console.log('[MacExplorer/Drop]   "' + type + '":', d ? d.substring(0, 500) : '(empty)');
                } catch (e) { console.log('[MacExplorer/Drop]   "' + type + '": error'); }
            }
        }
    }, true);

    // ── Visual feedback API ──

    window.fkfinderNativeDrag = {
        getDropTargetAtPoint: function (x, y) {
            var el = document.elementFromPoint(x, y);
            if (!el) return null;
            var row = el.closest('[data-path][data-is-directory="true"]');
            return row ? row.getAttribute('data-path') : null;
        },
        setDropHighlight: function (x, y) {
            document.querySelectorAll('.external-drag-over').forEach(function (el) {
                el.classList.remove('external-drag-over');
            });
            var el = document.elementFromPoint(x, y);
            if (el) {
                var row = el.closest('[data-path][data-is-directory="true"]');
                if (row) row.classList.add('external-drag-over');
            }
            var area = document.querySelector('.file-content-area');
            if (area) area.classList.add('external-drag-active');
        },
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
