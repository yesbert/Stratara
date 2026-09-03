# Design — Give every reader their door

## Context

See `proposal.md` → *Why*. What matters here is what the site is built with and what patterns the
comparable projects use.

The docs are DocFX with the `default` + `modern` templates and a project template at
`docs/templates/stratara` that today carries one CSS rule (`#logo` height). `docs/index.md` already
declares `_layout: landing`, which the modern template renders without the left TOC. DocFX passes
raw HTML in Markdown through, and the modern template ships Bootstrap 5 and Bootstrap Icons, so a
landing can be composed from HTML + utility classes + a small project stylesheet without exporting
or forking the template. `docs.yml` runs `docfx build --warningsAsErrors` on every push to `main`.

Patterns observed on 2026-09-03 across comparable .NET projects: Marten routes two audiences
(document DB, event store) with cards and navigation; Wolverine leads with one headline, one CTA and
three feature cards; FastEndpoints puts a real code sample above the fold and a large feature grid
below, and addresses migration explicitly. README guidance for 2026 converges on: hero image, quick
start in the first 200 words, a diagram GitHub renders natively (Mermaid), four working badges, and
contributor material moved out to `CONTRIBUTING.md`.

The logo is a blue triptych of three receding panes on white, wordmark below. Its blues
(`#0a84ff`-ish gradient to `#1e3a8a`) set the accent palette.

## Goals / Non-Goals

**Goals:**

- A visitor identifies their door in the first screen, on desktop and on a phone.
- Each door ends in an action: an install command and a link to the page that continues it.
- The landing works in the template's light and dark themes.
- The page is a static build — no JavaScript beyond the template's own and a theme default.

**Non-Goals:**

- Moving the site to `stratara.tech`. That is a CNAME and a redirect after the owner is satisfied
  with the page; the page itself is domain-agnostic.
- New runnable examples. The door code blocks are the post-#39 hello-mediator, the existing
  event-sourced sample's shapes, and the encryption sample's attribute; the Examples repository
  replaces them later.
- A terminal GIF and a social-preview PNG. Both are follow-ups: the first needs a recording tool,
  the second an upload in the repository settings.

## Decisions

### The landing is HTML inside `index.md`, styled by the project template, not a forked template

Raw HTML with Bootstrap 5 grid and utility classes, plus a scoped stylesheet (`.st-*` classes) in
`docs/templates/stratara/public/main.css`. This keeps the modern template's header, search, theme
toggle and footer, upgrades with DocFX, and stays diffable. A forked `_master.tmpl` would give
pixel control at the price of owning the template.

*Rejected: a separate static homepage outside DocFX.* Two sites to keep consistent, two deploys,
and the docs navigation would not be one click away from the marketing page.

### Three doors, in this order: Mediator, Event sourcing, Multi-tenant SaaS

Left to right by adoption weight, not by differentiation. The mediator door is the smallest
promise and the one most visitors can act on in a minute; the event-sourcing door is where the
framework's own examples and articles will point; the SaaS door carries the arguments that today
open the README. Each card has the same skeleton — headline, one sentence, install line, code,
"you do not need", link — so the eye compares rather than reads.

### The growth diagram is one SVG with `currentColor` and CSS variables

Three stages side by side: *mediator alone* (one box), *plus event store* (box + PostgreSQL), *plus
workers and bus* (API host, bus, workers, store, read models). Drawn by hand in SVG with strokes in
`currentColor` and fills from the accent palette so it reads in both themes. The README carries the
same picture as Mermaid because GitHub does not apply site CSS to an SVG.

### Honest comparison, on facts verified this week

License, scope and approach per project, dated. MediatR: RPL-1.5 or commercial since 13, Community
edition under 5 M USD revenue. MassTransit v9: commercial since Q1 2026, v8 Apache 2.0 supported to
end 2026. Marten and Wolverine: MIT, open core, commercial support. EventStoreDB/KurrentDB: a
server product with its own license. Stratara: MIT. The table states what each *is* and links to
each project's own license page; it makes no performance claim about any of them.

### The package table leaves the README

Twenty-five rows are reference, not persuasion. They move to `docs/overview/packages.md` with the
tier rule, and both the README and the landing link there.

## Risks / Trade-offs

- [The landing HTML breaks the documentation snippet tests] → The tests extract fenced code blocks
  from Markdown; the door code is in fenced C# blocks, so it is compiled like every other snippet.
  Anything not meant to compile carries the existing `stratara-snippet-ignore` marker.
- [`--warningsAsErrors` rejects something in the HTML] → DocFX warns on broken links and missing
  files, not on HTML; the local build is run before the PR.
- [Dark theme renders the SVG unreadably] → strokes in `currentColor`, fills with enough contrast
  on both backgrounds; checked in both themes in the local preview.
- [The comparison ages] → each row carries a "verified 2026-09" note and links to the source of
  truth for that project.
