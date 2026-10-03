/**
 * Policy Studio: adds/removes structured condition rows. The form posts
 * attribute/operator/value triples — never expression text — so there is no
 * client-side path that could introduce executable policy input.
 */

const ATTRIBUTES = [
  'user.department', 'user.status', 'user.riskLevel', 'user.clearance', 'user.roles',
  'resource.classification', 'resource.department', 'resource.environment', 'resource.type',
  'application.environment',
  'request.action', 'request.environment', 'request.deviceTrust',
  'request.authenticationStrength', 'request.riskLevel'
];

const OPERATORS = ['Equals', 'NotEquals', 'LessThanOrEqual', 'GreaterThanOrEqual', 'In'];

export function enhancePolicyBuilder(root = document) {
  root.querySelectorAll('[data-rule-builder]').forEach((builder) => enhanceBuilder(builder));
}

function enhanceBuilder(builder) {
  let conditionSeq = Number(builder.dataset.conditionSeq ?? builder.querySelectorAll('.condition-row').length);

  builder.querySelectorAll('[data-add-condition]').forEach((button) => {
    button.addEventListener('click', () => {
      const ruleIndex = button.dataset.addCondition;
      const host = builder.querySelector(`[data-conditions-for="${ruleIndex}"]`);
      if (!host) return;
      host.appendChild(buildConditionRow(ruleIndex, conditionSeq++));
    });
  });

  builder.addEventListener('click', (event) => {
    const remove = event.target.closest('[data-remove-condition]');
    if (remove) remove.closest('.condition-row')?.remove();
  });
}

function buildConditionRow(ruleIndex, index) {
  const row = document.createElement('div');
  row.className = 'condition-row';

  // Every condition carries the index of the rule it belongs to, so the server
  // can regroup the parallel arrays the form posts.
  const ruleIndexInput = document.createElement('input');
  ruleIndexInput.type = 'hidden';
  ruleIndexInput.name = 'ruleIndex';
  ruleIndexInput.value = String(ruleIndex);
  row.appendChild(ruleIndexInput);

  const attribute = document.createElement('select');
  attribute.className = 'select';
  attribute.name = 'conditionAttribute';
  attribute.dataset.ruleIndex = ruleIndex;
  ATTRIBUTES.forEach((value) => attribute.appendChild(option(value, value)));

  const operator = document.createElement('select');
  operator.className = 'select';
  operator.name = 'conditionOperator';
  OPERATORS.forEach((value) => operator.appendChild(option(value, value)));

  const value = document.createElement('input');
  value.className = 'input';
  value.name = 'conditionValue';
  value.placeholder = 'value or attribute path';
  value.setAttribute('aria-label', `Condition ${index + 1} value`);

  const isRef = document.createElement('label');
  isRef.className = 'row tiny';
  const checkbox = document.createElement('input');
  checkbox.type = 'checkbox';
  checkbox.name = 'conditionIsRef';
  checkbox.value = 'true';
  // A checkbox only posts when checked, so the "false" marker has to travel with it
  // or the conditionIsRef array falls out of step with conditionAttribute.
  const notRef = document.createElement('input');
  notRef.type = 'hidden';
  notRef.name = 'conditionIsRef';
  notRef.value = 'false';
  isRef.append(checkbox, notRef, document.createTextNode('attribute ref'));

  const remove = document.createElement('button');
  remove.type = 'button';
  remove.className = 'btn btn--sm btn--ghost';
  remove.dataset.removeCondition = '';
  remove.setAttribute('aria-label', `Remove condition ${index + 1}`);
  remove.textContent = '\u2715';

  row.append(attribute, operator, value, isRef, remove);
  return row;
}

function option(value, label) {
  const element = document.createElement('option');
  element.value = value;
  element.textContent = label;
  return element;
}
