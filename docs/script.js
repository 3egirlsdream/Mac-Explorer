(() => {
  "use strict";
  const stages = document.querySelectorAll(".replica-stage");
  const scaleStage = (stage) => {
    if (stage.clientWidth) {
      stage.style.setProperty(
        "--replica-scale",
        stage.clientWidth / Number(stage.dataset.baseWidth),
      );
    }
  };
  const resizeObserver = new ResizeObserver((entries) => {
    entries.forEach(({ target }) => scaleStage(target));
  });
  stages.forEach((stage) => {
    scaleStage(stage);
    resizeObserver.observe(stage);
  });

  const header = document.querySelector(".site-header");
  if (header) {
    const heroActions = document.querySelector(".hero-actions");
    const syncHeader = () => {
      header.classList.toggle("is-scrolled", window.scrollY > 8);
      if (heroActions) {
        header.classList.toggle(
          "is-past-hero",
          heroActions.getBoundingClientRect().bottom < 64,
        );
      }
    };
    syncHeader();
    window.addEventListener("scroll", syncHeader, { passive: true });
    window.addEventListener("resize", syncHeader, { passive: true });
  }

  const popovers = [
    document.querySelector(".language-picker"),
    document.querySelector(".mobile-menu"),
  ].filter(Boolean);

  document.addEventListener("click", (event) => {
    popovers.forEach((picker) => {
      if (!picker.contains(event.target)) picker.open = false;
    });
  });

  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") return;
    popovers.forEach((picker) => {
      if (!picker.open) return;
      picker.open = false;
      picker.querySelector("summary")?.focus();
    });
  });

  const mobileMenu = document.querySelector(".mobile-menu");
  if (mobileMenu) {
    mobileMenu.querySelectorAll("a").forEach((link) => {
      link.addEventListener("click", () => {
        mobileMenu.open = false;
      });
    });
  }

  const showcase = document.querySelector(".showcase");
  if (showcase) {
    const windows = [...showcase.querySelectorAll(".showcase-window")];
    const allThemes = windows.map((win) => win.dataset.themeId).filter(Boolean);

    const setTheme = (selectedTheme) => {
      if (!allThemes.includes(selectedTheme)) return;
      const ordered = [
        ...allThemes.filter((id) => id !== selectedTheme),
        selectedTheme,
      ];
      windows.forEach((win, idx) => {
        const nextTheme = ordered[idx];
        allThemes.forEach((id) => win.classList.remove(`theme-${id}`));
        win.classList.add(`theme-${nextTheme}`);
        win.dataset.themeId = nextTheme;
      });
    };

    windows.forEach((win) => {
      if (!win.classList.contains("showcase-back")) return;
      win.addEventListener("click", () => {
        if (win.dataset.themeId) setTheme(win.dataset.themeId);
      });
    });
  }
})();
