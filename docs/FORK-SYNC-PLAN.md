# OmniPacker Fork Sync and Roadmap

Status: living document. Created 2026-09-20. Owner: Rustbeard86 fork
(github.com/Rustbeard86/OmniPacker). Upstream: github.com/elgreams/OmniPacker.

This is the single source of truth for how this fork relates to upstream, what we
pull in, what we build, and in what order. Update it as phases land.

---

## 1. Why this document exists

The fork was created by re-rooting (a fresh history), so git saw NO common
ancestor with upstream even though the projects are identical in content. That
made upstream's ongoing work impossible to merge cleanly. Meanwhile the original
dev shipped 61 commits (up to v1.4.0 + an audit-hardening batch) that the fork
never received, while the fork built 22 commits of unique features on a stale
base.

Decision: re-anchor onto upstream's real history, then re-port our unique
features on top. This makes every future upstream change trackable and mergeable,
and positions the fork to be the go-to build until (and if) the original dev
resumes.

---

## 2. Analysis summary (how the two lines diverged)

- Fork `main` content was identical to upstream commit `5fd7413`
  ("Kill sidecar children on exit"), differing only by the 7 vendored
  DepotDownloader sidecar binaries (which the fork gitignores).
- Upstream is 61 commits ahead of that point. It decisively rewrote the shared
  core:
  - `depot_runner.rs`: +1188 lines (fork touched +84)
  - `template_renderer.rs` +507 plus a whole named-profile template engine
    (`template_engine.js`, `template_metadata.rs`, `template_store.rs`,
    `tests/template_parity.json`)
  - `main.js`: ~3000 lines of churn
  - New subsystems the fork lacks: 5-language i18n (`src/i18n/*.js`),
    portable-aware config, security hardening (`capabilities/default.json`,
    backend compression-password store), CI gate + template parity check,
    archive splitting (`zip_runner.rs`), custom output folder
    (`output_override.rs`), shared-depot module (`shared_depots.rs`),
    app self-update check (`update_check.rs`), `steamcmd_api.rs`.
- The fork's unique value is 5 net-new modules that DO NOT exist upstream:
  - `owned_apps.rs` (716) - owned-library enumeration
  - `update_watcher.rs` (375) - Steam app-build watcher
  - `steam_news.rs` (165) - patch notes
  - `steam_store.rs` (264) - rich app metadata
  - `app_detail.rs` (233) - per-game config panel
  - plus their frontend wiring (main.js +1337, index.html +102, styles.css +460)
  - plus build tooling: `bump-version.ps1`, `rebuild-depotdownloader.ps1`

Conclusion: upstream wins the shared core; the fork wins on these 5 additive
features. So we base on upstream and re-port the features, NOT the reverse.

---

## 3. Whose approach wins (per overlap zone)

| Overlap | Winner | Notes |
|---|---|---|
| `depot_runner.rs` core | Upstream | 14x more work; rebuild features against it |
| Template system | Upstream | Named-profile library + parity tests |
| Output/install-dir naming | Upstream | canonical installdir, MAX_PATH shortening, space preservation, real-app naming for shared/DLC depots |
| ACF timestamp cleanup | Upstream | `c40ab93` zero-out LastUpdated; drop fork's |
| `.npmrc` removal + gitignore | Upstream | it also gitignores; drop fork's |
| Branch-password forwarding | Upstream | `38153cd`; verify Queue-tab wiring parity, then drop fork's |
| Login/token store | Upstream base + fork's account-hint | see below |
| App self-update check | Upstream `update_check.rs` | distinct from fork's Steam build watcher; keep both; repoint to our repo |

Login detail: upstream `login_store` is strictly more capable (portable-aware,
encrypted, compression-password store) but `save_login_data` requires a password.
After a QR-only login there is no username to persist, and the watcher needs a
username to pass `-username` for silent token reuse. The fork's `account.dat`
username-hint fills exactly that gap. Plan: base on upstream's login_store and
carry the account-hint concept forward, ideally by extending upstream's store to
persist a username even with no saved password (rather than a separate file).

---

## 4. Repository and history strategy

### 4.1 Re-anchor (DONE locally, push pending)
- `main` reset to `upstream/main` (204c603). Now shares upstream history; clean
  fast-forward relationship.
- Safety net (all created before the reset):
  - branches `backup/pre-reanchor-main`, `backup/pre-reanchor-steam-library`
  - tags `archive/pre-reanchor-2026-09-20`, `archive/pre-reanchor-main-2026-09-20`
  - full bundle: `Z:/source/repos/_backups/OmniPacker-pre-reanchor-2026-09-20.bundle`
- Old `steam-library` branch retained as the port-reference source.
- PENDING: force-push `main` to origin (rewrites the fork's public history).
  Also push backup branches/tags so the safety net exists on the remote too.
- After push: set `main` to track `origin/main`; keep `upstream` remote for
  periodic `git fetch upstream && git merge --ff-only upstream/main`.

### 4.2 Branch model (git hygiene going forward)
```
main                       always releasable; tracks origin; syncs from upstream via ff-only
  <- feature/<slug>        one feature per branch, PR into main, delete after merge
  <- fix/<slug>            one concern per branch
  <- chore/<slug>          tooling, docs, deps
```
- PR into `main` even solo (self-review, clean history).
- Tag releases on `main` as `vX.Y.Z` via `bump-version.ps1`.
- Keep live feature branches rebased on `main` as upstream syncs land.

---

## 5. DepotDownloader fork strategy (DECIDED: git submodule)

We must NOT depend on the original dev's custom DepotDownloader (currently cloned
at `../DepotDownloader`, producing the `+opN` builds). We need our own trackable
fork so any change is ours and reproducible.

DECISION (2026-09-20): git submodule at `vendor/DepotDownloader`, pinned to an
exact commit in our own DD fork. Our DD fork gets the same primary treatment as
this repo (its own `upstream` remote pointing at the dev's DD fork, clean
history, tagged builds). `rebuild-depotdownloader.ps1` builds from the pinned
submodule commit so a fresh clone reproduces the exact sidecars. End users get
prebuilt sidecars in releases; only builders init the submodule.

Provenance: our DD fork continues the `+opN` lineage with clear attribution back
to the dev's DD fork, or restarts under our own scheme - decide when we create
the fork. Either way the commit history records where each `+opN` build came
from.

---

## 6. Feature roadmap

Ordering favors landing the subsystems first; UI/UX overhaul is deliberately
deferred to the end so we design it once against a stable backend.

### Phase 1 - Foundation (in progress)
- [x] Re-anchor main; create backups/bundle.
- [ ] Force-push main + backups to origin; fix tracking.
- [ ] Reconcile CLAUDE.md onto the upstream base (the detailed fork notes must be
      rebuilt as features re-land).

### Phase 2 - DepotDownloader fork control
- [ ] Create our DD fork on GitHub; decide submodule vs separate repo (Q1).
- [ ] Repoint `rebuild-depotdownloader.ps1` at our DD fork, pinned commit.
- [ ] Re-tag our first DD build under our own scheme (drop the `+opN` lineage or
      continue it with clear provenance).

### Phase 3 - Port clean API modules (low risk, backend only)
- [ ] Port `steam_news.rs`, `steam_store.rs`, `app_detail.rs` onto upstream base.
- [ ] Register their commands in `lib.rs` invoke_handler.
- [ ] Unit tests for parsing.

### Phase 4 - Port Library + watcher (login rewiring)
- [ ] Port `owned_apps.rs`; rewire auth-arg calls to upstream's new depot_runner.
- [ ] Port `update_watcher.rs`; repoint `resolve_credentials_dir` to upstream's
      config-dir helpers; reconcile with upstream's portable-aware paths.
- [ ] Login: keep QR; base on upstream login_store; add username-hint for the
      QR-only case so silent watcher login works.
- [ ] Keep upstream's `update_check.rs` (app self-update) alongside the watcher.

### Phase 5 - Network/DepotDownloader resilience (headline hardening)
- [ ] Classify DD failures: transient (rate limit, connection reset, manifest
      fetch timeout, Steam CM disconnect) vs fatal (auth, no license, invalid
      appid). Central error taxonomy in the runner.
- [ ] Automatic retry with capped exponential backoff + jitter for transient
      classes; resume partial downloads where DD supports it.
- [ ] Surface retry state in the UI (attempt N/M, next retry in Ns, cancel).
- [ ] User-configurable policy: max attempts, backoff base/cap, per-error
      overrides, "give up vs keep retrying" for long unattended archival runs.
- [ ] Preflight/network health check before a big job; clear actionable errors.

### Phase 6 - Expose all options (full configurability)
- [ ] Audit every DepotDownloader flag; map which to expose (Q4 scope):
      -validate, -max-downloads, -cellid, -manifest-only, -os/-osarch,
      -language/-all-languages, -all-platforms/-all-archs, -depot filters,
      -branch/-beta + -branchpassword, -manifest (historical), -remember-password,
      workshop/UGC (-pubfile/-ugc) - IN SCOPE (see scope decision), plus
      depot-only / manifest-only modes.
- [ ] Settings surface: curated "Advanced" panel for common flags + a raw
      passthrough escape hatch for power users (validated allowlist).
- [ ] Persist per-job vs global defaults; sensible presets.

### Phase 7 - DLC and multi-depot association (archiver core)
- [ ] Model: a "title" = base app + its DLC appids + their depots + shared/redist
      depots, discovered via the fork's dlcappid output and PICS.
- [ ] Let the user see and toggle each DLC/depot, with real names (leaning on
      upstream's real-app naming for shared/DLC depots).
- [ ] Output layout (DECIDED): user chooses per job between a merged
      Steam-accurate steamapps tree (base + selected DLC) and separate per-DLC
      outputs. Support both structures; preserve correct .acf/appmanifest
      fidelity in either.
- [ ] Handle depots shared across apps without double-downloading.
- [ ] Multiple Steam accounts per install (in scope): account switcher, per-
      account library/watch state and durable token.

### Phase 8 - Branches and historical releases (archiver core)
- [ ] Full branch/beta handling incl. password-protected branches (build on
      upstream + fork wiring).
- [ ] Download a specific historical build via per-depot `-manifest <id>`.
- [ ] Manifest-history sourcing (DECIDED): tiered - query PICS first, fall back
      to SteamDB integration as a fail-safe for deeper history, and always allow
      manual manifest-id entry. Present available builds and let the user pick a
      point in time. Isolate SteamDB behind an interface so its scraping/ToS/rate
      risk stays contained and optional.
- [ ] Record provenance (buildid, manifest ids, branch, date) in output metadata
      so an archive is self-describing.

### Phase 9 - Self-updater and release identity
- [ ] Repoint `update_check.rs` to `Rustbeard86/OmniPacker` releases.
- [ ] Ensure release CI builds per-platform sidecars from OUR DD fork.
- [ ] Version/branding pass (keep "OmniPacker" name unless decided otherwise).

### Phase 10 - UI/UX overhaul (deferred, iterate last)
- [ ] Redesign against the finished backend: unified title/DLC/depot browser,
      job/retry visibility, options surface, historical-build picker, watcher and
      patch-notes integration.
- [ ] Serious UX changes TBD (Rust to specify). i18n: extend upstream's 5
      languages to cover new strings, or English-first then backfill (Q in
      section 8).

---

## 7. Cross-cutting concerns

- Testing/CI: keep upstream's CI gate + template parity; add tests per new module
  and for the retry/error taxonomy.
- i18n: upstream ships de/en/es/fr/ru. New UI must add keys to all or we adopt an
  English-first-with-fallback policy. Decide before Phase 10.
- Platforms: fork dev happens on Windows; upstream maintains 5 targets. Decide
  whether to keep full matrix or Windows-first with best-effort others.
- Provenance/licensing: preserve upstream LICENSE; document our DD fork's origin
  and license; every adapted change traceable.
- CLAUDE.md: the detailed module docs (Library/watcher/etc.) must be rebuilt on
  the new base as each feature re-lands.

---

## 8. Decisions and open questions

### Decided (2026-09-20 review)
- DD fork: git submodule at `vendor/DepotDownloader`, pinned commit (section 5).
- Historical builds: tiered PICS -> SteamDB fail-safe -> manual entry (Phase 8).
- DLC layout: user-selectable per job, merged or separate (Phase 7).
- Scope: games + DLC + shared depots, Steam Workshop/UGC, depot-only/manifest-
  only modes, and multiple Steam accounts per install - all IN SCOPE.
- Git identity: `Rustbeard86 <Rustbeard86@users.noreply.github.com>` (anonymous).

### Still open (non-blocking; proposed defaults in the phases above)
1. Still need: do you already have a GitHub fork of the custom DepotDownloader,
   or do we create one? Where is the current `../DepotDownloader` sourced from
   (whose fork/commit)? Needed to init the submodule in Phase 2.
2. Options exposure: curated Advanced panel only, or also a raw arg passthrough
   (validated allowlist) for power users? (Proposed: both.)
3. Retry defaults: confirm the shipping policy (proposed: 5 attempts, exponential
   backoff 5s..5m + jitter, auto-resume) and whether long unattended archival
   runs default to "retry until cancelled".
4. Storage: interest in cross-build dedup/hardlinking to tame disk when archiving
   many historical builds? (future; proposed: defer.)
5. i18n policy for new strings: translate into all 5 languages as we go, or
   English-first with fallback and backfill later? (Proposed: English-first with
   fallback, backfill before a release.)
6. Platform matrix: maintain all 5 targets or Windows-first best-effort others?
7. GitHub numeric ID: if you want the modern `ID+Rustbeard86@users.noreply.
   github.com` no-reply form (from GitHub email settings), provide the ID; we are
   currently using the username-only no-reply form.
