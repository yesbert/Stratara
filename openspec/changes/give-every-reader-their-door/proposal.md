> **Status:** approved

# Give every reader their door

## Why

Stratara does many things well, and the front door says so all at once. The README and the docs
landing open with four compliance arguments — tamper-evidence, tenant-bound encryption, GDPR
erasure, tenant isolation — that are true, unique and the right story for one of three audiences.
The other two never find themselves: someone who wants a lean mediator sees a 25-package framework,
someone who wants event sourcing sees a security product. Both leave. The owner named this on
2026-09-03 as the likeliest reason the framework has drawn little attention, and asked for the
README and the landing to make each audience's entry point obvious, with pictures and code.

The docs site is to become the stratara.tech homepage, so it has to carry the marketing weight a
README cannot: a hero, routing by audience, visual structure, and a place a marketing article can
link to.

## What Changes

- **The landing page becomes a real homepage.** A hero with one positioning sentence and two
  calls to action; three door cards — *Mediator only*, *Event sourcing and CQRS*, *Multi-tenant
  SaaS with audit-grade evidence* — each with an install line, a short code block and a "you do not
  need" line; a "how it grows" diagram from mediator alone to the full stack; a feature grid; the
  performance numbers; an honest comparison table against MediatR, Marten/Wolverine, MassTransit
  and EventStoreDB on license, scope and approach; a closing call to action. Built as a custom
  DocFX modern-template landing with raw HTML, Bootstrap 5 and project CSS.
- **The README follows the same structure in short form.** Logo, positioning sentence, badges,
  the three doors with code, the growth diagram in Mermaid (GitHub renders it), the "why Stratara"
  block condensed, performance numbers, then documentation, install, license and contributing.
  The 25-package table moves to the docs (`overview/packages.md`), linked from both.
- **Images.** A growth diagram as an SVG that works in light and dark mode, door-card icons from
  Bootstrap Icons already shipped by the template, and the existing logo in the hero. A terminal
  recording and a GitHub social-preview image are noted as follow-ups; they need tooling and an
  upload the repository cannot carry.
- Every existing docs page and TOC entry stays. No package, API or behaviour changes.

## Capabilities

### New Capabilities

_none_

### Modified Capabilities

_none — documentation and presentation only; the change declares `skip_specs`._

## Impact

- `docs/index.md` — rewritten as the landing.
- `docs/templates/stratara/public/main.css` — landing styles; `docs/templates/stratara/public/main.js`
  (new) — theme default and icon links.
- `docs/assets/how-it-grows.svg` (new) — the growth diagram; `docs/assets/logo.png` reused.
- `docs/overview/packages.md` (new) and `docs/overview/toc.yml` — the package table's new home.
- `README.md` — restructured; the package table replaced by a link.
- `docs/docfx.json` — `_appTitle` and description metadata for the homepage.
- `tests/Stratara.Documentation.Tests` — the README and docs snippets stay compilable; any README
  snippet the tests extract must still compile.
- No changelog entry: nothing a package consumer observes changes.
