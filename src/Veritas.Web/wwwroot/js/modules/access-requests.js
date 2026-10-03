/** Access request pages: idempotent approval submission with a generated key. */
import { api } from '../core/api-client.js';
import { notify } from '../core/notifications.js';

export function enhanceApprovalForms(root = document) {
  root.querySelectorAll('form[data-approval-form]').forEach((form) => {
    // One key per form instance: a double-click or a retry replays safely.
    const idempotencyKey = form.dataset.idempotencyKey
      ?? (globalThis.crypto?.randomUUID?.() ?? `ap-${Date.now()}`);
    form.dataset.idempotencyKey = idempotencyKey;

    form.addEventListener('submit', () => {
      form.querySelectorAll('button[type="submit"]').forEach((button) => { button.disabled = true; });
    });
  });
}

export { api };
