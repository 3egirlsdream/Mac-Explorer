// FKFinder keyboard shortcuts
window.fkfinderKeyboard = {
    initialized: false,
    init: function(dotNetRef) {
        if (this.initialized) return;
        this.initialized = true;
        this.dotNetRef = dotNetRef;

        document.addEventListener('keydown', (e) => {
            if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;

            const key = e.key.toLowerCase();
            const meta = e.metaKey || e.ctrlKey;

            if (meta && key === 'c') {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'copy');
            } else if (meta && key === 'x') {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'cut');
            } else if (meta && key === 'v') {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'paste');
            } else if (meta && key === 'a') {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'selectAll');
            } else if (key === 'delete' || (meta && key === 'backspace')) {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'delete');
            } else if (key === 'enter') {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'enter');
            } else if (meta && key === 'o') {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'open');
            } else if (key === 'backspace' && !meta) {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'navigateUp');
            } else if (meta && key === 'r') {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'refresh');
            } else if (meta && key === 'i') {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'showInfo');
            } else if (meta && key === 'n') {
                e.preventDefault();
                if (e.shiftKey) {
                    this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'newFolder');
                } else {
                    this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'newFile');
                }
            } else if (key === ' ' && !meta) {
                e.preventDefault();
                this.dotNetRef.invokeMethodAsync('OnKeyboardShortcut', 'quickLook');
            }
        });

        // Prevent native context menu globally
        document.addEventListener('contextmenu', (e) => {
            e.preventDefault();
        });
    }
};

// Context menu position adjustment — keep menu within viewport
window.fkfinderContextMenu = {
    adjustPosition: function(menuEl, x, y) {
        var rect = menuEl.getBoundingClientRect();
        var vw = window.innerWidth;
        var vh = window.innerHeight;
        var newX = x;
        var newY = y;
        if (x + rect.width > vw) newX = Math.max(0, vw - rect.width - 4);
        if (y + rect.height > vh) newY = Math.max(0, vh - rect.height - 4);
        return [newX, newY];
    },
    // Auto-close context menu when clicking outside
    _autoCloseRef: null,
    _autoCloseHandler: null,
    registerAutoClose: function(dotNetRef) {
        this.unregisterAutoClose();
        this._autoCloseRef = dotNetRef;
        this._autoCloseHandler = function(e) {
            if (!e.target.closest('.context-menu')) {
                dotNetRef.invokeMethodAsync('RequestClose');
            }
        };
        document.addEventListener('mousedown', this._autoCloseHandler);
    },
    unregisterAutoClose: function() {
        if (this._autoCloseHandler) {
            document.removeEventListener('mousedown', this._autoCloseHandler);
            this._autoCloseHandler = null;
        }
        this._autoCloseRef = null;
    }
};

// Toolbar dropdown auto-close handler
window.fkfinderDropdown = {
    _ref: null,
    _handler: null,
    register: function(dotNetRef) {
        this.unregister();
        this._ref = dotNetRef;
        this._handler = function(e) {
            if (!e.target.closest('.new-btn-wrapper') && !e.target.closest('.sort-dropdown-wrapper')) {
                dotNetRef.invokeMethodAsync('CloseDropdowns');
            }
        };
        document.addEventListener('mousedown', this._handler);
    },
    unregister: function() {
        if (this._handler) {
            document.removeEventListener('mousedown', this._handler);
            this._handler = null;
        }
        this._ref = null;
    }
};

// Box selection and rename helpers
window.fkfinderSelection = {
    getIntersectingIndices: function(selector, left, top, right, bottom) {
        var elements = document.querySelectorAll(selector);
        var indices = [];
        for (var i = 0; i < elements.length; i++) {
            var rect = elements[i].getBoundingClientRect();
            // Check intersection
            if (rect.right >= left && rect.left <= right &&
                rect.bottom >= top && rect.top <= bottom) {
                indices.push(i);
            }
        }
        return indices;
    },
    focusAndSelect: function(el) {
        if (el && el.focus) {
            el.focus();
            if (el.select) el.select();
        }
    }
};
