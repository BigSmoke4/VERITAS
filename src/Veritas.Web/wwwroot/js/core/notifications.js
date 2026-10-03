/**
 * Toast annunciators. Announced to screen readers through a polite live region
 * that is created once and reused.
 */
import { Channels, emit } from './events.js';

let region = null;
let liveRegion = null;

function ensureRegions() {
  if (!region) {
    region = document.querySelector('[data-toast-region]');
    if (!region) {
      region = document.createElement('div');
      region.className = 'toast-region';
      region.setAttribute('data-toast-region', '');
      document.body.appendChild(region);
    }
  }
  if (!liveRegion) {
    liveRegion = document.querySelector('[data-live-region]');
    if (!liveRegion) {
      liveRegion = document.createElement('div');
      liveRegion.className = 'visually-hidden';
      liveRegion.setAttribute('role', 'status');
      liveRegion.setAttribute('aria-live', 'polite');
      liveRegion.setAttribute('data-live-region', '');
      document.body.appendChild(liveRegion);
    }
  }
}

const VARIANT_BY_TONE = { ok: 'toast--ok', warn: 'toast--warn', alarm: 'toast--alarm', info: '' };

export function notify({ title, body = '', tone = 'info', timeoutMs = 6000 }) {
  ensureRegions();

  const toast = document.createElement('div');
  toast.className = `toast ${VARIANT_BY_TONE[tone] ?? ''}`.trim();
  toast.setAttribute('role', tone === 'alarm' ? 'alert' : 'status');

  const content = document.createElement('div');
  const heading = document.createElement('p');
  heading.className = 'toast__title';
  heading.textContent = title;
  content.appendChild(heading);

  if (body) {
    const paragraph = document.createElement('p');
    paragraph.className = 'toast__body';
    paragraph.textContent = body;
    content.appendChild(paragraph);
  }

  const close = document.createElement('button');
  close.type = 'button';
  close.className = 'toast__close';
  close.setAttribute('aria-label', `Dismiss notification: ${title}`);
  close.textContent = '\u00d7';
  close.addEventListener('click', () => dismiss());

  toast.append(content, close);
  region.appendChild(toast);

  liveRegion.textContent = `${title}. ${body}`.trim();
  emit(Channels.notification, { title, body, tone });

  const timer = timeoutMs > 0 ? setTimeout(dismiss, timeoutMs) : null;

  function dismiss() {
    if (timer) clearTimeout(timer);
    toast.remove();
  }

  return dismiss;
}

/** Reads a server-rendered TempData notice and surfaces it as a toast. */
export function hydrateServerNotices() {
  document.querySelectorAll('[data-server-notice]').forEach((node) => {
    const message = node.textContent?.trim();
    if (message) notify({ title: message, tone: node.dataset.tone ?? 'info', timeoutMs: 9000 });
    node.remove();
  });
}
