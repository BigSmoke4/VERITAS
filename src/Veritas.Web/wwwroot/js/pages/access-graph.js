/** Access graph page: loads real graph data from the API and renders it. */
import { api } from '../core/api-client.js';
import { ApiError } from '../core/http.js';
import { notify } from '../core/notifications.js';
import { AccessGraph } from '../components/graph.js';

export function initAccessGraph(root = document) {
  const canvas = root.querySelector('[data-graph-canvas]');
  if (!canvas) return;

  const graph = new AccessGraph(canvas, { onSelect: (node, related) => renderInspector(root, node, related) });
  const form = root.querySelector('[data-graph-form]');
  const emptyState = root.querySelector('[data-graph-empty]');

  async function load(mode, id) {
    if (!id) return;
    try {
      const data = mode === 'resource'
        ? await api.accessGraphForResource(id)
        : await api.accessGraphForUser(id);

      graph.setData({ nodes: data.nodes ?? [], edges: data.edges ?? [] });
      if (emptyState) emptyState.hidden = (data.nodes?.length ?? 0) > 0;
      canvas.hidden = (data.nodes?.length ?? 0) === 0;
      renderStats(root, data);
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'Could not load the graph.';
      notify({ title: 'Graph unavailable', body: message, tone: 'alarm' });
    }
  }

  form?.addEventListener('submit', (event) => {
    event.preventDefault();
    const data = Object.fromEntries(new FormData(form).entries());
    load(data.mode ?? 'user', data.id);
  });

  const initialMode = form?.querySelector('[name="mode"]')?.value;
  const initialId = form?.querySelector('[name="id"]')?.value;
  if (initialId) load(initialMode ?? 'user', initialId);
}

function renderInspector(root, node, related) {
  const host = root.querySelector('[data-graph-inspector]');
  if (!host) return;
  host.innerHTML = '';

  if (!node) {
    host.textContent = 'Select a node to inspect it.';
    return;
  }

  host.append(row('Type', node.type), row('Label', node.label), row('Id', node.id), row('Linked', String(related.length)));
}

function renderStats(root, data) {
  const host = root.querySelector('[data-graph-stats]');
  if (!host) return;
  host.innerHTML = '';
  host.append(row('Nodes', String(data.nodes?.length ?? 0)), row('Edges', String(data.edges?.length ?? 0)));
}

function row(key, value) {
  const wrapper = document.createElement('div');
  wrapper.className = 'graph-inspector__row';
  const keyNode = document.createElement('span');
  keyNode.className = 'graph-inspector__key';
  keyNode.textContent = key;
  const valueNode = document.createElement('span');
  valueNode.className = 'mono';
  valueNode.textContent = value;
  wrapper.append(keyNode, valueNode);
  return wrapper;
}
