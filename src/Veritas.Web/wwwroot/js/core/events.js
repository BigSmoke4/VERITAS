/** Minimal typed event bus so modules never reach into each other's DOM. */

const channels = new Map();

export function on(channel, handler) {
  if (!channels.has(channel)) channels.set(channel, new Set());
  channels.get(channel).add(handler);
  return () => off(channel, handler);
}

export function off(channel, handler) {
  channels.get(channel)?.delete(handler);
}

export function emit(channel, detail) {
  channels.get(channel)?.forEach((handler) => {
    try {
      handler(detail);
    } catch (error) {
      console.error(`[veritas] handler for "${channel}" threw`, error);
    }
  });
}

export const Channels = Object.freeze({
  notification: 'veritas:notification',
  decisionRendered: 'veritas:decision',
  graphNodeSelected: 'veritas:graph:node',
  filterChanged: 'veritas:filters'
});
