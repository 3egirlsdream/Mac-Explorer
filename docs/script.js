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
    const syncHeader = () =>
      header.classList.toggle("is-scrolled", window.scrollY > 8);
    syncHeader();
    window.addEventListener("scroll", syncHeader, { passive: true });
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
    const themeButtons = [...showcase.querySelectorAll("[data-theme-select]")];
    const allThemes = themeButtons.map((btn) => btn.dataset.themeSelect);

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
      themeButtons.forEach((btn) => {
        const active = btn.dataset.themeSelect === selectedTheme;
        btn.classList.toggle("is-active", active);
        btn.setAttribute("aria-pressed", String(active));
      });
    };

    themeButtons.forEach((btn) => {
      btn.addEventListener("click", () => setTheme(btn.dataset.themeSelect));
    });

    windows.forEach((win) => {
      if (!win.classList.contains("showcase-back")) return;
      win.addEventListener("click", () => {
        if (win.dataset.themeId) setTheme(win.dataset.themeId);
      });
    });

    const modeBtn = showcase.querySelector("[data-appearance-toggle]");
    if (modeBtn) {
      modeBtn.addEventListener("click", () => {
        const isDark = showcase.classList.toggle("showcase-dark");
        modeBtn.classList.toggle("is-active", isDark);
        modeBtn.setAttribute("aria-pressed", String(isDark));
        const useEl = modeBtn.querySelector("use");
        const labelEl = modeBtn.querySelector("span");
        if (useEl) {
          useEl.setAttribute(
            "href",
            isDark ? "#icon-weather-sunny" : "#icon-weather-moon",
          );
        }
        if (labelEl) {
          labelEl.textContent = isDark
            ? modeBtn.dataset.lightLabel
            : modeBtn.dataset.darkLabel;
        }
      });
    }
  }
})();
