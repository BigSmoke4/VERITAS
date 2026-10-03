/** Live countdown against the grant's real expiry timestamp. */
import { relativeTime } from '../components/timeline.js';

export function enhanceGrantCountdowns(root = document) {
  const nodes = root.querySelectorAll('[data-expires-at]');
  if (nodes.length === 0) return;

  const tick = () => {
    const now = new Date();
    nodes.forEach((node) => {
      const expiry = new Date(node.dataset.expiresAt);
      if (Number.isNaN(expiry.getTime())) return;

      const remainingMs = expiry - now;
      node.classList.remove('countdown--expiring', 'countdown--expired');

      if (remainingMs <= 0) {
        node.textContent = 'EXPIRED';
        node.classList.add('countdown--expired');
        return;
      }

      const minutes = Math.floor(remainingMs / 60000);
      const seconds = Math.floor((remainingMs % 60000) / 1000);
      node.textContent = `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
      node.setAttribute('title', `Expires ${expiry.toISOString()} (${relativeTime(expiry, now)})`);
      if (minutes < 5) node.classList.add('countdown--expiring');
    });
  };

  tick();
  setInterval(tick, 1000);
}
