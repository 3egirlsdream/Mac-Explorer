// Preview pane resize — pure JS, auto-initializing, zero Blazor interop
window.fkfinderPreviewResize = {
    _initialized: false,

    init() {
        if (this._initialized) return;
        this._initialized = true;

        document.addEventListener('mousedown', (e) => {
            const handle = e.target.closest('.preview-resize-handle');
            if (!handle) return;
            e.preventDefault();
            e.stopPropagation();

            const pane = handle.closest('.preview-pane');
            if (!pane) return;

            const startX = e.clientX;
            const startWidth = pane.offsetWidth;

            handle.classList.add('active');
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';

            let pendingX = startX;
            let rafId = 0;

            const applyResize = () => {
                rafId = 0;
                const delta = startX - pendingX;
                const newWidth = Math.min(Math.max(startWidth + delta, 200), window.innerWidth * 0.5);
                pane.style.width = newWidth + 'px';
            };

            const onMove = (e2) => {
                pendingX = e2.clientX;
                if (!rafId) rafId = requestAnimationFrame(applyResize);
            };

            const onUp = () => {
                if (rafId) { cancelAnimationFrame(rafId); applyResize(); }
                handle.classList.remove('active');
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
            };

            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });
    }
};

// Auto-initialize when DOM is ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => fkfinderPreviewResize.init());
} else {
    fkfinderPreviewResize.init();
}
