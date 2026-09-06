# Design — Make every page findable and readable

## Context

See proposal.md → *Why* for the motivation. What follows is only the state of the build that shapes
the approach.

**How a page is produced today.** DocFX 2.78.5 renders every page by filling one Mustache template,
`layout/_master.tmpl`, which ships inside the tool. `docs/templates/stratara/` contributes only
`public/main.css` and `public/main.js`; both are *added* to the page, neither touches its structure.
Anything that belongs in `<head>` or at the top of `<body>` — a canonical link, an Open Graph tag, a
skip link — is a line in that template, and DocFX offers no configuration key for it.

**What is configurable, and was verified.** On 2026-09-06 a scratch template and a copy of
`docfx.json` were built to answer three questions with evidence rather than documentation:

| Question | Answer | Evidence |
|---|---|---|
| Does `_lang` reach the `<html>` element? | Yes | `_master.tmpl` line 6 is `<html {{#_lang}}lang="{{_lang}}"{{/_lang}}>`; with `"_lang": "en"` in `globalMetadata` every built page carried `<html lang="en">` |
| Can DocFX emit a sitemap? | Yes | A `build.sitemap` block produced `sitemap.xml` with 517 `<url>` entries, absolute against `baseUrl`, each with `lastmod` |
| Is the page's own output path available to the template? | Yes | A probe meta tag rendered `{{_path}}` as `index.html`, `guides/write-a-saga.html` and `reference/api/Stratara.Mediator.Multitenancy.html` — landing, hand-written page and generated API page alike |

**The defect that forces the fork.** `_master.tmpl` lines 16–17 are two independent sections:

```mustache
{{#_description}}<meta name="description" content="{{_description}}">{{/_description}}
{{#description}}<meta name="description" content="{{description}}">{{/description}}
```

Neither excludes the other. A page with front-matter `description` therefore serves **two**
description tags — confirmed on the built `legal/privacy.html`, which carries the global sentence and
its own. Adding descriptions to the other 51 pages without touching the template would spread the
defect from two pages to every one of them rather than fix anything.

**Where the fork sits in the build.** DocFX resolves `build.template` in order and lets a later entry
override an earlier one. `docs/docfx.json` already lists `["default", "modern", "templates/stratara"]`,
so a file at `docs/templates/stratara/layout/_master.tmpl` wins over the tool's own. The scratch build
proved this by overriding the layout from a fourth entry appended to the same array.

## Goals / Non-Goals

**Goals:**

- Every produced page — all 525, generated API reference included — carries a language, a canonical
  URL, a description that is its own, and a link preview that is not a bare URL.
- The two WCAG 2.2 Level A failures are closed in the template, once, rather than per page.
- The fork's divergence from its origin is detectable by a test rather than by noticing a regression
  on the live site.
- A local `docfx build` produces the same artefacts the deploy produces, `robots.txt` and
  `sitemap.xml` included. Nothing that only exists in CI.

**Non-Goals:**

- Rewriting or restyling any page. The audit found the prose sound; this arc is about what a machine
  reads.
- Touching the DocFX search index, `index.json`, or the client-side breadcrumb.
- Forking any template file other than `_master.tmpl`. If a second file turns out to be needed, that
  is a decision to make then, with this document's risk section in front of us.

## Decisions

### 1. Fork `_master.tmpl` rather than inject after the build

Five findings — canonical, Open Graph and Twitter, the description precedence, the skip link and the
`aria-label` on three `<nav>` landmarks — have no configuration key. Three routes were on the table
and the owner chose the first on 2026-09-06:

- **Fork the layout** (chosen). The added lines are part of the build, so `docfx build` on a laptop
  produces exactly what the deploy publishes, and `Stratara.Documentation.Tests` can assert against
  it the way it already asserts against the badges.
- **Configuration only** (rejected). It delivers `lang` and `sitemap` and stops. The skip link stays
  missing, so one of the two Level A failures survives; and the 51 descriptions stay impossible,
  because the duplicate-tag defect would make the site worse rather than better.
- **A post-build injection step in `deploy-site.yml`** (rejected). It avoids the fork, but the result
  would exist only inside GitHub Actions: a local build would no longer resemble the published site,
  and no test could reach the injected markup. In a repository that pins its documentation with 502
  tests, that is the wrong kind of clever.

### 2. Pin `docfx` to `2.78.5`, and make the fork say where it came from

`deploy-site.yml` runs `dotnet tool install -g docfx`, which installs whatever is newest that
morning. That was tolerable while the template was DocFX's own. With a copy of one of its files in
our tree it is not: a DocFX release that improves the layout would leave our copy behind without a
single failing check.

Three things together, none sufficient alone:

- the workflow pins `--version 2.78.5`;
- the forked file opens with a comment naming the version and the upstream path it was taken from,
  and marking the lines we added;
- a test reads that comment and the version the workflow pins and fails when they disagree.

The test cannot detect that upstream 2.79.0 changed the file — nothing short of vendoring the
original could — but it makes the upgrade a deliberate act. Whoever bumps the pin is told, by a red
test, that a file is waiting for them.

### 3. Canonical from `_appBaseUrl` and `_path`

`_appBaseUrl` is already `https://stratara.tech`. `{{_path}}` resolves to the site-relative output
path on every page type (evidence in *Context*). The canonical is the two joined. This matters
because the site answers on more than one URL: `docs.stratara.tech` 301s to the apex and will do so
permanently, since published package READMEs link to it, and `/samples/` and `/samples/index.html`
are the same page.

### 4. Page description wins, global is the fallback

The forked layout replaces the two independent sections with one choice: emit `{{description}}` when
the page has one, `{{_description}}` otherwise. That is what the two sections were plainly meant to
express. The global sentence stays in `docfx.json` as the fallback for the generated API reference,
which has no front matter and where a per-page sentence would have to be invented rather than
written.

### 5. `robots.txt` as a DocFX resource, not a workflow step

`llms.txt` is copied by the workflow because it lives at the repository root and is not part of the
documentation tree. `robots.txt` has no such constraint: put it in `docs/` and add it to the
`resource` list, and it appears at the site root in every build, including a local one. Goal four
says the deploy must not be the only place an artefact exists.

### 6. `SoftwareApplication` on the landing, `TechArticle` elsewhere, no `BreadcrumbList`

The landing page describes a product, and `SoftwareApplication` is the type that carries a licence,
an operating requirement and an author. Every other page is documentation, and `TechArticle` can be
built entirely from values the template already holds — `{{title}}`, `{{description}}`, the canonical
URL. `BreadcrumbList` was considered and dropped: DocFX renders `<nav id="breadcrumb"></nav>` empty
and fills it in the browser from `toc.json`, so the ancestor titles it would need do not exist at
build time. A JSON-LD block assembled by JavaScript is worth less than none, because the crawlers
that most need it are the ones least likely to run the script.

The author is `Norbert Rosenwinkel`, which the imprint already publishes under § 5 DDG. Nothing new
becomes public.

### 7. The cover image has an SVG master and a script, and the script needs one tool

`og:image` must be a raster format; no major consumer of Open Graph renders SVG. But a PNG committed
without a source is an artefact nobody can edit, so the master is `docs/assets/og-cover.svg` — 1200×630,
the wordmark, the brand blue `#0673cb` from `main.css`, and the line *CQRS and Event Sourcing for
.NET* — and `scripts/refresh-og-cover.sh` renders it, in the shape of `scripts/refresh-badges.sh`:
`set -euo pipefail`, `ROOT_DIR` from `BASH_SOURCE`, a header comment saying why the file is committed
rather than generated at build time.

Rendering needs `rsvg-convert`, which is not installed on the owner's machine today; the script
checks for it and fails with `brew install librsvg` rather than producing a broken file. That is a
real prerequisite and it is stated here rather than discovered during apply.

The alternative that needs nothing — `sips -Z` the existing `logo.png` and `--padToHeightWidth 630
1200 --padColor ffffff` — was considered and rejected, but is worth recording because it works: it
produces a correct, on-brand cover with no tagline, because `sips` cannot set type. If the librsvg
prerequisite is refused, that is the fallback, and the cover loses one line of text.

## Risks / Trade-offs

**A DocFX upgrade silently leaves the fork behind** → The pin, the provenance comment and the test
that ties them together (decision 2). The residual risk is real and accepted: nobody is told what
changed upstream, only that something must be looked at.

**51 hand-written descriptions rot as pages change** → A test asserts that every hand-written page
*has* one and that no page emits two; nothing can assert that a sentence still describes its page.
This is the same bargain `llms.txt` already makes, and the same one `refresh-badges.sh` makes with
the version badge.

**`sitemap.xml` will advertise 517 URLs, most of them generated API reference** → That is what a
sitemap is for, and `lastmod` comes from the build, so a crawler is not told that an unchanged page
changed. If the reference pages later prove to dilute the site in search results, the answer is a
`priority` split between hand-written and generated content, not a smaller sitemap. Not done now:
there is no evidence yet, and guessing at crawl budget is how sitemaps become superstition.

**The skip link is the first focusable element and will be seen by anyone tabbing** → That is the
point. It is visually hidden until focused, the standard pattern, and it lands on `<main>`, which
already carries no `id` and will get one.

**A second deploy approval** → Every merge to `main` that touches `docs/**` puts a run in front of
the owner. This arc is one change, so it costs one approval rather than one per finding.

## Migration Plan

No migration. The site is static, the deploy replaces the tree with `rsync --delete`, and every
change here is additive except the description tag, which goes from two to one.

**Rollback** is `git revert` and a redeploy. There is no state anywhere — no database, no cache the
project controls, no consumer holding an artefact. A crawler that fetched a sitemap that then
disappears re-crawls; that is ordinary.

**The one-way door is `robots.txt`**, and only if it were written wrongly. A `Disallow: /` shipped by
accident would deindex the site, and getting back in takes weeks. The file is four lines and its
content is checked by a test before it can reach the deploy.

## Open Questions

None that can wait. The three that would have changed the approach — fork or not, image or not, how
far to go without the owner — were answered on 2026-09-06 and are recorded in decisions 1, 7 and in
tasks.md's closing group.
