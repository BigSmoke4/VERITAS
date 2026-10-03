/** Identity module behaviours: copy-to-clipboard for one-time secrets, confirm guards. */
import { notify } from '../core/notifications.js';

export function enhanceIdentityPages(root = document) {
  root.querySelectorAll('[data-copy-target]').forEach((button) => {
    button.addEventListener('click', async () => {
      const source = document.getElementById(button.dataset.copyTarget);
      const text = source?.textContent?.trim() ?? source?.value ?? '';
      if (!text) return;
      try {
        await navigator.clipboard.writeText(text);
        notify({ title: 'Copied to clipboard', tone: 'ok', timeoutMs: 3000 });
      } catch {
        notify({ title: 'Clipboard unavailable', body: 'Select the value and copy it manually.', tone: 'warn' });
      }
    });
  });

  root.querySelectorAll('form[data-confirm]').forEach((form) => {
    form.addEventListener('submit', (event) => {
      if (!window.confirm(form.dataset.confirm)) event.preventDefault();
    });
  });
}
