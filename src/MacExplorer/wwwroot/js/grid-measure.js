window.fkfinderGrid = {
    _observer: null,

    getItemWidth: function () {
        var el = document.querySelector('.file-content-scroll');
        if (!el) return 0;
        var style = getComputedStyle(el);
        var width = el.clientWidth;
        var paddingLeft = parseFloat(style.paddingLeft) || 0;
        var paddingRight = parseFloat(style.paddingRight) || 0;
        return width - paddingLeft - paddingRight;
    },

    observeResize: function (dotNetRef) {
        var el = document.querySelector('.file-content-scroll');
        if (!el) return;
        if (this._observer) this._observer.disconnect();
        this._observer = new ResizeObserver(function () {
            dotNetRef.invokeMethodAsync('OnContainerResize');
        });
        this._observer.observe(el);
    },

    disconnect: function () {
        if (this._observer) {
            this._observer.disconnect();
            this._observer = null;
        }
    }
};
