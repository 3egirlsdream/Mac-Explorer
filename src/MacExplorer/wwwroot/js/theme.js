// Theme mode management for Mac Explorer
(function() {
    'use strict';

    // Apply dark mode class to body
    function applyDarkMode(isDark) {
        if (isDark) {
            document.body.classList.add('dark-mode');
        } else {
            document.body.classList.remove('dark-mode');
        }
    }

    // Detect system color scheme preference
    function getSystemDarkMode() {
        return window.matchMedia('(prefers-color-scheme: dark)').matches;
    }

    // Initialize theme based on mode: 'system', 'light', 'dark'
    function initTheme(mode) {
        if (mode === 'system') {
            applyDarkMode(getSystemDarkMode());
        } else {
            applyDarkMode(mode === 'dark');
        }
    }

    // Listen for system theme changes
    if (window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function(e) {
            // Only auto-switch if we're in system mode
            if (window._themeMode === 'system') {
                applyDarkMode(e.matches);
            }
        });
    }

    // Expose to global scope for .NET interop
    window.applyTheme = function(mode) {
        window._themeMode = mode;
        initTheme(mode);
    };

    // Expose a method to set dark mode directly without changing the mode setting
    window.setDarkMode = function(isDark) {
        applyDarkMode(isDark);
    };

    window.getSystemDarkMode = getSystemDarkMode;
})();
