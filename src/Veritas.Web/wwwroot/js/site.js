/**
 * VERITAS front-end entry point (ES module).
 *
 * Razor remains the rendering architecture: this file only enhances the
 * server-rendered document. Every initialiser is guarded, so a page that does
 * not contain the relevant markup simply does nothing — no global side effects,
 * no framework, no SPA routing.
 */
import { hydrateServerNotices } from './core/notifications.js';
import { bindModalTriggers } from './core/modal.js';
import { enhanceAllForms } from './core/validation.js';
import { enhanceTabs } from './components/tabs.js';
import { enhanceSortableTables } from './components/data-table.js';
import { enhanceFilterForms } from './components/filters.js';
import { renderGauges } from './components/gauge.js';
import { enhanceTimelines } from './components/timeline.js';

import { enhanceIdentityPages } from './modules/identity.js';
import { enhancePolicyBuilder } from './modules/policies.js';
import { enhanceAuthorizationConsole } from './modules/authorization.js';
import { enhanceApprovalForms } from './modules/access-requests.js';
import { enhanceGrantCountdowns } from './modules/privileged-access.js';
import { enhanceRiskDashboard } from './modules/risk.js';

import { initDashboard } from './pages/dashboard.js';
import { initPolicyStudio } from './pages/policy-studio.js';
import { initAccessGraph } from './pages/access-graph.js';
import { initAuditExplorer } from './pages/audit-explorer.js';

function boot() {
  hydrateServerNotices();
  bindModalTriggers();
  enhanceAllForms();
  enhanceTabs();
  enhanceSortableTables();
  enhanceFilterForms();
  renderGauges();
  enhanceTimelines();

  enhanceIdentityPages();
  enhancePolicyBuilder();
  enhanceAuthorizationConsole();
  enhanceApprovalForms();
  enhanceGrantCountdowns();
  enhanceRiskDashboard();

  initDashboard();
  initPolicyStudio();
  initAccessGraph();
  initAuditExplorer();

  // Exposes a tiny, documented surface for operators scripting in the console.
  // Deliberately does not expose any privileged capability.
  globalThis.Veritas = Object.freeze({ version: '1.0.0', ready: true });
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot, { once: true });
} else {
  boot();
}
