/**
 * Typed surface over the VERITAS v1 API. Every function here maps to a real
 * endpoint; anything not implemented server-side is deliberately absent rather
 * than stubbed with a fake response.
 */
import { get, post } from './http.js';

export const api = {
  authorize: (payload, idempotencyKey) =>
    post('/api/v1/authorize', payload, { idempotencyKey }),

  accessGraphForUser: (userId) => get(`/api/v1/access-graph/user/${encodeURIComponent(userId)}`),
  accessGraphForResource: (resourceId) => get(`/api/v1/access-graph/resource/${encodeURIComponent(resourceId)}`),

  submitApproval: (accessRequestId, decision, idempotencyKey) =>
    post(`/api/v1/access-requests/${encodeURIComponent(accessRequestId)}/approvals`, decision, { idempotencyKey }),

  simulatePolicy: (payload) => post('/api/v1/policy-simulator/simulate', payload),

  health: () => get('/health')
};

export default api;
