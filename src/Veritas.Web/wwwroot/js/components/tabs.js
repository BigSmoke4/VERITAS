/** Roving-tabindex tablist per WAI-ARIA APG. */

export function enhanceTabs(root = document) {
  root.querySelectorAll('[role="tablist"]').forEach((tablist) => {
    const tabs = Array.from(tablist.querySelectorAll('[role="tab"]'));

    tabs.forEach((tab, index) => {
      tab.setAttribute('tabindex', tab.getAttribute('aria-selected') === 'true' ? '0' : '-1');

      tab.addEventListener('keydown', (event) => {
        let next = null;
        if (event.key === 'ArrowRight') next = tabs[(index + 1) % tabs.length];
        else if (event.key === 'ArrowLeft') next = tabs[(index - 1 + tabs.length) % tabs.length];
        else if (event.key === 'Home') next = tabs[0];
        else if (event.key === 'End') next = tabs[tabs.length - 1];
        if (!next) return;
        event.preventDefault();
        activate(next);
        next.focus();
      });

      tab.addEventListener('click', () => activate(tab));
    });

    function activate(selected) {
      tabs.forEach((tab) => {
        const isSelected = tab === selected;
        tab.setAttribute('aria-selected', String(isSelected));
        tab.setAttribute('tabindex', isSelected ? '0' : '-1');
        const panel = document.getElementById(tab.getAttribute('aria-controls'));
        if (panel) panel.hidden = !isSelected;
      });
    }
  });
}
