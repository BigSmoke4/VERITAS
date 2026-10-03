/**
 * Thin transport layer. Centralises correlation IDs, JSON handling and the
 * ProblemDetails shape the API returns on failure so no caller re-implements it.
 */

const CORRELATION_HEADER = 'X-Correlation-ID';

export class ApiError extends Error {
  constructor(status, problem, correlationId) {
    super(problem?.title ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
    this.correlationId = correlationId;
  }
}

function newCorrelationId() {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
  return `c-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

export async function request(path, { method = 'GET', body, headers = {}, idempotencyKey, signal } = {}) {
  const correlationId = newCorrelationId();
  const finalHeaders = {
    Accept: 'application/json',
    [CORRELATION_HEADER]: correlationId,
    ...headers
  };

  if (body !== undefined) finalHeaders['Content-Type'] = 'application/json';
  if (idempotencyKey) finalHeaders['Idempotency-Key'] = idempotencyKey;

  const response = await fetch(path, {
    method,
    headers: finalHeaders,
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: 'same-origin',
    signal
  });

  if (response.status === 204) return null;

  const contentType = response.headers.get('content-type') ?? '';
  const payload = contentType.includes('application/json') ? await response.json() : await response.text();

  if (!response.ok) throw new ApiError(response.status, typeof payload === 'object' ? payload : { title: payload }, correlationId);
  return payload;
}

export const get = (path, options) => request(path, { ...options, method: 'GET' });
export const post = (path, body, options) => request(path, { ...options, method: 'POST', body });
