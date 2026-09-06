Ordered so that each group leaves the site buildable and better than it found it. Groups 1 and 2 are
the two configuration switches and need nothing else; group 3 is the fork everything after it rests
on; groups 4 to 7 fill it with content.

Every item names what proves it. Where that is a command, it is one that can be run before the pull
request exists — a green `docfx build` and a grep over `docs/_site` are the instrument for most of
this arc, because the artefact under test is generated HTML.

## 1. The two configuration switches

- [x] 1.1 Add `"_lang": "en"` to `globalMetadata` in `docs/docfx.json`. Proof:
      `grep -L 'html lang="en"' docs/_site/**/*.html` lists nothing after a build.
- [x] 1.2 Add a `build.sitemap` block to `docs/docfx.json` with `baseUrl` `https://stratara.tech`,
      `changefreq` `weekly` and the default `priority`. Proof: `docs/_site/sitemap.xml` exists and
      `grep -c '<url>' docs/_site/sitemap.xml` reports 517 or more, every `<loc>` absolute.
- [x] 1.3 Confirm the build is still clean: `docfx build docs/docfx.json --warningsAsErrors` reports
      0 warnings and 0 errors. Locally this needs
      `RestoreSources=https://api.nuget.org/v3/index.json` on the metadata step — a second source in
      the user's NuGet.Config makes NU1507 an error otherwise.

## 2. robots.txt

- [x] 2.1 Write `docs/robots.txt`: `User-agent: *`, `Allow: /`, and
      `Sitemap: https://stratara.tech/sitemap.xml`. It must not contain `Disallow: /` — design.md →
      *Migration Plan* names that as the one-way door.
- [x] 2.2 Add `robots.txt` to the `build.resource` list in `docs/docfx.json`, beside `assets/**`.
      Proof: `docs/_site/robots.txt` exists after a local build, without a workflow step.

## 3. The forked layout

- [ ] 3.1 Copy `layout/_master.tmpl` from DocFX 2.78.5's `modern` template to
      `docs/templates/stratara/layout/_master.tmpl`, unmodified, and commit that copy on its own so
      the next commit's diff shows exactly what we added.
- [ ] 3.2 Give the file a header comment naming the upstream version (`2.78.5`), the upstream path,
      and the rule that every added block is marked. Proof: task 8.3's test reads this comment.
- [ ] 3.3 Replace the two independent description sections (upstream lines 16–17) with one choice:
      `{{description}}` when the page has one, `{{_description}}` otherwise. Proof:
      `grep -c 'name="description"' docs/_site/legal/privacy.html` reports 1, not 2.
- [ ] 3.4 Add `<link rel="canonical" href="{{_appBaseUrl}}/{{_path}}">`. Proof: the canonical on
      `docs/_site/guides/write-a-saga.html` reads `https://stratara.tech/guides/write-a-saga.html`,
      and on `docs/_site/index.html` it reads `https://stratara.tech/index.html`.
- [ ] 3.5 Add Open Graph (`og:type`, `og:site_name`, `og:title`, `og:description`, `og:url`,
      `og:image`) and Twitter (`twitter:card` as `summary_large_image`, `twitter:title`,
      `twitter:description`, `twitter:image`) tags, all from the same values the title, description
      and canonical already use. Proof: `grep -c 'property="og:' docs/_site/index.html` reports 6.
- [ ] 3.6 Add the skip link as the first element inside `<body>`, pointing at `#main`, and give
      `<main>` that id. Proof: the first `<a>` in `docs/_site/index.html` targets `#main`, and
      `id="main"` appears once.
- [ ] 3.7 Give the three unlabelled `<nav>` landmarks an `aria-label` — the navbar, the table of
      contents and the affix ("Main", "Table of contents", "On this page"). Proof: every `<nav>` in
      `docs/_site/concepts/why-event-sourcing.html` carries either `aria-label` or `aria-labelledby`.
- [ ] 3.8 Add the JSON-LD block: `SoftwareApplication` when `_layout` is `landing`, `TechArticle`
      otherwise, per design.md → decision 6. Author `Norbert Rosenwinkel`, licence
      `https://opensource.org/license/mit`, `operatingSystem` `.NET 10`. Proof: both
      `docs/_site/index.html` and `docs/_site/guides/write-a-saga.html` contain exactly one
      `application/ld+json` script and both parse as JSON.
- [ ] 3.9 Pin the tool in `.github/workflows/deploy-site.yml`:
      `dotnet tool install -g docfx --version 2.78.5`. Proof: the workflow file names the same
      version as the header comment from 3.2.

## 4. The cover image

- [ ] 4.1 Write `docs/assets/og-cover.svg` at 1200×630 — wordmark, brand blue `#0673cb` taken from
      `docs/templates/stratara/public/main.css`, and the line *CQRS and Event Sourcing for .NET*.
- [ ] 4.2 Write `scripts/refresh-og-cover.sh` in the shape of `scripts/refresh-badges.sh`:
      `set -euo pipefail`, `ROOT_DIR` from `BASH_SOURCE`, a header comment saying why the PNG is
      committed rather than built, and a check for `rsvg-convert` that fails with
      `brew install librsvg` when it is absent. Proof: running it produces
      `docs/assets/og-cover.png`, and `sips -g pixelWidth -g pixelHeight` on it reports 1200×630.
- [ ] 4.3 Point `og:image` and `twitter:image` at the absolute URL
      `https://stratara.tech/assets/og-cover.png` — a relative path is not resolved by most Open
      Graph consumers. Proof: the tag in `docs/_site/index.html` is absolute.
- [ ] 4.4 Confirm the cover does not reintroduce a third-party request:
      `NoDocumentationPage_LoadsAnImageFromAnotherHost` in
      `tests/Stratara.Documentation.Tests/LandingBadgeTests.cs` still passes.

## 5. The 51 descriptions

Written by hand, one sentence each, naming what the page answers rather than repeating its title.
Front matter gains `title` and `description`; nothing else about the page changes.

- [ ] 5.1 `docs/index.md` — add `description`, and shorten `title` so it no longer repeats
      `_appTitle` almost verbatim (today the landing `<title>` says the same sentence twice).
- [ ] 5.2 `docs/overview/` — 5 pages: `index`, `what-is-stratara`, `architecture-at-a-glance`,
      `packages`, `glossary`.
- [ ] 5.3 `docs/concepts/` — 5 pages: `index`, `why-event-sourcing`, `tamper-evident-streams`,
      `tenant-aware-encryption`, `performance-and-scaling`.
- [ ] 5.4 `docs/getting-started/` — 4 pages: `index`, `prerequisites`, `first-stratara-app`,
      `di-composition`.
- [ ] 5.5 `docs/guides/` — 21 pages, the whole directory.
- [ ] 5.6 `docs/samples/` — 11 pages: `index`, `01` through `08`, `hero-encryption`,
      `hero-tamper-proof`.
- [ ] 5.7 `docs/reference/` — 4 pages: `index`, `di-extensions-cheatsheet`, `routing-conventions`,
      `log-events-schema`.
- [ ] 5.8 Confirm the sweep is complete: no `.md` under `docs/` outside `reference/api/` lacks front
      matter with a `description`. Proof: task 8.1's test.

## 6. The landing page's own markup

- [ ] 6.1 Repair the heading order: the three `<h3>` in the "New to the terms?" section follow an
      `<h1>` with no `<h2>` between, and the eight `<h4>` under "What is in the box" follow an
      `<h2>`. Proof: the heading sequence in `docs/_site/index.html` never skips a level.
- [ ] 6.2 Add `aria-hidden="true"` to the three decorative Bootstrap icons in the door cards
      (`bi-lightning-charge`, `bi-layers`, `bi-shield-lock`). The navbar's search and menu icons are
      inside labelled controls and are left alone.
- [ ] 6.3 Add `scope="col"` to the header cells of the "Where it sits" comparison table.

## 7. Style for the skip link

- [ ] 7.1 Add the skip-link rule to `docs/templates/stratara/public/main.css`: off-screen until
      focused, then visible against the body background with the brand blue. Proof: the rule uses
      `:focus`, not `display: none`, which would take it out of the tab order and defeat the point.

## 8. Lock it down

- [ ] 8.1 Add a test in `tests/Stratara.Documentation.Tests/` asserting every hand-written page under
      `docs/` (excluding `reference/api/`) carries front matter with a non-empty `description`. Use
      the existing `DocumentationFiles` enumerator, which already excludes generated output.
- [ ] 8.2 Add a test asserting no built page emits two `<meta name="description">` tags. It needs a
      built site, so follow whatever `LandingBadgeTests` does about that rather than inventing a
      second mechanism.
- [ ] 8.3 Add a test reading the DocFX version from the forked layout's header comment and from the
      `--version` flag in `.github/workflows/deploy-site.yml`, failing when they disagree
      (design.md → decision 2).
- [ ] 8.4 Add a test asserting `docs/robots.txt` names the sitemap and contains no `Disallow: /`.

## 9. Close it out

- [ ] 9.1 `./scripts/local-gauntlet.sh` green, and `docfx build docs/docfx.json --warningsAsErrors`
      with 0 warnings.
- [ ] 9.2 Check the built site by hand for the things a test cannot: tab from the top of
      `index.html` and confirm the skip link appears and works; open the page in dark mode; confirm
      the cover image renders as intended at 1200×630.
- [ ] 9.3 Open the pull request through the `/pr` skill. Merge once CI is green and every review
      thread is resolved — the owner agreed on 2026-09-06 that this arc may go to `main` without a
      second stop, while the change's approval and the deploy's approval stay his.
- [ ] 9.4 After the merge, approve the `deploy-site.yml` run — that gate is the owner's — and verify
      against the live host: `https://stratara.tech/sitemap.xml` and `/robots.txt` return 200, the
      canonical on a deep page names the apex, and `curl -s https://stratara.tech/ | grep 'og:image'`
      shows the absolute cover URL.
- [ ] 9.5 Record in `.claude/roadmap/STATE.md` that the site now carries a sitemap, canonical URLs,
      Open Graph and structured data, that `_master.tmpl` is forked from DocFX 2.78.5, and that the
      workflow pins that version — so the next session that bumps DocFX knows a file is waiting.
- [ ] 9.6 Set the generated cover as the repository's social preview image in the GitHub settings.
      **owner** — it is a repository setting, not a file. This closes the "GitHub-Social-Preview-Bild"
      follow-up that STATE.md has carried since 2026-09-05.
