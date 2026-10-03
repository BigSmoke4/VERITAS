/** Sets gauge/dial fill from a data attribute so markup stays declarative. */

export function renderGauges(root = document) {
  root.querySelectorAll('[data-gauge]').forEach((element) => {
    const value = clamp(Number(element.dataset.gauge ?? 0));
    element.style.setProperty('--gauge-value', String(value));
    const tone = toneFor(value);
    if (tone) element.classList.add(`gauge--${tone}`);
  });

  root.querySelectorAll('[data-dial]').forEach((element) => {
    const value = clamp(Number(element.dataset.dial ?? 0));
    element.style.setProperty('--dial-value', String(value));
    element.style.setProperty('--dial-color', dialColor(value));
  });
}

function clamp(value) {
  if (Number.isNaN(value)) return 0;
  return Math.max(0, Math.min(100, value));
}

function toneFor(value) {
  if (value >= 80) return 'alarm';
  if (value >= 60) return 'warn';
  return null;
}

function dialColor(value) {
  if (value >= 80) return 'var(--status-alarm)';
  if (value >= 60) return 'var(--status-warn)';
  if (value >= 30) return 'var(--status-info)';
  return 'var(--status-ok)';
}
