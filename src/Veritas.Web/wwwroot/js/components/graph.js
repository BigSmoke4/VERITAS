/**
 * Access graph renderer. Draws real nodes/edges returned by
 * /api/v1/access-graph/* into an SVG surface with pan, zoom, keyboard
 * navigation and per-node inspection. No layout is faked: positions come from
 * a deterministic breadth-first layering of the actual edge list.
 */
import { Channels, emit } from '../core/events.js';

const SVG_NS = 'http://www.w3.org/2000/svg';
const LAYER_ORDER = { User: 0, Role: 1, Permission: 2, Application: 3, Resource: 4 };

export class AccessGraph {
  constructor(container, { onSelect } = {}) {
    this.container = container;
    this.onSelect = onSelect;
    this.viewBox = { x: 0, y: 0, w: 1000, h: 640 };
    this.selectedId = null;
    this.data = { nodes: [], edges: [] };
    this.build();
  }

  build() {
    this.container.innerHTML = '';
    this.svg = document.createElementNS(SVG_NS, 'svg');
    this.svg.setAttribute('viewBox', `${this.viewBox.x} ${this.viewBox.y} ${this.viewBox.w} ${this.viewBox.h}`);
    this.svg.setAttribute('role', 'application');
    this.svg.setAttribute('aria-label', 'Authorization relationship graph. Use arrow keys to move between nodes.');
    this.edgeLayer = document.createElementNS(SVG_NS, 'g');
    this.nodeLayer = document.createElementNS(SVG_NS, 'g');
    this.svg.append(this.edgeLayer, this.nodeLayer);
    this.container.appendChild(this.svg);

    this.attachPanZoom();
    this.attachKeyboard();
  }

  setData(data) {
    this.data = data ?? { nodes: [], edges: [] };
    this.layout();
    this.render();
  }

  /** Deterministic layered layout: depth = BFS distance from the first node. */
  layout() {
    const { nodes, edges } = this.data;
    if (nodes.length === 0) {
      this.positions = new Map();
      return;
    }

    const adjacency = new Map(nodes.map((n) => [n.id, []]));
    edges.forEach((e) => {
      adjacency.get(e.fromId)?.push(e.toId);
      adjacency.get(e.toId)?.push(e.fromId);
    });

    const depth = new Map();
    const queue = [nodes[0].id];
    depth.set(nodes[0].id, 0);
    while (queue.length > 0) {
      const current = queue.shift();
      for (const neighbour of adjacency.get(current) ?? []) {
        if (depth.has(neighbour)) continue;
        depth.set(neighbour, depth.get(current) + 1);
        queue.push(neighbour);
      }
    }

    const columns = new Map();
    nodes.forEach((node) => {
      const layer = LAYER_ORDER[node.type] ?? depth.get(node.id) ?? 0;
      const index = columns.get(layer) ?? 0;
      columns.set(layer, index + 1);
      this.set(node.id, layer, index);
    });

    this.positions = this.positions ?? new Map();
    const pending = [...this.pendingPositions ?? []];
    this.pendingPositions = [];
    pending.forEach(([id, x, y]) => this.positions.set(id, { x, y }));
  }

  set(id, layer, index) {
    this.pendingPositions = this.pendingPositions ?? [];
    const x = 110 + layer * 200;
    const y = 70 + index * 74;
    this.pendingPositions.push([id, x, y]);
  }

  render() {
    this.edgeLayer.innerHTML = '';
    this.nodeLayer.innerHTML = '';

    const positions = new Map();
    (this.pendingPositions ?? []).forEach(([id, x, y]) => positions.set(id, { x, y }));
    this.positions = positions;

    this.data.edges.forEach((edge) => {
      const from = positions.get(edge.fromId);
      const to = positions.get(edge.toId);
      if (!from || !to) return;

      const path = document.createElementNS(SVG_NS, 'path');
      const midX = (from.x + to.x) / 2;
      path.setAttribute('d', `M ${from.x} ${from.y} C ${midX} ${from.y}, ${midX} ${to.y}, ${to.x} ${to.y}`);
      path.setAttribute('class', 'graph-edge');
      path.dataset.from = edge.fromId;
      path.dataset.to = edge.toId;
      if (edge.label) path.setAttribute('aria-label', edge.label);
      this.edgeLayer.appendChild(path);
    });

    this.data.nodes.forEach((node) => {
      const position = positions.get(node.id) ?? { x: 60, y: 60 };
      const group = document.createElementNS(SVG_NS, 'g');
      group.setAttribute('class', `graph-node graph-node--${(node.type ?? 'node').toLowerCase()}`);
      group.setAttribute('transform', `translate(${position.x}, ${position.y})`);
      group.setAttribute('tabindex', '0');
      group.setAttribute('role', 'button');
      group.setAttribute('aria-label', `${node.type}: ${node.label}`);
      group.dataset.id = node.id;

      const rect = document.createElementNS(SVG_NS, 'rect');
      rect.setAttribute('class', 'graph-node__shape');
      rect.setAttribute('x', '-72');
      rect.setAttribute('y', '-16');
      rect.setAttribute('width', '144');
      rect.setAttribute('height', '32');
      rect.setAttribute('rx', '5');

      const label = document.createElementNS(SVG_NS, 'text');
      label.setAttribute('class', 'graph-node__label');
      label.setAttribute('dy', '4');
      label.textContent = node.label.length > 20 ? `${node.label.slice(0, 19)}\u2026` : node.label;

      group.append(rect, label);
      group.addEventListener('click', () => this.select(node.id));
      group.addEventListener('keydown', (event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          this.select(node.id);
        }
      });
      this.nodeLayer.appendChild(group);
    });
  }

  select(nodeId) {
    this.selectedId = nodeId;
    this.nodeLayer.querySelectorAll('.graph-node').forEach((el) => {
      el.classList.toggle('graph-node--selected', el.dataset.id === nodeId);
    });

    const related = new Set([nodeId]);
    this.edgeLayer.querySelectorAll('.graph-edge').forEach((edge) => {
      const touches = edge.dataset.from === nodeId || edge.dataset.to === nodeId;
      edge.classList.toggle('graph-edge--highlight', touches);
      if (touches) {
        related.add(edge.dataset.from);
        related.add(edge.dataset.to);
      }
    });

    const node = this.data.nodes.find((n) => n.id === nodeId);
    emit(Channels.graphNodeSelected, { node, related: [...related] });
    this.onSelect?.(node, [...related]);
  }

  attachPanZoom() {
    let dragging = false;
    let last = null;

    const apply = () => this.svg.setAttribute(
      'viewBox', `${this.viewBox.x} ${this.viewBox.y} ${this.viewBox.w} ${this.viewBox.h}`);

    this.svg.addEventListener('pointerdown', (event) => {
      if (event.target.closest('.graph-node')) return;
      dragging = true;
      last = { x: event.clientX, y: event.clientY };
      this.svg.setPointerCapture(event.pointerId);
    });

    this.svg.addEventListener('pointermove', (event) => {
      if (!dragging) return;
      const scale = this.viewBox.w / this.svg.clientWidth;
      this.viewBox.x -= (event.clientX - last.x) * scale;
      this.viewBox.y -= (event.clientY - last.y) * scale;
      last = { x: event.clientX, y: event.clientY };
      apply();
    });

    ['pointerup', 'pointercancel'].forEach((type) =>
      this.svg.addEventListener(type, () => { dragging = false; }));

    this.svg.addEventListener('wheel', (event) => {
      event.preventDefault();
      const factor = event.deltaY > 0 ? 1.1 : 0.9;
      const nextW = Math.min(4000, Math.max(200, this.viewBox.w * factor));
      const ratio = nextW / this.viewBox.w;
      this.viewBox.x += (this.viewBox.w - nextW) / 2;
      this.viewBox.y += (this.viewBox.h - this.viewBox.h * ratio) / 2;
      this.viewBox.w = nextW;
      this.viewBox.h *= ratio;
      apply();
    }, { passive: false });

    this.container.querySelectorAll('[data-graph-zoom]').forEach((button) => {
      button.addEventListener('click', () => {
        const factor = button.dataset.graphZoom === 'in' ? 0.8 : 1.25;
        const nextW = Math.min(4000, Math.max(200, this.viewBox.w * factor));
        const ratio = nextW / this.viewBox.w;
        this.viewBox.x += (this.viewBox.w - nextW) / 2;
        this.viewBox.y += (this.viewBox.h - this.viewBox.h * ratio) / 2;
        this.viewBox.w = nextW;
        this.viewBox.h *= ratio;
        apply();
      });
    });

    this.container.querySelector('[data-graph-reset]')?.addEventListener('click', () => {
      this.viewBox = { x: 0, y: 0, w: 1000, h: 640 };
      apply();
    });
  }

  attachKeyboard() {
    this.svg.addEventListener('keydown', (event) => {
      if (!['ArrowRight', 'ArrowLeft', 'ArrowDown', 'ArrowUp'].includes(event.key)) return;
      event.preventDefault();
      const nodes = [...this.nodeLayer.querySelectorAll('.graph-node')];
      if (nodes.length === 0) return;
      const index = nodes.findIndex((n) => n === document.activeElement);
      const delta = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : -1;
      const next = nodes[(index + delta + nodes.length) % nodes.length] ?? nodes[0];
      next.focus();
    });
  }
}
