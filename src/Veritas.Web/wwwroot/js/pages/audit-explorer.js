/** Audit explorer page wiring. */
import { enhanceAuditExplorer } from '../modules/audit.js';
import { enhanceSortableTables } from '../components/data-table.js';
import { enhanceFilterForms } from '../components/filters.js';

export function initAuditExplorer(root = document) {
  enhanceAuditExplorer(root);
  enhanceSortableTables(root);
  enhanceFilterForms(root);
}
