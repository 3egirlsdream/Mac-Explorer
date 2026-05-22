// Native drag-and-drop support for MacExplorer
// - Drag OUT: injects file:// URLs into dataTransfer during dragstart so WKWebView's
//   built-in drag carries file references that Finder, chat apps, browsers etc. accept.
// - Drop IN: intercepts drop events for external drags, reads file URLs from multiple
//   data sources, and sends file paths to C# via WKScriptMessageHandler.
// - Internal drag (file → subfolder in same window): handled at JS level because
//   WKWebView's native NSDraggingSession suppresses HTML5 dragover/drop events
//   in Blazor's element-level handlers.

(function () {
    'use strict';

    var _isInternalDrag = false;

    // Store selected paths captured at mousedown time. In WKWebView, Blazor's async
    // rendering may not update the DOM before dragstart fires, so .selected[data-path]
    // queries in dragstart can return empty. We capture the DOM state at mousedown
    // as a fallback.
    var _capturedSelectedPaths = [];
    var _lastCaptureTime = 0;

    // Send log message to native layer for file-based logging.
    // console.log is unreliable in WKWebView on Mac Catalyst.
    function jsLog(msg) {
        if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.fkfinderLog) {
            try { window.webkit.messageHandlers.fkfinderLog.postMessage(msg); } catch (e) {}
        }
    }

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

    // ── Capture selection state at mousedown for drag fallback ──

    document.addEventListener('mousedown', function (event) {
        var target = event.target;
        if (!target || !target.closest) return;
        var row = target.closest('[data-path]');
        if (!row) return;

        // If the clicked row does NOT have .selected class, decide whether
        // to clear the capture based on timing. WKWebView fires mousedown
        // multiple times within ~30ms during a drag gesture — those should
        // NOT clear the capture. A genuine click on a different row (>150ms
        // after last capture) should clear it so stale data doesn't persist.
        if (!row.classList.contains('selected')) {
            var now = Date.now();
            if (now - _lastCaptureTime < 150) {
                jsLog('[MacExplorer/Drag] mousedown: unselected row (rapid-fire, keeping ' + _capturedSelectedPaths.length + ' captured paths)');
            } else {
                _capturedSelectedPaths = [];
                _lastCaptureTime = 0;
                jsLog('[MacExplorer/Drag] mousedown: unselected row (genuine click, cleared capture)');
            }
            return;
        }

        // Snapshot current DOM selection state so dragstart can use it
        // even if Blazor's deferred deselect timer has cleared .selected
        var selected = document.querySelectorAll('.file-list-row.selected[data-path], .file-grid-item.selected[data-path]');
        _capturedSelectedPaths = [];
        selected.forEach(function (el) {
            var path = el.getAttribute('data-path');
            if (path) _capturedSelectedPaths.push(path);
        });
        _lastCaptureTime = Date.now();
        jsLog('[MacExplorer/Drag] mousedown: captured ' + _capturedSelectedPaths.length + ' selected path(s)');
    }, true);

    // ── Drag OUT ──

    document.addEventListener('dragstart', function (event) {
        _isInternalDrag = true;
        var target = event.target;
        if (!target || !target.closest) return;
        var row = target.closest('[data-path]');
        if (!row) return;

        var urls = [];

        jsLog('[MacExplorer/Drag] dragstart: _capturedSelectedPaths.length=' + _capturedSelectedPaths.length + ', _isInternalDrag=' + _isInternalDrag);

        // Try DOM query first (most accurate if Blazor has rendered)
        var selected = document.querySelectorAll('.file-list-row.selected[data-path], .file-grid-item.selected[data-path]');
        selected.forEach(function (el) {
            var path = el.getAttribute('data-path');
            if (path) urls.push(pathToFileUrl(path));
        });

        // If DOM query returns fewer than the mousedown-captured set, use the
        // captured set instead. This handles two WKWebView timing scenarios:
        // 1. Blazor's re-render hasn't updated the DOM at all → urls empty
        // 2. Blazor's deferred deselect timer (120ms) partially cleared .selected
        //    → urls has 1 entry but _capturedSelectedPaths has N > 1
        jsLog('[MacExplorer/Drag] dragstart DOM query: ' + urls.length + ' urls from .selected, captured=' + _capturedSelectedPaths.length);
        if (_capturedSelectedPaths.length > urls.length) {
            jsLog('[MacExplorer/Drag] DOM query incomplete (' + urls.length + ' vs captured ' + _capturedSelectedPaths.length + '), using captured');
            urls = [];
            _capturedSelectedPaths.forEach(function (path) {
                urls.push(pathToFileUrl(path));
            });
        }

        // Restore .selected class on all captured entries before WKWebView captures
        // the drag ghost. In WKWebView, Blazor's deferred deselect timer (120ms after
        // mouseup) may have already cleared the .selected class from the DOM, and
        // Blazor's StateHasChanged re-render won't complete before the native drag
        // ghost is captured. We fix the DOM synchronously here in the capture phase.
        if (_capturedSelectedPaths.length > 1) {
            var capturedSet = {};
            _capturedSelectedPaths.forEach(function (p) { capturedSet[p] = true; });
            var allRows = document.querySelectorAll('[data-path]');
            for (var i = 0; i < allRows.length; i++) {
                var rowEl = allRows[i];
                var rowPath = rowEl.getAttribute('data-path');
                if (capturedSet[rowPath]) {
                    if (!rowEl.classList.contains('selected')) {
                        rowEl.classList.add('selected');
                        rowEl.classList.add('js-drag-selected');
                    }
                } else if (!capturedSet[rowPath] && rowEl.classList.contains('selected')) {
                    // Remove .selected from rows NOT in the captured set.
                    // This handles the case where Blazor re-added .selected to the
                    // wrong element due to timing.
                }
            }
        }

        // Always include the dragged element's path as fallback
        if (urls.length === 0) {
            var path = row.getAttribute('data-path');
            if (path) urls.push(pathToFileUrl(path));
        }

        if (urls.length > 0) {
            try {
                event.dataTransfer.setData('text/uri-list', urls.join('\r\n'));
                event.dataTransfer.setData('text/plain', urls.join('\n'));
                event.dataTransfer.effectAllowed = 'copyMove';
                jsLog('[MacExplorer/Drag] Set ' + urls.length + ' file URL(s)');
            } catch (e) {
                jsLog('[MacExplorer/Drag] setData failed: ' + e);
            }
        }

        // Notify native layer about internal drag state, including file paths.
        // WKWebView may not translate JS dataTransfer to native pasteboard types
        // that we can read, so we send paths directly to native for reliability.
        var paths = [];
        urls.forEach(function (u) {
            var p = fileUrlToPath(u);
            if (p) paths.push(p);
        });
        if (window.webkit && window.webkit.messageHandlers &&
            window.webkit.messageHandlers.fkfinderDragState) {
            window.webkit.messageHandlers.fkfinderDragState.postMessage({
                started: true,
                paths: paths
            });
        }
    }, true);

    document.addEventListener('dragend', function () {
        jsLog('[MacExplorer/Drag] dragend: _isInternalDrag=' + _isInternalDrag + ', capturedPathCount=' + _capturedSelectedPaths.length);
        _isInternalDrag = false;
        _capturedSelectedPaths = [];
        _lastCaptureTime = 0;

        // Clean up internal drag-over highlights
        document.querySelectorAll('.internal-drag-over').forEach(function (el) {
            el.classList.remove('internal-drag-over');
        });

        // Clean up js-drag-selected class restored during dragstart
        document.querySelectorAll('.js-drag-selected').forEach(function (el) {
            el.classList.remove('selected', 'js-drag-selected');
        });

        // Notify native layer about internal drag state
        if (window.webkit && window.webkit.messageHandlers &&
            window.webkit.messageHandlers.fkfinderDragState) {
            window.webkit.messageHandlers.fkfinderDragState.postMessage({started: false});
        }
    }, true);

    // ── dragenter: must call preventDefault to allow subsequent dragover/drop ──

    document.addEventListener('dragenter', function (event) {
        // Always prevent default — without this, dragover/drop won't fire.
        // For external drags, the native layer handles it; for internal drags,
        // this ensures HTML5 dragover/drop work in WKWebView.
        event.preventDefault();
    }, true);

    // ── dragover: handles BOTH internal and external drags ──

    document.addEventListener('dragover', function (event) {
        if (_isInternalDrag) {
            // Internal drag: preventDefault to allow drop, and highlight target folder.
            // WKWebView's native NSDraggingSession suppresses HTML5 events from reaching
            // Blazor handlers, so we handle highlighting entirely at the JS level.
            event.preventDefault();
            event.dataTransfer.dropEffect = 'move';

            // Clear previous internal drag highlights
            document.querySelectorAll('.internal-drag-over').forEach(function (el) {
                el.classList.remove('internal-drag-over');
            });

            // Highlight folder under cursor
            var el = document.elementFromPoint(event.clientX, event.clientY);
            if (el) {
                var folder = el.closest('[data-path][data-is-directory="true"]');
                if (folder && !folder.classList.contains('internal-drag-over')) {
                    folder.classList.add('internal-drag-over');
                }
            }
            return;
        }

        // External drag
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        if (window.fkfinderNativeDrag) {
            window.fkfinderNativeDrag.setDropHighlight(event.clientX, event.clientY);
        }
    }, true);

    document.addEventListener('dragleave', function (event) {
        if (_isInternalDrag) {
            // Allow internal dragleave to propagate — the dragend handler cleans up.
            return;
        }
        if (!event.relatedTarget || !document.contains(event.relatedTarget)) {
            if (window.fkfinderNativeDrag) {
                window.fkfinderNativeDrag.clearDropHighlight();
            }
        }
    }, true);

    // ── drop: handles BOTH internal and external drags ──

    document.addEventListener('drop', function (event) {
        // Clear highlights first
        document.querySelectorAll('.internal-drag-over').forEach(function (el) {
            el.classList.remove('internal-drag-over');
        });
        if (window.fkfinderNativeDrag) {
            window.fkfinderNativeDrag.clearDropHighlight();
        }

        if (_isInternalDrag) {
            // ── Internal drag drop ──
            // WKWebView suppresses Blazor's ondragstart/ondragover/ondrop handlers,
            // so we handle the entire internal drag-drop flow at the JS level.
            event.preventDefault();
            event.stopPropagation();

            // Find target folder
            var targetDir = null;
            var el = document.elementFromPoint(event.clientX, event.clientY);
            if (el) {
                var folder = el.closest('[data-path][data-is-directory="true"]');
                if (folder) targetDir = folder.getAttribute('data-path');
            }

            // Get source paths from dataTransfer (set during dragstart)
            var paths = [];
            try {
                var d = event.dataTransfer.getData('text/uri-list');
                if (d) {
                    jsLog('[MacExplorer/Drop] Internal drop text/uri-list: ' + d.substring(0, 300));
                    paths = extractFilePathsFromString(d);
                }
            } catch (e) {
                jsLog('[MacExplorer/Drop] Failed to read text/uri-list: ' + e);
            }

            if (paths.length === 0) {
                // Fallback: use captured paths from mousedown
                paths = _capturedSelectedPaths.slice();
                jsLog('[MacExplorer/Drop] Using captured paths fallback: ' + paths.length);
            }

            jsLog('[MacExplorer/Drop] Internal drop: ' + paths.length + ' path(s) -> ' + (targetDir || '(no target)'));

            if (paths.length > 0 && targetDir) {
                if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.fkfinderDrop) {
                    window.webkit.messageHandlers.fkfinderDrop.postMessage({
                        paths: paths,
                        target: targetDir,
                        isInternal: true
                    });
                    jsLog('[MacExplorer/Drop] Sent internal drop via fkfinderDrop');
                } else {
                    jsLog('[MacExplorer/Drop] fkfinderDrop handler not available for internal drop!');
                }
            }

            _isInternalDrag = false;
            return;
        }

        // ── External drag drop (existing logic) ──
        event.preventDefault();
        event.stopPropagation();

        jsLog('[MacExplorer/Drop] External drop. types: ' + Array.from(event.dataTransfer.types));

        var paths = [];

        // Source 1: text/uri-list
        if (paths.length === 0) {
            try {
                var d = event.dataTransfer.getData('text/uri-list');
                if (d) { jsLog('[MacExplorer/Drop] text/uri-list: ' + d.substring(0, 300)); }
                paths = extractFilePathsFromString(d);
            } catch (e) { }
        }

        // Source 2: text/plain
        if (paths.length === 0) {
            try {
                var d = event.dataTransfer.getData('text/plain');
                if (d) { jsLog('[MacExplorer/Drop] text/plain: ' + d.substring(0, 300)); }
                paths = extractFilePathsFromString(d);
            } catch (e) { }
        }

        // Source 3: URL
        if (paths.length === 0) {
            try {
                var d = event.dataTransfer.getData('URL');
                if (d) { jsLog('[MacExplorer/Drop] URL: ' + d); }
                paths = extractFilePathsFromString(d);
            } catch (e) { }
        }

        // Source 4: public.file-url (macOS pasteboard type)
        if (paths.length === 0) {
            try {
                var d = event.dataTransfer.getData('public.file-url');
                if (d) { jsLog('[MacExplorer/Drop] public.file-url: ' + d); }
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
                        jsLog('[MacExplorer/Drop] Found file:// in "' + type + '": ' + d.substring(0, 300));
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
                jsLog('[MacExplorer/Drop] File: ' + f.name + ' path=' + (p || 'N/A') + ' size=' + f.size);
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
                    target: targetDir,
                    isInternal: false
                });
                jsLog('[MacExplorer/Drop] Sent ' + paths.length + ' path(s) via messageHandler');
            } else {
                jsLog('[MacExplorer/Drop] fkfinderDrop handler not available!');
            }
        } else {
            jsLog('[MacExplorer/Drop] No file paths extracted. Dump all data:');
            for (var i = 0; i < event.dataTransfer.types.length; i++) {
                var type = event.dataTransfer.types[i];
                if (type === 'Files') { jsLog('[MacExplorer/Drop]   Files count: ' + event.dataTransfer.files.length); continue; }
                try {
                    var d = event.dataTransfer.getData(type);
                    jsLog('[MacExplorer/Drop]   "' + type + '": ' + (d ? d.substring(0, 500) : '(empty)'));
                } catch (e) { jsLog('[MacExplorer/Drop]   "' + type + '": error'); }
            }
        }
    }, true);

    // ── Captured selection API (for count badge in index.html) ──

    window.fkfinderDragCapture = {
        getSelectedCount: function () {
            return _capturedSelectedPaths.length;
        },
        getSelectedPaths: function () {
            return _capturedSelectedPaths.slice();
        }
    };

    // ── Visual feedback API ──

    window.fkfinderNativeDrag = {
        // Diagnostic: returns detailed info about what's at a point
        debugPoint: function (x, y) {
            var info = { x: x, y: y, vpW: window.innerWidth, vpH: window.innerHeight, el: null, path: null, isDir: null, pathsNearby: [] };
            var el = document.elementFromPoint(x, y);
            if (el) {
                info.el = el.tagName + (el.className ? '.' + String(el.className).split(' ').slice(0,3).join('.') : '');
                var cp = el.closest('[data-path]');
                if (cp) {
                    info.path = cp.getAttribute('data-path');
                    info.isDir = cp.getAttribute('data-is-directory');
                }
            }
            // Also check if there's a [data-path] element within 30px in any direction
            var nearbyOffsets = [0, 5, 10, 15, 20, 25, 30, -5, -10, -15, -20, -25, -30];
            for (var dx = 0; dx < nearbyOffsets.length; dx++) {
                for (var dy = 0; dy < nearbyOffsets.length; dy++) {
                    var e2 = document.elementFromPoint(x + nearbyOffsets[dx], y + nearbyOffsets[dy]);
                    if (e2) {
                        var cp2 = e2.closest('[data-path]');
                        if (cp2) {
                            var p = cp2.getAttribute('data-path');
                            var d = cp2.getAttribute('data-is-directory');
                            var key = p + '|' + d;
                            if (info.pathsNearby.indexOf(key) < 0) info.pathsNearby.push(key);
                        }
                    }
                }
            }
            return JSON.stringify(info);
        },
        getDropTargetAtPoint: function (x, y) {
            // Diagnostic: log what's at the exact point
            var el = document.elementFromPoint(x, y);
            if (el) {
                var closestPath = el.closest('[data-path]');
                var pathAttr = closestPath ? closestPath.getAttribute('data-path') : null;
                var isDir = closestPath ? closestPath.getAttribute('data-is-directory') : null;
                jsLog('[MacExplorer/Drop] getDropTargetAtPoint(' + x.toFixed(1) + ',' + y.toFixed(1) + '): el=' + (el.tagName||'?') + ' path=' + (pathAttr||'N/A') + ' isDir=' + (isDir||'N/A') + ' vp=' + window.innerWidth + 'x' + window.innerHeight);
            } else {
                jsLog('[MacExplorer/Drop] getDropTargetAtPoint(' + x.toFixed(1) + ',' + y.toFixed(1) + '): el=null vp=' + window.innerWidth + 'x' + window.innerHeight);
            }
            // Try exact point first
            if (el) {
                var row = el.closest('[data-path][data-is-directory="true"]');
                if (row) return row.getAttribute('data-path');
            }
            // Search nearby points for small coordinate mismatches between
            // native overlay coordinates and WKWebView viewport.
            // Check an expanding cross pattern: radius 1,2,4,8,12,20,30 px.
            var radii = [1, 2, 4, 8, 12, 20, 30];
            for (var r = 0; r < radii.length; r++) {
                var d = radii[r];
                var pts = [[d,0],[-d,0],[0,d],[0,-d],[d,d],[d,-d],[-d,d],[-d,-d]];
                for (var p = 0; p < pts.length; p++) {
                    var e2 = document.elementFromPoint(x + pts[p][0], y + pts[p][1]);
                    if (e2) {
                        var r2 = e2.closest('[data-path][data-is-directory="true"]');
                        if (r2) return r2.getAttribute('data-path');
                    }
                }
            }
            return null;
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
