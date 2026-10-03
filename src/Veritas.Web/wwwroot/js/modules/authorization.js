/**
 * Authorization decision console: posts a real request to /api/v1/authorize and
 * renders the returned decision, reasons and check transcript. Nothing is
 * simulated client-side — an empty result means no call was made.
 */
import { api } from '../core/api-client.js';
import { ApiError } from '../core/http.js';
import { notify } from '../core/notifications.js';

export function enhanceAuthorizationConsole(root = document) {
  const form = root.querySelector('[data-authorize-form]');
  if (!form) return;

  const output = root.querySelector('[data-authorize-output]');

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const data = Object.fromEntries(new FormData(form).entries());

    const payload = {
      subject: data.subject,
      resource: data.resource,
      action: data.action,
      environment: data.environment || 'production',
      context: {
        ip: data.ip || null,
        deviceTrust: data.deviceTrust || null,
        authenticationStrength: data.authenticationStrength || null
      }
    };

    const button = form.querySelector('button[type="submit"]');
    button.disabled = true;
    const startedAt = performance.now();

    try {
      const result = await api.authorize(payload, data.idempotencyKey || undefined);
      const latencyMs = Math.round(performance.now() - startedAt);
      renderDecision(output, result, latencyMs);
    } catch (error) {
      if (error instanceof ApiError) {
        notify({ title: `Request failed (${error.status})`, body: error.message, tone: 'alarm' });
        output.innerHTML = '';
      } else {
        throw error;
      }
    } finally {
      button.disabled = false;
    }
  });
}

function renderDecision(host, result, latencyMs) {
  if (!host) return;
  host.innerHTML = '';

  const banner = document.createElement('div');
  banner.className = `decision-banner decision-banner--${variant(result.decision)}`;

  const lamp = document.createElement('span');
  lamp.className = `status-lamp status-lamp--lg status-lamp--${lampTone(result.decision)}`;

  const text = document.createElement('div');
  const decision = document.createElement('p');
  decision.className = 'decision-banner__result';
  decision.textContent = result.decision;

  const meta = document.createElement('p');
  meta.className = 'small muted';
  meta.textContent = `${result.decisionId} · risk ${result.riskScore ?? 'n/a'} · policy ${result.policyVersion ?? 'none'} · ${latencyMs} ms`;

  text.append(decision, meta);
  banner.append(lamp, text);
  host.appendChild(banner);

  if (result.reasons?.length) {
    const list = document.createElement('ul');
    list.className = 'check-list';
    result.reasons.forEach((reason) => {
      const item = document.createElement('li');
      item.className = 'check-list__item';
      const mark = document.createElement('span');
      mark.className = 'check-mark check-mark--pass';
      mark.textContent = '\u2022';
      const body = document.createElement('div');
      body.textContent = reason;
      item.append(mark, body);
      list.appendChild(item);
    });
    host.appendChild(list);
  }
}

function variant(decision) {
  return { ALLOW: 'allow', DENY: 'deny', REQUIRE_APPROVAL: 'approval', REQUIRE_MFA: 'mfa' }[decision] ?? 'mfa';
}

function lampTone(decision) {
  return { ALLOW: 'ok', DENY: 'alarm', REQUIRE_APPROVAL: 'warn', REQUIRE_MFA: 'info' }[decision] ?? 'idle';
}
