/** Risk module: renders the 3x3 matrix heat from server-computed counts. */
import { renderGauges } from '../components/gauge.js';

export function enhanceRiskDashboard(root = document) {
  renderGauges(root);

  root.querySelectorAll('[data-matrix-cell]').forEach((cell) => {
    const impact = Number(cell.dataset.impact ?? 0);
    const likelihood = Number(cell.dataset.likelihood ?? 0);
    const heat = Math.max(1, Math.min(3, impact + likelihood));
    cell.dataset.heat = String(heat);
  });
}
