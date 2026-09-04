# Serve the documentation site from our own server

> **Status:** approved — 2026-09-04, owner

## Why

The documentation site is to become the public homepage at `stratara.tech`, and on 2026-09-03 that
move was attempted by pointing the apex at GitHub Pages. It was rolled back within the hour: too
much else already answers on that apex, and it has to keep pointing at the Plesk server. The
domain cannot move to the site, so the site has to move to the domain.

That is not a consolation prize. `loomweaver.dev` is already deployed to the same server from
GitHub Actions over rsync, so the pattern exists, is proven, and is one this project can copy rather
than invent. It also improves the story the site tells about itself: the privacy policy currently
has to name GitHub Pages in the United States as the host of a site whose subject matter is
tamper-evidence and GDPR erasure. Afterwards it names a German server whose logs belong to the
controller.

No framework behaviour changes. No published package changes. Nothing a consumer of a NuGet package
can observe changes.

## What Changes

- The DocFX site is deployed by rsync to the `stratara.tech` Plesk vhost instead of to GitHub Pages,
  from a new workflow that mirrors `loomweaver.dev`'s: an ed25519 deploy key, an `environment` with
  a required reviewer so neither an accident nor a compromised dependency ships unattended, and a
  post-deploy check that asks the live domain rather than trusting the upload.
- `stratara.tech` serves the documentation. The apex A record does not move.
- `docs.stratara.tech` keeps working, as a **301 to the apex**. This is not optional politeness: the
  package READMEs already published to nuget.org under 4.0.3 link to that host, and those cannot be
  edited after the fact.
- `docs.yml`, `docs/CNAME` and the repository's GitHub Pages publication are retired, in that order
  and only after the new host is verified.
- The repository's references move to `stratara.tech` again — the same 33 that were moved and
  reverted today, plus the README docs badge.
- The privacy policy's hosting section is rewritten: the controller's own server, his own logs, no
  transfer to the United States, and no GitHub Pages.
- Cache headers for the static site, as `.htaccess` — which works because Plesk's nginx proxies to
  Apache rather than serving the directory itself.
- The site domain is corrected in Umami Cloud. Until that is done, nothing is counted.

**Not in scope.** Moving `sonar.stratara.tech`, anything about mail, and the Plesk vhost's other
settings. Mail was collateral damage of today's rollback and is already repaired.

## Capabilities

### New Capabilities

None. This is an infrastructure and documentation arc; `skip_specs: true` is set in
`.openspec.yaml`. Specs describe what a consumer of a published package observes, and where a
documentation site is hosted is not that.

### Modified Capabilities

None.

## Impact

**Retired by this change:**

- `.github/workflows/docs.yml` — the DocFX → GitHub Pages deployment, superseded by the new rsync
  workflow. Delete only after the new host serves and the redirect is in place.
- `docs/CNAME` — meaningless once Pages no longer publishes this repository.
- The repository's GitHub Pages publication itself (repository setting), which is the last step.

**Changed:**

- `docs/legal/privacy.md` — the *Hosting and server log files* section, and the description in the
  front matter.
- `README.md`, `llms.txt`, `SUPPORT.md`, `CONTRIBUTING.md`, `.github/ISSUE_TEMPLATE/config.yml`,
  `.github/ISSUE_TEMPLATE/question.md`, `samples/Stratara.Sample.TamperProof/README.md`,
  `samples/Stratara.Sample.Encryption/README.md` — host references, and the README docs badge whose
  label has to name the host it links to.
- `docs/docfx.json` — `_appBaseUrl`.

**Outside the repository**, and therefore the part that cannot be done by a pull request alone:

- The Plesk subscription for `stratara.tech`: SSH must be enabled for its system user, which today
  has `/bin/false` as its shell where `loomweaver.dev`'s has `/bin/bash`.
- A deploy key pair, its public half in that user's `~/.ssh/authorized_keys`, its private half in
  the repository's secrets.
- DNS: `docs.stratara.tech` moves from its `yesbert.github.io` CNAME to the server, once the
  redirect is configured there.
- Umami Cloud: the website's domain.

**Two traps that cost hours today and must not be rediscovered.** Both are recorded in design.md
with the evidence: with the Actions-based Pages deployment the `CNAME` file in the artifact does
*not* set the custom domain, but it *does* decide which hostname Pages answers for; and a `CNAME` on
an apex shadows the `MX`, `TXT` and `NS` records of the same domain without deleting them, which
silently broke mail to `info@stratara.tech` until it was removed.
