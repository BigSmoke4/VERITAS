# Front-end conventions

The UI is server-rendered Razor. **There is no SPA framework** — no React, Angular, Vue,
Blazor, Next or Nuxt. The page arrives complete from the server; JavaScript progressively
enhances it and is never required for a screen to be legible or for a form to submit.

## CSS

All stylesheets live under `wwwroot/css/` in a fixed tree:

```
wwwroot/css/
├── site.css            ← @import manifest only; contains no visual rules
├── core/               ← variables, reset, typography, layout, accessibility
├── components/         ← button, badge, panel, input, table, tabs, modal,
│                          timeline, gauge, graph, notification, status-indicator
├── modules/            ← one file per bounded module
├── pages/              ← one file per page that needs page-specific rules
└── identity/           ← the standalone authentication screens
```

Rules:

- `site.css` is a pure manifest. Adding a visual rule there defeats the tree.
- No large inline `<style>` blocks in views.
- Every value comes from a token in `core/variables.css` — colour, spacing, radius,
  type scale, shadow. A literal hex colour in a component file is a defect.
- The visual language is a skeuomorphic control room: engraved type, bezelled panels,
  physical status lamps. The tokens encode it once.

## JavaScript

All scripts live under `wwwroot/js/` as ES modules:

```
wwwroot/js/
├── site.js             ← entry point; the only script tag in _Layout
├── core/               ← http, api-client, events, notifications, modal, validation
├── components/         ← graph, data-table, tabs, gauge, timeline, filters, modal
├── modules/            ← one file per bounded module
└── pages/              ← one file per page
```

Rules:

- `_Layout.cshtml` emits exactly one `<script type="module" src="~/js/site.js">`.
- No large inline scripts. The only global the modules expose is
  `globalThis.Veritas = { version, ready }`.
- Enhancement is opt-in through `data-*` attributes, so a view that omits an attribute
  simply does not get that behaviour.
- Every `data-*` attribute a view emits is consumed by a module, and vice versa. The
  contract is checked, not assumed.

## Accessibility

Every page provides a skip link, landmarks, a document outline, visible focus rings and
`aria-*` state on interactive widgets. Status is never conveyed by colour alone — lamps
are paired with text.
