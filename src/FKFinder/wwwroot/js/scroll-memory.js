// Scroll position memory for navigation back/forward — pure JS
window.fkfinderScroll = {
    // Reset scroll to top for normal navigation
    resetScroll() {
        const el = document.querySelector('.file-content-scroll');
        if (el) el.scrollTop = 0;
    },

    // Hide scroll content before re-rendering (prevents flash at wrong position)
    hideContent() {
        const el = document.querySelector('.file-content-scroll');
        if (el) el.style.visibility = 'hidden';
    },

    // Scroll selected entry into view, then reveal content
    scrollToSelected() {
        const container = document.querySelector('.file-content-scroll');
        if (!container) return;

        const selected = container.querySelector('.file-list-row.selected, .file-grid-item.selected');
        if (selected) {
            const containerRect = container.getBoundingClientRect();
            const selectedRect = selected.getBoundingClientRect();

            if (selectedRect.top < containerRect.top || selectedRect.bottom > containerRect.bottom) {
                const offsetTop = selected.offsetTop - container.offsetTop;
                const centerOffset = offsetTop - (container.clientHeight / 2) + (selected.clientHeight / 2);
                container.scrollTop = Math.max(0, centerOffset);
            }
        }

        // Reveal content at the correct position
        container.style.visibility = '';
    }
};
