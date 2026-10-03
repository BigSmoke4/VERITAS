/**
 * Client-side validation mirrors the server's FluentValidation rules. It is a
 * convenience only — the server re-validates every submission, so bypassing
 * this file cannot produce an invalid write.
 */

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PERMISSION_PATTERN = /^[a-z0-9][a-z0-9._-]*\.[a-z0-9][a-z0-9._-]*$/i;

export const validators = {
  required: (value) => (String(value ?? '').trim() === '' ? 'This field is required.' : null),
  guid: (value) => (GUID_PATTERN.test(String(value ?? '')) ? null : 'A valid identifier (GUID) is required.'),
  permissionKey: (value) =>
    PERMISSION_PATTERN.test(String(value ?? '')) ? null : "Permission keys must follow 'resource.action'.",
  positiveInteger: (value) => (Number.isInteger(Number(value)) && Number(value) > 0 ? null : 'Enter a positive whole number.'),
  url: (value) => {
    try {
      const parsed = new URL(String(value));
      return parsed.protocol === 'https:' || parsed.protocol === 'http:' ? null : 'Only http(s) URLs are allowed.';
    } catch {
      return 'Enter a valid absolute URL.';
    }
  }
};

function setFieldState(input, message) {
  const field = input.closest('.field');
  let slot = field?.querySelector('.field__error');

  if (message) {
    input.setAttribute('aria-invalid', 'true');
    if (!slot && field) {
      slot = document.createElement('p');
      slot.className = 'field__error';
      field.appendChild(slot);
    }
    if (slot) {
      slot.id = slot.id || `${input.id || input.name}-error`;
      slot.textContent = message;
      input.setAttribute('aria-describedby', slot.id);
    }
  } else {
    input.removeAttribute('aria-invalid');
    if (slot) slot.remove();
  }
}

/** Wires every [data-validate] input in a form and blocks submit when invalid. */
export function enhanceForm(form) {
  const inputs = Array.from(form.querySelectorAll('[data-validate]'));

  function validateInput(input) {
    const rules = input.dataset.validate.split(',').map((r) => r.trim()).filter(Boolean);
    for (const rule of rules) {
      const message = validators[rule]?.(input.value);
      if (message) {
        setFieldState(input, message);
        return false;
      }
    }
    setFieldState(input, null);
    return true;
  }

  inputs.forEach((input) => {
    input.addEventListener('blur', () => validateInput(input));
    input.addEventListener('input', () => {
      if (input.getAttribute('aria-invalid') === 'true') validateInput(input);
    });
  });

  form.addEventListener('submit', (event) => {
    const firstInvalid = inputs.find((input) => !validateInput(input));
    if (firstInvalid) {
      event.preventDefault();
      firstInvalid.focus();
    }
  });
}

export function enhanceAllForms(root = document) {
  root.querySelectorAll('form[data-validate-form]').forEach(enhanceForm);
}
