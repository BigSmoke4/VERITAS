/** Audit explorer: keeps filter state in the URL and formats timestamps. */
import { relativeTime } from '../components/timeline.js';

export function enhanceAuditExplorer(root = document) {
  root.querySelectorAll('[data-audit-time]').forEach((node) => {
    const parsed = new Date(node.dataset.auditTime);
    if (Number.isNaN(parsed.getTime())) return;
    node.setAttribute('title', parsed.toISOString());
    const relative = document.createElement('span');
    relative.className = 'muted small';
    relative.textContent = ` (${relativeTime(parsed)})`;
    node.appendChild(relative);
  });
}
