/** Client-side sorting for already-rendered tables. Paging stays server-side. */

export function enhanceSortableTables(root = document) {
  root.querySelectorAll('table[data-sortable]').forEach((table) => {
    const headers = table.querySelectorAll('th[data-sort-key]');
    headers.forEach((header, columnIndex) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'tab';
      button.textContent = header.textContent.trim();
      button.setAttribute('aria-label', `Sort by ${header.textContent.trim()}`);
      header.textContent = '';
      header.appendChild(button);
      header.setAttribute('aria-sort', 'none');

      button.addEventListener('click', () => {
        const current = header.getAttribute('aria-sort');
        const direction = current === 'ascending' ? 'descending' : 'ascending';
        headers.forEach((h) => h.setAttribute('aria-sort', 'none'));
        header.setAttribute('aria-sort', direction);
        sortTable(table, columnIndex, direction, header.dataset.numeric !== undefined);
      });
    });
  });
}

function sortTable(table, columnIndex, direction, numeric) {
  const tbody = table.querySelector('tbody');
  if (!tbody) return;

  const rows = Array.from(tbody.querySelectorAll('tr'));
  rows.sort((a, b) => {
    const left = cellValue(a, columnIndex);
    const right = cellValue(b, columnIndex);
    if (numeric) return direction === 'ascending' ? left - right : right - left;
    return direction === 'ascending'
      ? String(left).localeCompare(String(right))
      : String(right).localeCompare(String(left));
  });
  rows.forEach((row) => tbody.appendChild(row));
}

function cellValue(row, columnIndex) {
  const cell = row.children[columnIndex];
  const raw = cell?.dataset.sortValue ?? cell?.textContent?.trim() ?? '';
  const parsed = Number(raw);
  return Number.isNaN(parsed) ? raw : parsed;
}
