Ordered per design.md → *Migration Plan*. The guarantee is not that nothing changes — group 2
replaces the placeholder on the apex with the documentation — but that **a working host exists at
every point**: `docs.stratara.tech` stays served by GitHub Pages until group 3 moves it, and Pages
stays published as a rollback path until group 4.

Tasks marked **owner** cannot be done from a pull request: they are Plesk, DNS, GitHub secrets or
Umami. Each one names how to prove it worked.

## 1. Open the door on the server

- [ ] 1.1 **owner** In Plesk, enable SSH access for the `stratara.tech` subscription's system user
      `stratara.tech_d4hk78khxi` (shell `/bin/bash`, as `loomweaver.dev_1kv2wmu3zbc` already has).
      Proof: `ssh stratara-server 'grep stratara.tech_d4hk78khxi /etc/passwd'` no longer ends in
      `/bin/false`.
- [ ] 1.2 Generate an ed25519 deploy key pair named for this purpose. The private half must never
      be written to the repository or to a file that survives the session.
- [ ] 1.3 Install the public half in `/var/www/vhosts/stratara.tech/.ssh/authorized_keys`, owned by
      the vhost user, `700` on `.ssh` and `600` on the file — the layout
      `/var/www/vhosts/loomweaver.dev/.ssh/` already has. Proof: `ssh -i <key> <user>@<host> true`
      succeeds.
- [ ] 1.4 **owner** Register the repository secrets `DEPLOY_SSH_KEY_B64` (base64 of the private
      half), `DEPLOY_USER`, `DEPLOY_HOST`, and the variable `DEPLOY_KNOWN_HOSTS`
      (`ssh-keyscan` output for the host). Proof: `gh secret list` and `gh variable list` show all
      four.
- [ ] 1.5 Prove the path end to end before any workflow exists: `rsync -n -az` a throwaway file to
      `<user>@<host>:httpdocs/` and confirm it is listed but, being a dry run, not written.

## 2. The deploy workflow

- [x] 2.1 Add `.github/workflows/deploy-site.yml`, modelled on `loomweaver.dev`'s `deploy.yml`:
      trigger on push to `main` filtered to `docs/**`, `llms.txt`, `llms-full.txt` and the workflow
      itself, plus `workflow_dispatch`; **never** on `pull_request`, so a fork's run gets no
      secrets; `concurrency` group so two deploys cannot interleave; `permissions: contents: read`.
- [ ] 2.2 **owner** Create the `production` environment with a required reviewer, and reference it
      from the job. Proof: the first run stops and waits, exactly as `release.yml` does at
      `nuget-org`.
- [x] 2.3 Build step — the same two commands `docs.yml` runs today, so the build is unchanged by
      this arc:

      ```bash
      docfx metadata docs/docfx.json
      docfx build docs/docfx.json --warningsAsErrors
      ```

- [x] 2.4 Write `docs/_site/.htaccess` with cache headers before uploading: HTML, JSON, TXT and XML
      `no-cache`; hashed assets under `public/` `immutable, max-age=31536000`. `.htaccess` is the
      right mechanism because Plesk's nginx proxies to Apache (design.md → *Context*).
- [x] 2.5 Deploy step. The exclusion is decision 2 — without it the ACME challenge directory is
      deleted on every deploy:

      ```bash
      rsync -az --delete --exclude='.well-known' -e 'ssh -i ~/.ssh/stratara_deploy' \
        docs/_site/ "${DEPLOY_USER}@${DEPLOY_HOST}:httpdocs/"
      ```

- [x] 2.6 Post-deploy check that asks the live domain and fails the run otherwise: `https://stratara.tech/`
      contains the landing hero string; a deep page such as `/concepts/why-event-sourcing.html`
      returns 200; `/legal/imprint.html` returns 200; `/assets/badges/nuget.svg` returns 200 and is
      served from our own host.
- [ ] 2.7 Run it once by `workflow_dispatch`, approve the gate, and confirm `stratara.tech` serves
      the documentation while `docs.stratara.tech` is still served by GitHub Pages. Proof: both
      hosts return 200 and the same landing content.

## 3. Keep the old host alive

- [ ] 3.1 **owner** In Plesk, add `docs.stratara.tech` to the server (subdomain or domain alias of
      `stratara.tech`) and configure a permanent redirect to `https://stratara.tech`, preserving the
      path — a bare redirect to the root would turn every deep link in the published package
      READMEs into a homepage visit.
- [ ] 3.2 Verify the redirect **before** any DNS moves, by asking the server directly with the
      right `Host`. It must return `301` with a `Location` of the same path on the apex:

      ```bash
      curl -sI --resolve docs.stratara.tech:443:217.154.79.173 \
        https://docs.stratara.tech/concepts/why-event-sourcing.html
      ```

- [ ] 3.3 **owner** Ensure Plesk's Let's Encrypt certificate covers `docs.stratara.tech` as well as
      `stratara.tech`. Without it, step 3.4 makes every link to the old host a TLS error rather
      than a redirect.
- [ ] 3.4 **owner** Move the DNS: `docs.stratara.tech` from its `yesbert.github.io` CNAME to the
      server. Proof: `dig +short @ns1.antagus.de docs.stratara.tech` returns `217.154.79.173`, and
      `curl -sIL https://docs.stratara.tech/` lands on the apex with 200.

## 4. Retire GitHub Pages

- [ ] 4.1 Delete `.github/workflows/docs.yml` and `docs/CNAME`.
- [ ] 4.2 Update the workflow table in `.claude/CLAUDE.md` — it lists `docs.yml` as the deployment
      of this site.
- [ ] 4.3 **owner** Unpublish the repository's GitHub Pages site in the repository settings. Do this
      last: while it is published, it remains a rollback path (design.md → *Rollback*).

## 5. Move the repository's references

- [ ] 5.1 Re-apply what PR #55 did and PR #56 reverted: `stratara.tech` in `README.md`, `llms.txt`,
      `SUPPORT.md`, `CONTRIBUTING.md`, `.github/ISSUE_TEMPLATE/config.yml`,
      `.github/ISSUE_TEMPLATE/question.md`, both hero-sample READMEs, and `_appBaseUrl` in
      `docs/docfx.json`. Leave `CHANGELOG.md` and the archived
      `2026-09-03-give-every-reader-their-door` alone — dated records are not rewritten, and the
      redirect keeps their links working.
- [ ] 5.2 Set the README docs badge back to `docs-stratara.tech` so its label names the host it
      links to (the reason it was changed in #56).
- [ ] 5.3 Confirm no live reference to the old host remains: `grep -rn 'docs\.stratara\.tech'`
      returns only `CHANGELOG.md` and the archived change.

## 6. Say where the site is hosted

- [ ] 6.1 Rewrite *Hosting and server log files* in `docs/legal/privacy.md`: the controller's own
      server, his own logs, a German location, and no transfer to the United States. Remove the
      GitHub, Inc. paragraph and its two links.
- [ ] 6.2 Update the front-matter `description` in the same file, which names the host.
- [ ] 6.3 Confirm the rest of the policy is still true after the move: the *No third-party requests
      before you agree* section, the localStorage keys, and the Umami paragraph. Only the host
      changed.

## 7. Analytics

- [ ] 7.1 **owner** In Umami Cloud, set the website's domain to `stratara.tech`. Proof: a visit to
      the apex appears in the dashboard after accepting the notice. Until this is done nothing is
      counted, whatever the banner says.

## 8. Close it out

- [ ] 8.1 `./scripts/local-gauntlet.sh` green, and `docfx build docs/docfx.json --warningsAsErrors`
      with 0 warnings.
- [ ] 8.2 `NoDocumentationPage_LoadsAnImageFromAnotherHost` in
      `tests/Stratara.Documentation.Tests/LandingBadgeTests.cs` still passes — the move must not
      reintroduce a third-party request.
- [ ] 8.3 Open the pull request through the `/pr` skill, and record in `.claude/roadmap/STATE.md`
      that the site is no longer on GitHub Pages, including the two traps from design.md → decision
      6 so the next session does not rediscover them.
