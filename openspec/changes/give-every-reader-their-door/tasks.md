## 1. Landing page

- [x] 1.1 `docs/templates/stratara/public/main.css`: landing styles (`.st-hero`, `.st-door`, `.st-grid`,
      `.st-compare`, spacing, both themes) and `docs/templates/stratara/public/main.js` with the
      icon links (GitHub, NuGet). Existing `#logo` rule stays.
- [x] 1.2 `docs/assets/how-it-grows.svg`: the three-stage growth diagram, `currentColor` strokes.
- [x] 1.3 `docs/index.md`: hero, three doors with install + code + "you do not need" + link, growth
      diagram, feature grid (eight items), performance numbers, comparison table, closing CTA.
      Door code blocks are fenced C# so the documentation snippet tests compile them.
- [x] 1.4 `docs/docfx.json`: `_appTitle` to the positioning sentence; `_description` metadata.
- [x] 1.5 `docs/overview/packages.md` (new) with the 25-package table and tier rule;
      `docs/overview/toc.yml` gains the entry.
- [x] 1.6 Local `docfx build docs/docfx.json --warningsAsErrors` green; landing checked at desktop
      width in the local preview (screenshots kept outside the repository). Phone width could not be
      judged in headless Chrome — the unchanged live site overflows there too, a known headless
      artefact of the template's `100vw` body width — and the dark theme relies on the CSS variables;
      both to be eyeballed on a real device after deploy.

## 2. README

- [x] 2.1 `README.md`: logo + positioning sentence + badges; three doors with code; Mermaid growth
      diagram; "Why Stratara" condensed to five bullets; performance section kept; documentation,
      install, license, contributing kept; package table replaced by a link to
      `docs/overview/packages.md`.
- [x] 2.2 `tests/Stratara.Documentation.Tests` green (README and docs snippets compile).

## 3. Gate and follow-ups

- [ ] 3.1 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` clean.
- [ ] 3.2 Record as follow-ups, not done here: terminal recording of the TamperProof sample; GitHub
      social-preview image; CNAME move to `stratara.tech` with a redirect from `docs.stratara.tech`.
