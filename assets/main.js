(function () {
  const storageKey = 'uc-theme';
  const root = document.documentElement;

  function applyTheme(theme) {
    root.setAttribute('data-theme', theme);
    try {
      localStorage.setItem(storageKey, theme);
    } catch (_) {
      /* ignore */
    }
    const toggle = document.querySelector('[data-theme-toggle]');
    if (toggle) {
      const isDark = theme === 'dark';
      toggle.setAttribute('aria-pressed', isDark ? 'true' : 'false');
      toggle.textContent = isDark ? 'Light mode' : 'Dark mode';
    }
  }

  function systemTheme() {
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }

  function initTheme() {
    let theme = 'dark';
    try {
      theme = localStorage.getItem(storageKey) || systemTheme();
    } catch (_) {
      theme = systemTheme();
    }
    applyTheme(theme);
  }

  function initNav() {
    const button = document.querySelector('[data-nav-toggle]');
    const panel = document.querySelector('[data-nav-panel]');
    if (!button || !panel) return;
    button.addEventListener('click', () => {
      const open = panel.classList.toggle('is-open');
      button.setAttribute('aria-expanded', open ? 'true' : 'false');
    });
    panel.querySelectorAll('a').forEach((link) => {
      link.addEventListener('click', () => {
        panel.classList.remove('is-open');
        button.setAttribute('aria-expanded', 'false');
      });
    });
  }

  document.addEventListener('DOMContentLoaded', () => {
    initTheme();
    initNav();
    const toggle = document.querySelector('[data-theme-toggle]');
    if (toggle) {
      toggle.addEventListener('click', () => {
        const next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
        applyTheme(next);
      });
    }
  });
})();