(() => {
  const root = document.documentElement;
  const system = matchMedia('(prefers-color-scheme: dark)');
  let preference;
  try { preference = localStorage.getItem('mac-explorer-theme'); } catch {}
  let explicit = ['light', 'dark'].includes(preference);
  const button = document.querySelector('.theme-toggle');
  function apply(theme) {
    root.dataset.theme = theme;
    const label = theme === 'dark' ? '切换到浅色外观' : '切换到深色外观';
    button.setAttribute('aria-label', label); button.title = label;
    button.querySelector('use').setAttribute('href', '../Assets/icons/fluent.svg#' + (theme === 'dark' ? 'weather-sunny' : 'weather-moon'));
    document.querySelector('meta[name="theme-color"]').content = theme === 'dark' ? '#17191e' : '#ffffff';
  }
  apply(explicit ? preference : system.matches ? 'dark' : 'light');
  button.addEventListener('click', () => {
    explicit = true;
    apply(root.dataset.theme === 'dark' ? 'light' : 'dark');
    try { localStorage.setItem('mac-explorer-theme', root.dataset.theme); } catch {}
  });
  system.addEventListener('change', event => { if (!explicit) apply(event.matches ? 'dark' : 'light'); });
})();
