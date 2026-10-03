/** Control Center: scales the trend chart from the server-rendered bar heights. */
import { renderGauges } from '../components/gauge.js';

export function initDashboard(root = document) {
  renderGauges(root);

  root.querySelectorAll('[data-trend-chart]').forEach((chart) => {
    const bars = Array.from(chart.querySelectorAll('[data-allowed]'));
    if (bars.length === 0) return;

    const peak = Math.max(1, ...bars.map((bar) =>
      Number(bar.dataset.allowed ?? 0) + Number(bar.dataset.denied ?? 0)));

    bars.forEach((bar) => {
      const allowed = Number(bar.dataset.allowed ?? 0);
      const denied = Number(bar.dataset.denied ?? 0);
      bar.innerHTML = '';
      bar.appendChild(segment(allowed, peak, 'trend-chart__allow'));
      bar.appendChild(segment(denied, peak, 'trend-chart__deny'));
      bar.setAttribute('title', `${bar.dataset.label ?? ''}: ${allowed} allowed, ${denied} denied`);
    });
  });
}

function segment(value, peak, className) {
  const element = document.createElement('span');
  element.className = `trend-chart__segment ${className}`;
  element.style.height = `${Math.max(value === 0 ? 0 : 2, (value / peak) * 100)}%`;
  return element;
}
