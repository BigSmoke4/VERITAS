/** Policy Studio page wiring. */
import { enhancePolicyBuilder } from '../modules/policies.js';
import { enhanceFilterForms } from '../components/filters.js';

export function initPolicyStudio(root = document) {
  enhancePolicyBuilder(root);
  enhanceFilterForms(root);
}
