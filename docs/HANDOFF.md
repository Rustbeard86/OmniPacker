# OmniPacker - Session Handoff

Last updated: 2026-09-20. Read this first to resume. It is the "where are we"
pointer; the deep plans are in the companion docs listed in section 9.

Addressed to whoever continues (the user is [Rust]; git identity is
`Rustbeard86 <Rustbeard86@users.noreply.github.com>`).

---

## 1. The one-paragraph situation

OmniPacker is a fork (github.com/Rustbeard86/OmniPacker) of elgreams/OmniPacker -
a Tauri v2 desktop GUI that currently drives the DepotDownloader (DD) sidecar to
download Steam depots and package them. We are transforming it into "an
archiver's best friend" for Steam: multi-account, searchable owned-app library,
per-title config/profiles, historical builds, DLC/depot association, resilient
downloads, all local-first. The chosen path is to REPLACE the DD sidecar with a
headless daemon built on the vendored **SteamKit2 engine** from the user's own
SteamForge project, driven over a frozen IPC contract. The engine backend
(auth/metadata/download) is built and verified; it is NOT yet wired into the UI,
and DD is still the shipping downloader until the engine reaches parity.

---

## 2. Git state (accurate as of 2026-09-20)

- `origin` = github.com/Rustbeard86/OmniPacker (the fork). `upstream` =
  github.com/elgreams/OmniPacker (added this project; read-only sync source).
- `main` is RE-ANCHORED onto `upstream/main` (commit 204c603, v1.4.0 + audit) and
  is 0/0 with it - so future upstream work merges cleanly (`git fetch upstream &&
  git merge --ff-only upstream/main`). The fork's ORIGINAL unrelated-history main
  is preserved (see backups).
- Branches:
  - `main` - releasable, == upstream. Nothing of ours merged into it yet.
  - `feature/e0-engine-foundation` - ALL the engine backend work (PR #2). Active
    branch; 8 commits ahead of main. **This is where the new code lives.**
  - `chore/vendor-depotdownloader` - the DD git submodule (PR #1).
  - `chore/fork-roadmap` - the planning docs (FORK-SYNC-PLAN.md,
    ARCHITECTURE-DISCUSSION.md) and roadmap progress.
  - `backup/pre-reanchor-main`, `backup/pre-reanchor-steam-library` - the fork's
    pre-re-anchor state (pushed to origin). Tags `archive/pre-reanchor-*-2026-09-20`.
  - `steam-library` - the OLD active dev branch before the re-anchor (22 commits
    of the previous, now-superseded Library/watcher/patch-notes approach). Kept as
    reference only; do NOT build on it.
- Open PRs (NEITHER merged): **#1** DD submodule, **#2** engine backend. The user
  reviews/merges PRs; do not merge without asking. Pushing feature branches is OK
  this session.
- Full backup bundle (all refs, pre-re-anchor):
  `Z:/source/repos/_backups/OmniPacker-pre-reanchor-2026-09-20.bundle`.

---

## 3. Toolchain / environment

- Windows 11, git-bash + PowerShell available.
- .NET SDK 10.0.401, cargo/rustc 1.96, node 26 - all present; both sides build.
- `gh` authed as Rustbeard86 with scopes incl. repo, workflow, delete_repo.
- VPS: `ssh ubuntu@209.126.85.78` (key auth, no password). Runs SteamForge in
  Docker (container `deploy-app-1`, volume `steamforge_data` at `/data`).
  fail2ban is active: do ALL remote work in ONE ssh connection (batch), never
  rapid-fire connections. `docker` needs `sudo -n` there.

---

## 4. The two vendored dependencies (both under Rustbeard86 control)

### DepotDownloader (current downloader; being phased out)
- Our fork: github.com/Rustbeard86/DepotDownloader, parent elgreams/DepotDownloader
  (which forks SteamRE). `master` @ v3.4.0+op5 (commit e17792d).
- Wired as git submodule `vendor/DepotDownloader` (branch omnipacker->master,
  pinned op5) on branch `chore/vendor-depotdownloader` (PR #1). The submodule has
  `elgreams` + `steamre` remotes for future syncs. `scripts/rebuild-depotdownloader.ps1`
  builds sidecars from it into `src-tauri/binaries/<platform>/`.
- NOTE: the submodule only exists on `chore/vendor-depotdownloader`, not on main
  or the engine branch. `git submodule update --init vendor/DepotDownloader` on
  that branch.

### SteamForge engine (the future downloader)
- VENDORED COPY (not a submodule) at `engine/src/SteamForge.{Abstractions,Engine}`
  and `engine/src/SteamForge.Plugins.Game`, taken from the user's local SteamForge
  repo `Z:/source/repos/SteamWorkshopDownloader.NET` at commit
  **48a4fef6b9844907a8355ac6ca667c5cfa0b0163** (2026-09-11). Provenance in
  `engine/README.md`.
- SteamForge is PRODUCTION (deployed on the VPS) and local-only (no GitHub remote,
  its docs carry VPS/admin secrets). NEVER push changes back to it. OmniPacker
  evolves this copy independently; when editing a vendored file, note it.

---

## 5. What has been built and VERIFIED

The engine backend, all on `feature/e0-engine-foundation` (PR #2):

- **Daemon** `engine/src/OmniPacker.EngineHost` (C#, net10.0): reads line-JSON
  requests on stdin, writes responses/events on stdout (stderr = human logs only).
  Transport-agnostic (stdio now, socket later). Composes only the Steam core (no
  auto-connect - launching it never touches Steam by itself).
- **IPC contract** frozen v1 in `docs/IPC-CONTRACT.md`. Envelope: `{"t":"req"|
  "res"|"evt", ...}`. Implemented methods: `ping`, `hello`, `shutdown`,
  `auth.status`, `account.list`, `account.switch`, `auth.resume`, `auth.beginQr`,
  `auth.beginCredentials`, `auth.submitGuard`, `auth.logout`, `library.enumerate`,
  `app.branches`, `app.depots`, `download.start`, `download.cancel`. Events:
  `ready`, `log`, `auth.status`, `download.progress`, `download.done`,
  `download.failed`.
- **Rust host** (in `src-tauri`):
  - `crates/engine-ipc` - protocol types (pure serde, no Tauri).
  - `crates/engine-client` - spawns/drives the daemon (id-correlated responses +
    event channel); has an integration test that drives the REAL daemon.
  - `src/engine_bridge.rs` - Tauri layer: lazily spawns the daemon, forwards
    events to the webview as `engine-event` ({event,data}), exposes ONE command
    `engine_request(method, params)`, kills the daemon on ExitRequested. Resolves
    the bundled sidecar via the resource path, else `OMNIPACKER_ENGINE_CMD/DLL`,
    else a dev `dotnet <dll>` guess. Registered in `src/lib.rs` (managed state +
    invoke_handler + exit handler).
- **Self-contained sidecar**: `scripts/build-engine-host.ps1 [-Rid <rid>]`
  publishes `src-tauri/binaries/<platform>/OmniPackerEngine(.exe)` (81 MB, no .NET
  SDK needed, gitignored). `bundle.resources` globs `binaries/<platform>/*` so
  releases include it automatically. win-x64 is built locally and verified to run
  standalone.
- **Tests (all green)**: C# 53 (`dotnet test engine/OmniPacker.Engine.slnx`),
  Rust `engine-ipc` 13 + `engine-client` 1 (`cargo test -p engine-ipc -p
  engine-client` in `src-tauri`). CI `.github/workflows/verify.yml` runs all on
  every push/PR (incl. a job that builds the daemon and runs engine-client).

Live-verified with REAL credentials (see section 6):
- Silent token resume -> LoggedOn; `account.list` (6 accounts);
  `account.switch` (daddydrew86 -> smackdown72); `library.enumerate` = 940 apps /
  847 packages; `app.branches`/`app.depots` for TF2 (appid 440);
  `download.start` streaming progress (resolved 8 depots, began chunk download);
  **QR login end to end via a real Steam mobile scan**.

---

## 6. Credentials / the safe dev store (IMPORTANT)

- The engine stores Steam refresh tokens (bearer creds) as plaintext JSON in a
  per-user OS app-data dir, NEVER the repo:
  `%LOCALAPPDATA%\OmniPacker\engine\steam-tokens.json`
  (i.e. `C:\Users\drewb\AppData\Local\OmniPacker\engine\`). SteamKit auto-refreshes
  the live session; rotated tokens are re-saved. `.gitignore` guards
  `engine-data/`, `**/steam-tokens.json`, `account.config`, `downloads/.credentials/`.
- That store currently holds **6 accounts** pulled once from the VPS
  (`sudo -n docker exec deploy-app-1 cat /data/steam-tokens.json`, piped straight
  to the local file, never printed): daddydrew86, smackdown72, blackenedice,
  ln2dl4vk3pk6, p4rogue, calebgaming86. daddydrew86's token was refreshed by the
  QR test. These belong to the user and are the dev-test logins.
- To re-pull if tokens expire: one batched ssh, `sudo -n docker exec deploy-app-1
  cat /data/steam-tokens.json` -> redirect to the local store path. Do not print
  the token contents.
- The store is plaintext (SteamKit norm, like DD's account.config). Hardening to
  OS-encryption (DPAPI/Keychain) is a possible future task, not done.

---

## 7. Design decisions already LOCKED (don't relitigate)

From `docs/ARCHITECTURE-DISCUSSION.md` (section 10):
- Engine: adopt SteamForge's SteamKit2 engine as a VENDORED COPY + headless
  daemon. Retire the DD sidecar once the engine downloader reaches parity.
- IPC: transport-agnostic line-JSON; stdio-pipe transport first, socket a later
  drop-in (portability lives in the transport, not the protocol).
- Scope: LOCAL archiver only (disk + optional 7-Zip; no upload). In scope: games
  + DLC + shared depots, Steam Workshop/UGC, depot-only/manifest-only modes,
  multiple accounts per install.
- Config: layered Global -> App/Title -> Job merge, safe defaults, per-value
  provenance, validated raw-arg passthrough. Storage split: JSON for user
  config/profiles, SQLite (engine daemon) for cache/dedupe/history/job state.
  Profiles = a global named library, assignable per app.
- Accounts: automatic owner rotation (AccountRouter-style) + optional manual pin;
  one deduped local library cache spanning all accounts.
- SteamDB (for historical builds, tiered PICS -> SteamDB -> manual): OPT-IN,
  isolated behind an adapter, persisted per-account webview, VERSIONED hot-swap
  extraction scripts (so site changes = ship a script, not an app release),
  Cloudflare-prompt surfacing, optional safe background fetch. Windows-first
  behind a platform-webview abstraction so other-OS support is a minimal PR.
  RISK: SteamDB is now owned by NexusMods - treat it as degradable; core archival
  must never depend on it.
- UI: contract-first (freeze the backend API before serious UI); tabbed; primary
  = one searchable app list across all accounts (auto-switch, cached-first, lazy,
  non-blocking); per-app config/profile window; session tab with a collapsible,
  color-by-origin (global/app/job) settings overlay that pre-configured users can
  skip. UI decoupled from the backend, overridable via loose-file drop-ins + a
  hot-reload, so we and the community iterate before baking a final UI.
- Reference implementations to reuse: SteamForge (engine, job store, ownership
  scanner, PICS watcher, dedupe by depot+manifest); OutsideLauncher at
  `Z:/source/repos/OutsideLauncher` (WebView2 SteamDB login persistence +
  versioned extraction scripts - the model for our SteamDB tier).

---

## 8. NEXT STEPS (where to resume)

Ordered by leverage. Items 1-2 need a resource only the user can provide, so ask.

1. **Engine -> finalization (highest leverage; needs a SMALL owned app id).**
   Wire the engine's downloaded depot output into OmniPacker's existing
   `job_finalization.rs`/`acf_generator.rs` (steamapps/.acf layout) and verify a
   full small download produces a correct archive. TF2 (440) is 30 GB - too big;
   ASK the user for a small owned app id to test. This is the last piece before
   the engine can produce a real archive and DD can start being retired.
2. **Branch-password parity (needs a password-protected branch the user can access).**
   `ResolveGameAsync`/`GetManifestAsync` in `engine/src/SteamForge.Engine/Content/
   SteamContentClient.cs` take a branch but no beta password; protected betas need
   `CheckAppBetaPassword` + the branch password hash into `GetManifestRequestCode`
   (currently passes null). Real engine change; verify against a protected branch.
3. **Parity extras that need NO external resource (can do autonomously):**
   os-arch and language depot filters, and a `-validate` equivalent, added to the
   engine download path (`DownloadGameAsync`/`ResolveGameAsync`). Add hermetic
   guards + live-verify with the existing token store.
4. **SteamDB tier** (`app.buildHistory` + historical build download via per-depot
   `-manifest`). Its own subsystem - see the design in section 7 and OutsideLauncher.
5. **UI phase** (frontend calls `engine_request`; account/library/download/config
   screens; the drop-in/hot-reload system). The user wants to drive UI direction.
6. **Wire `build-engine-host.ps1` into the per-platform release scripts** (e.g.
   `scripts/build-windows-x64.ps1`) so releases build the engine sidecar. Not done.
7. **Reconcile the repo `CLAUDE.md`**: on `main` it is upstream's (pre-fork-features)
   version; the detailed fork notes were on the old `steam-library` branch. Rebuild
   OmniPacker's CLAUDE.md on the new base as features re-land.

---

## 9. Companion docs (source of truth for detail)

- `docs/FORK-SYNC-PLAN.md` (on `chore/fork-roadmap`) - re-anchor rationale, the
  fork-vs-upstream analysis, whose-approach-wins matrix, phased roadmap, DD fork.
- `docs/ARCHITECTURE-DISCUSSION.md` (on `chore/fork-roadmap`) - the full archiver
  architecture, all locked decisions (section 7 above summarizes), engine adoption
  analysis, E0-E3 phase status.
- `docs/IPC-CONTRACT.md` (on `feature/e0-engine-foundation`) - the frozen v1 wire
  contract; keep it in lock-step with the code and `contracts/fixtures/`.
- `engine/README.md`, `contracts/README.md` (feature branch) - engine build/test +
  the cross-language fixture testing rule (a message shape is "in the contract"
  only when BOTH a C# and a Rust test cover its fixture).

---

## 10. How to verify / run things

- C# tests: `dotnet test engine/OmniPacker.Engine.slnx` (note: `.slnx`, .NET 10
  solution format - NOT `.sln`).
- Rust tests: from `src-tauri`, `cargo test -p engine-ipc -p engine-client`.
  Do NOT rely on plain `cargo test` for the app crate - `omnipacker` needs
  frontend assets (generate_context!) to build its test harness.
- Run the daemon by hand (dev): `dotnet engine/src/OmniPacker.EngineHost/bin/
  Debug/net10.0/OmniPacker.EngineHost.dll`, then pipe JSON lines, e.g.
  `{"t":"req","id":1,"method":"auth.resume","params":null}`. It uses the safe
  app-data store by default (has the 6 tokens); set `OMNIPACKER_ENGINE_DATA` to
  override.
- Interactive QR test: set `OMNIPACKER_ENGINE_TTY_QR=1`, send `auth.beginQr`; the
  daemon writes `C:\Users\drewb\AppData\Local\Temp\omnipacker-qr.png` and prints
  its path to stderr. Steam rotates the challenge ~30s and the daemon overwrites
  the PNG in place, so a plain image viewer caches a STALE code and Steam says
  "failed to load qr info". Open it in a BROWSER via an auto-refreshing HTML
  wrapper (an `<img>` reloaded every 2s with a cache-buster) so it always shows
  the current code. Keep only ONE daemon running (two clobber the same PNG). The
  QR-driver used this session lived in the session scratchpad (ephemeral); re-make
  it if needed.
- Publish the sidecar: `pwsh scripts/build-engine-host.ps1 -Rid win-x64`.

---

## 11. Gotchas / conventions

- ASCII-only in everything produced (no em/en dashes, curly quotes, arrows, etc.);
  vendored external code is the exception. Run `/ascii-clean` if unsure.
- git hygiene: feature/fix/chore branch per concern, PR into main, user merges.
  Commit messages end with the `Claude-Session:` trailer.
- The daemon's stdout is PROTOCOL ONLY; anything human goes to stderr.
- On exit, `RunEvent::ExitRequested` in `src/lib.rs` kills DD, 7-Zip, AND the
  engine daemon (Windows does not kill child processes on window close).
- `git add -A` will wrongly stage `vendor/DepotDownloader` (embedded repo on
  branches without the submodule) and stray installers in `release/`. Stage
  specific paths.
- No sub-agents (user preference); do work in the main session.
- Reversible/VPS/own-infra actions are pre-authorized; stop only for destructive/
  irreversible or genuine scope changes. The user is often not at the helm and is
  NOT testing unless they say so.
