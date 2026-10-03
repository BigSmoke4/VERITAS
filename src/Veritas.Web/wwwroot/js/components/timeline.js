/** Progressively enhances a static timeline with relative timestamps. */

export function enhanceTimelines(root = document) {
  root.querySelectorAll('[data-timestamp]').forEach((element) => {
    const parsed = new Date(element.dataset.timestamp);
    if (Number.isNaN(parsed.getTime())) return;
    const relative = relativeTime(parsed);
    element.setAttribute('title', parsed.toISOString());
    const suffix = document.createElement('span');
    suffix.className = 'timeline__meta';
    suffix.textContent = ` \u00b7 ${relative}`;
    element.appendChild(suffix);
  });
}

export function relativeTime(date, now = new Date()) {
  const seconds = Math.round((now - date) / 1000);
  const units = [
    ['year', 31536000], ['month', 2592000], ['day', 86400],
    ['hour', 3600], ['minute', 60]
  ];
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  for (const [unit, secondsInUnit] of units) {
    if (Math.abs(seconds) >= secondsInUnit) {
      return formatter.format(-Math.round(seconds / secondsInUnit), unit);
    }
  }
  return formatter.format(-seconds, 'second');
}
