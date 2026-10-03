/** Submits a filter form as a GET so results stay linkable and bookmarkable. */

export function enhanceFilterForms(root = document) {
  root.querySelectorAll('form[data-filter-form]').forEach((form) => {
    form.addEventListener('submit', () => {
      Array.from(form.elements).forEach((element) => {
        if (element.name && element.value === '') element.removeAttribute('name');
      });
    });

    form.querySelectorAll('[data-autosubmit]').forEach((element) => {
      element.addEventListener('change', () => form.requestSubmit());
    });
  });
}
