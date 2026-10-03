/** Accessible modal: focus trap, Escape to close, focus restored on close. */

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function openModal(dialogElement) {
  const previouslyFocused = document.activeElement;
  dialogElement.hidden = false;
  document.body.style.overflow = 'hidden';

  const focusables = () => Array.from(dialogElement.querySelectorAll(FOCUSABLE)).filter((el) => el.offsetParent !== null);
  focusables()[0]?.focus();

  function onKeyDown(event) {
    if (event.key === 'Escape') {
      event.preventDefault();
      close();
      return;
    }
    if (event.key !== 'Tab') return;

    const items = focusables();
    if (items.length === 0) return;
    const first = items[0];
    const last = items[items.length - 1];

    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  function close() {
    document.removeEventListener('keydown', onKeyDown);
    dialogElement.hidden = true;
    document.body.style.overflow = '';
    previouslyFocused?.focus?.();
  }

  document.addEventListener('keydown', onKeyDown);
  dialogElement.addEventListener('click', (event) => {
    if (event.target === dialogElement || event.target.hasAttribute('data-modal-close')) close();
  });

  return close;
}

export function bindModalTriggers() {
  document.querySelectorAll('[data-modal-open]').forEach((trigger) => {
    trigger.addEventListener('click', () => {
      const target = document.getElementById(trigger.dataset.modalOpen);
      if (target) openModal(target);
    });
  });
}
