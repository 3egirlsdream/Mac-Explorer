// Column resize for file list header — pure JS, zero Blazor interop
window.fkfinderColumnResize = {
    _initialized: false,

    init() {
        if (this._initialized) return;
        this._initialized = true;

        document.addEventListener('mousedown', (e) => {
            const handle = e.target.closest('.col-resize-handle');
            if (!handle) return;
            e.preventDefault();
            e.stopPropagation();

            const col = handle.dataset.resize;
            const header = handle.closest('.file-list-header');
            const colEl = handle.closest('.file-list-header-col');
            if (!header || !colEl) return;

            const startX = e.clientX;
            const startWidth = colEl.getBoundingClientRect().width;
            const area = header.closest('.file-content-area');

            handle.classList.add('active');
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';

            let pendingX = startX;
            let rafId = 0;

            const applyResize = () => {
                rafId = 0;
                const delta = startX - pendingX;
                const newWidth = Math.max(50, startWidth + delta);
                const val = `${newWidth}px`;
                header.style.setProperty(`--col-${col}`, val);
                if (area) area.style.setProperty(`--col-${col}`, val);
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
    },

    dispose() {
        this._initialized = false;
    }
};
