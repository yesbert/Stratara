# Design — Serve the documentation site from our own server

## Context

See proposal.md → *Why*. What follows is only the state a reader needs to act, established by
inspection on 2026-09-03 rather than assumed.

**The server.** Plesk, root reachable as `ssh stratara-server`. The vhost is
`/var/www/vhosts/stratara.tech`, its document root `httpdocs/`, owned by the system user
`stratara.tech_d4hk78khxi`. Today `httpdocs/` holds a 455-byte Vite placeholder from 2025-08-24 and
a `.well-known/` directory.

**Plesk serves through Apache, not nginx alone.** `/var/www/vhosts/system/loomweaver.dev/conf/nginx.conf`
proxies `location /` to `https://127.0.0.1:7081`. That is why `.htaccess` works, and why LoomWeaver
uses it for cache headers rather than server configuration it would have to re-apply after every
Plesk change.

**The blocker nobody would guess.** The system user's login shell is `/bin/false`:

```
loomweaver.dev_1kv2wmu3zbc   shell=/bin/bash    ← rsync deploys here today
stratara.tech_d4hk78khxi     shell=/bin/false   ← cannot log in at all
hiveweaver.dev_unmgvri06j    shell=/bin/false
```

rsync-over-SSH needs a shell. Until the subscription's SSH access is changed in Plesk, the deploy
key is irrelevant — authentication would succeed and the session would end immediately.

**The proven pattern.** `loomweaver.dev`'s `.github/workflows/deploy.yml` in the private LoomWeaver
repository: `actions/checkout`, build, write the deploy key from a base64 secret, `rsync -az --delete`
to `${DEPLOY_USER}@${DEPLOY_HOST}:httpdocs/`, then curl the live domain and grep for content that
must be there. It runs on push to `main` filtered by path, never on a pull request — a fork's run
gets no secrets, which is what makes a public repository safe to run it in. Its key lives at
`/var/www/vhosts/loomweaver.dev/.ssh/authorized_keys`, in the vhost home, not in `httpdocs`.

**Where the site is right now.** GitHub Pages serves `docs.stratara.tech`; the apex resolves to the
Plesk server. Mail, `sonar.stratara.tech`, `MX`, `SPF`, `DMARC` and `DKIM` are all intact after
today's rollback and must stay that way.

## Goals / Non-Goals

**Goals**

- `stratara.tech` serves the documentation, deployed from `main` without a human copying files.
- No window in which neither host answers. The cutover is ordered so that a working host always
  exists.
- `docs.stratara.tech` keeps resolving, permanently, because published packages link to it.
- The deploy cannot report success while the site is broken.

**Non-Goals**

- Deploying anything but the documentation site. No demo application, no API.
- Changing how DocFX builds. The same `docfx build --warningsAsErrors` produces the same `_site`.
- Server hardening, Plesk upgrades, or touching the other vhosts.
- Keeping GitHub Pages as a warm standby. Two publishers of the same content is how the two hosts
  drifted apart today.

## Decisions

### 1. rsync to the vhost, not a Plesk git-deploy or an FTP upload

Plesk can pull from git itself, and it can take an FTP upload. Both were rejected: the build needs
the .NET SDK and DocFX, which the server does not have and should not gain, and an FTP upload has no
identity to revoke. rsync over SSH with a dedicated key matches what already works one vhost over,
which means one pattern to understand rather than two.

*Evidence: `loomweaver.dev`'s deploy workflow, running since 2025-08.*

### 2. `--delete`, but never against `.well-known`

`rsync -az --delete` is what makes the deployed tree equal the built tree; without it, a page renamed
in `docs/` lingers forever. It would also remove `httpdocs/.well-known/`, which Plesk uses for ACME
challenges.

Plesk's nginx routes `/.well-known/acme-challenge/` to `/var/www/vhosts/default/htdocs`, so the
directory in `httpdocs` is not actually needed — which is why LoomWeaver has never noticed. Relying
on that is a bet on a Plesk implementation detail surviving an upgrade, and losing the bet means
certificate renewal fails silently until the certificate expires. `--exclude='.well-known'` costs
nothing and removes the bet.

### 3. `docs.stratara.tech` becomes a 301, served by the same server

The alternative — leaving GitHub Pages publishing that host forever purely as a redirect — keeps a
second publisher of the same content alive, which is exactly the configuration that produced today's
split. Instead the subdomain moves to the Plesk server and is redirected there.

**This is the one step with an ordering constraint that bites.** The moment its DNS leaves
`yesbert.github.io`, GitHub Pages stops being reachable for it, so the redirect must already work on
the server before the record moves.

### 4. The environment gate stays, and it is not ceremony

`environment: production` with a required reviewer means the deploy waits for a human. The workflow
holds a key that can write the document root of a public site. A dependency compromised in the
`actions/*` supply chain, or a bad merge, otherwise ships unattended. This mirrors both LoomWeaver
and this repository's own `nuget-org` gate, and it is the same reasoning: the action is
outward-facing and not cheaply reversible.

### 5. The post-deploy check asks the live domain

Copying files successfully is not evidence that a site works. The check fetches
`https://stratara.tech/`, greps for content that must be present, fetches a deep page and the
redirect from the old host, and fails the run if any of them is wrong. A deploy that reports success
over a broken site is worse than one that fails, because nobody looks.

### 6. Two facts about GitHub Pages that are not discoverable from its documentation

Both were established today by direct observation, and both cost hours.

**The `CNAME` file and the repository setting are two different switches, and they do different
things.** With the Actions-based deployment (`build_type: workflow`), the `CNAME` file in the
uploaded artifact does **not** set the repository's custom domain — that was the behaviour of the
old branch publishing. Setting `docs/CNAME` to `stratara.tech` and deploying left the setting on
`docs.stratara.tech`; the domain had to be set through
`gh api -X PUT repos/yesbert/Stratara/pages -f cname=…`.

But the same file **does** decide which hostname Pages answers for. Rolling the setting back to
`docs.stratara.tech` was not enough: with `stratara.tech` still inside the deployed artifact,
`docs.stratara.tech` kept returning *There isn't a GitHub Pages site here* until the artifact was
rebuilt. Both switches have to agree, and rolling back means doing both.

**A `CNAME` on an apex shadows every other record type of that name.** While the apex carried
`CNAME → yesbert.github.io`, queries for `MX`, `TXT` and `NS` at `stratara.tech` all returned that
CNAME. Mail to `info@stratara.tech` — the address in the imprint — could not be delivered, because
no `MX` existed and an MTA falls back to the `A` record, which was GitHub Pages. The records were
never deleted; removing the CNAME brought `MX 10 mail.crosslabs.eu.`, the SPF and the
`google-site-verification` token back unchanged.

*Evidence: `dig` against `ns1.antagus.de` and public resolvers, and the GitHub Pages 404 body, all
on 2026-09-03. The consequence for this change is decision 3's ordering constraint and task 6's
verification.*

## Risks / Trade-offs

**Uptime becomes ours.** GitHub Pages has a CDN and an operations team; the Plesk server has
neither. → Accepted deliberately: the apex has to point at that server regardless, so the site would
depend on it either way. The `.htaccess` cache headers reduce origin hits.

**A broken deploy replaces a working site.** `--delete` means a build that produces a wrong tree
publishes a wrong tree. → The post-deploy check fails the run, and rollback is re-running the
workflow from the last good commit — the source of truth is `main`, not the server.

**The deploy key can write a public document root.** → Its own key, one vhost, no root, guarded by
the environment reviewer; revoking it is one line in `authorized_keys`. Never in a pull-request
trigger, so a fork cannot reach it.

**The redirect is a single point of failure for published links.** The nuget.org READMEs for 4.0.3
cannot be edited. → Task 6 verifies the redirect against the live host, and the check runs on every
deploy thereafter, so a regression fails a run rather than silently rotting.

**Certificate for the apex.** Plesk's Let's Encrypt must cover `stratara.tech` and, after the move,
`docs.stratara.tech`. → Verified explicitly rather than assumed; renewal is Plesk's job and the
`.well-known` exclusion protects the challenge path.

## Migration Plan

Ordered so that a working host always exists. Nothing before step 5 changes what a visitor sees.

1. **Enable SSH** for the `stratara.tech` subscription in Plesk (`/bin/bash`, as `loomweaver.dev`
   has). Nothing else can start.
2. **Install the deploy key** and register the secrets. Prove it with a dry-run rsync.
3. **Add the workflow**, deploy, and check `stratara.tech`. `docs.stratara.tech` is still served by
   Pages throughout — there is a working host the whole time.
4. **Configure the redirect** for `docs.stratara.tech` on the server, and verify it by `Host` header
   before any DNS moves.
5. **Move the DNS** for `docs.stratara.tech`. Both hosts now answer from our server.
6. **Retire Pages**: delete `docs.yml` and `docs/CNAME`, unpublish the repository's Pages site.
7. **Move the repository's references** and rewrite the privacy policy's hosting section.
8. **Umami**, which counts nothing until its domain matches.

**Rollback.** Before step 5, revert the DNS thought and nothing is lost — Pages still serves the
documentation. After step 6, rollback means restoring `docs.yml` and `docs/CNAME` **and** the Pages
repository setting; decision 6 explains why doing only one of the two leaves a 404.
