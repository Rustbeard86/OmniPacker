# OmniPacker Archiver - Architecture Discussion

Status: draft for discussion (2026-09-20). Companion to `FORK-SYNC-PLAN.md`.
This organizes the vision into concrete architecture, records what is decided,
flags what is open, and points at the reference implementations we reuse.

Guiding principle from the brief: local-first, cached-everything, lazy,
never-blocking UX; expose full control without forcing steps on users who just
want to run; a decoupled, hot-reloadable UI bound to a stable backend API.

---

## 0. Vision

"An archiver's best friend for Steam." Multi-account, a single searchable/
filterable app list across all enabled accounts (no manual per-account login),
per-title configuration with reusable profiles, historical-build downloads,
DLC/depot association, full branch handling, resilient/auto-retrying downloads,
everything cached locally, and a UI that is separate from and overridable on top
of the backend.

---

## 1. Reference implementations (reuse before writing)

- Z:/source/repos/SteamWorkshopDownloader.NET ("SteamForge") - SteamKit2 engine
  with a plugin pipeline. Reusable patterns:
  - SQLite job store/queue (`Jobs/JobStore.cs`, `JobQueue*.cs`).
  - `OwnershipScanner`, `LibraryRescanService` - owned-app enumeration/rescan.
  - `PicsChangeWatcher` - PICS change detection (our update-watcher, done right).
  - `StorageMonitor`/`StorageOptions`/`StoragePaths`, `ExpiryPruner`.
  - Dedupe history keyed by depot + manifest id (identical request reuses result).
  - Plugin contracts `IContentSource`/`IArchiver`/`IUploader`.
  - Note: it is a hosted Blazor service on a VPS using SteamKit2 DIRECTLY, not
    the DepotDownloader sidecar. See open question Q-ARCH-1.
- Z:/source/repos/OutsideLauncher - WPF + WebView2 Steam/SteamDB integration.
  Reusable patterns (this is the model for our SteamDB approach):
  - `Views/SteamDbFetchWindow`, `SteamDbSearchWindow`, `DepotVerifyWindow`,
    `SetupWizardWindow` (dedicated SteamDB login browser).
  - WebView2 `CoreWebView2Environment` with a persisted `userDataFolder` so
    SteamDB login/cookies persist across runs.
  - Navigate + `ExecuteScriptAsync(extractionScript)` to parse pages.
  - Versioned, updatable extraction JS cached at `cache/steamdb/` with a
    `steamdb-version.txt` and an `-upstream.js` - exactly the "augment our
    parsing scripts, hot-swap them" idea.
  - `SteamStoreService` sets age-gate cookies for store fetches.

---

## 2. Config model (mostly decided)

DECIDED:
- Apply order (later overrides earlier): Global defaults -> App/Title config ->
  Per-job override. Backend merges into a resolved settings object.
- Layered merge where it makes sense; every value has a safe/sane default so a
  missing value is never fatal.
- Provenance tracking: the resolved object records WHICH stage set each value, so
  the UI can show the origin of every setting (see UI section, color-by-stage).
- Raw arg passthrough: YES, but validated (allowlist + sanity checks) so it can't
  break the run or smuggle unsafe flags.
- Multiple accounts handled per DD token; a locally managed, deduped library
  cache across accounts.

OPEN:
- Storage format/location (portable-aware; JSON per current app, or SQLite like
  SteamForge?). See Q-CFG-1.
- Profiles: named, reusable config bundles at the app/title stage - scope and
  where they live. See Q-CFG-2.
- Exactly which DD options are "basic" vs "advanced" vs internal (section 3).

---

## 3. DepotDownloader option surface (discovery for "expose all")

From the vendored fork (`Program.cs`) plus fork-only modes. Proposed tiering:

Basic (common, in the app/job UI):
- `-app`, `-depot` (list), `-branch`/`-beta`, `-branchpassword`, `-os`, `-osarch`,
  `-language`, `-dir` (managed by us), `-validate`/`-verify-all`.

Advanced (power-user panel):
- `-manifest` (list; historical builds), `-manifest-only`, `-all-platforms`,
  `-all-archs`, `-all-languages`, `-lowviolence`, `-max-downloads` (default 8),
  `-cellid`, `-loginid`, `-filelist`, `-use-lancache`.

Content-type modes (scope features):
- `-pubfile`, `-ugc` (Workshop/UGC - in scope).

Auth/internal (managed by us, not free-form UI):
- `-username`/`-user`, `-password`/`-pass`, `-remember-password`, `-qr`,
  `-no-mobile`, `-clear-token`, `-config-dir` (per-account token dir),
  `-debug`.

Fork-only (managed):
- `-list-owned` (+ `-include-free`), `-app-build` (watcher), dlcappid + depot-name
  stdout markers.

Escape hatch: validated raw passthrough for anything not surfaced.

---

## 4. Multi-account and library cache

DECIDED direction:
- One durable DD token per account via `-config-dir <per-account dir>` (matches
  the fork's `-config-dir` patch). Account rotation switches the active token dir.
- Local, deduped library cache aggregating all enabled accounts' owned apps, so
  the primary list spans every account without manual login/switching.
- Cached-first, lazy, non-blocking: reads serve cache instantly; refreshes happen
  in the background at safe rates; change-detection (PICS) before refetching.
- Reuse SteamForge's `OwnershipScanner` / `LibraryRescanService` /
  `PicsChangeWatcher` patterns (adapted; we drive DD, they drive SteamKit2).

OPEN: see Q-ACCT-1 (rotation UX), Q-ARCH-1 (engine choice affects how cheaply we
can enumerate/rotate).

---

## 5. SteamDB integration (opt-in)

DECIDED:
- Opt-in only. Isolated behind an interface so its rate/ToS risk is contained.
- Managed webview with a persisted, per-account userDataFolder so SteamDB login
  persists and is paired to the matching DD-token account (rotate DD account ->
  rotate SteamDB session together; invariant: DD token account == SteamDB account).
- Custom, seamless login: drive `https://steamdb.info/login/` directly rather
  than the awkward mobile-mode nav; a dedicated login surface where possible.
- Our own versioned, hot-updatable extraction/parsing scripts (OutsideLauncher
  model) - cache locally, check for changes before refetching, don't hammer.
- Surface Cloudflare / bot-check / "are you human" prompts to the user for manual
  approval, then resume.
- Optional background "service": while OmniPacker runs, safely fetch + cache data
  for logged-in / rotated accounts across all apps at a safe rate limit.

OWNERSHIP RISK (2026-09): SteamDB is now owned by NexusMods. The site, its
markup, login, and access policy will very likely change over the next 6-12
months. This makes resilience mandatory, not optional:
- All SteamDB knowledge lives in versioned, hot-updatable extraction scripts and
  a thin adapter behind the interface - never hard-coded selectors in the core.
  When the site changes we ship a new script version, not an app release.
- Treat SteamDB as a degradable, opt-in enrichment source: if it breaks or
  changes terms, the app must fall back cleanly to PICS + manual entry with no
  loss of core function. Never make any core archival path depend on it.
- Watch for the login/auth flow moving under a NexusMods account; keep the login
  surface swappable. Cache aggressively so a broken session degrades gracefully.

REUSE: OutsideLauncher `SteamDbFetchWindow`/`SetupWizardWindow` login browser,
persisted `userDataFolder`, versioned `cache/steamdb/*.js` extraction scripts.

CROSS-PLATFORM CONCERN: OutsideLauncher uses WebView2 (Windows). Tauri uses the
OS webview (WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux). Persisted
cookies + a scriptable secondary webview must be validated per platform, or we
gate the SteamDB webview to platforms where it is reliable. See Q-SDB-1.

WHAT WE PULL FROM STEAMDB (vs Steam API we already have via steam_store):
- Manifest/build history per depot (for historical builds), build dates,
  DLC/depot associations and names. Steam store/API stays the source for basic
  metadata; SteamDB augments history/associations. Confirm scope: Q-SDB-2.

---

## 6. Download resilience and retries

- Central error taxonomy classifying DD failures: transient (rate limit, CM
  disconnect, connection reset, manifest fetch timeout) vs fatal (auth, no
  license, invalid app/depot).
- Auto-retry transient classes with capped exponential backoff + jitter; resume
  partial downloads where DD supports it.
- Retry state surfaced in the session UI (attempt N/M, next-in, cancel).
- Configurable policy (global/app/job): max attempts, backoff base/cap, per-error
  overrides, and a "retry until cancelled" mode for long unattended archival runs.
- Optional network preflight before large jobs.

---

## 7. UI/UX architecture

DECIDED shape:
- Keep the tabbed approach.
- PRIMARY view: one searchable/filterable app list spanning ALL enabled accounts;
  account switching is automatic and invisible; cached-first, lazy, never blocks.
- SELECT an app -> SECONDARY view (a separate window if feasible) for per-app/
  per-title configuration and profile creation. This is where full settings are
  exposed.
- SESSION/QUEUE tab: queued apps and per-run setting overrides.
- Staged-settings overlay: at each stage (global -> app -> job) show the prior
  stage's effective settings, build/overlay this stage's object, and the final
  view shows the resolved settings annotated by the stage each value came from
  (color-coded wording/marker per stage). Must be collapsible / skippable: a user
  who already configured global/app defaults can just run the queue without being
  forced through steps, prompts, or clicks. Do not overload with info they don't
  want.

DECIDED architecture (decoupling + hot iteration):
- The UI binds to a STABLE backend API model (Tauri command + event contract).
  Backend logic is fully decoupled from presentation.
- Ship a default UI, but allow loose-file custom drop-ins that override UI code,
  so we and the community can iterate/hot-refresh without rebuilding the app.
- A force reload/refresh (hotkey) to hot-swap UI during iteration.
- Implication: design and freeze the backend API contract FIRST; treat the UI as
  a replaceable client. Iterate UI via drop-ins before baking a final UI into the
  release. See Q-UI-1 (drop-in mechanism), Q-UI-2 (contract-first scope).

---

## 8. Backend-first execution plan (supersedes UI-heavy phases until UI track)

B1. Account + token model: per-account `-config-dir`, account registry, rotation
    primitive, durable silent login (QR + credentials, keep QR). Port the fork's
    login/account-hint concept onto upstream's login_store.
B2. Library engine: owned-app enumeration across accounts, deduped local cache,
    PICS-based change detection; cached-first/lazy access. (Re-port owned_apps +
    update_watcher onto the new base; borrow SteamForge patterns.)
B3. Metadata engine: steam_store/steam_news/app_detail modules ported; define the
    cache layer (TTL, change-check) they share with the library engine.
B4. SteamDB engine (opt-in): managed webview + persisted per-account session,
    versioned extraction scripts, local cache w/ change-detection, Cloudflare
    prompt surfacing, optional background fetch service. Pair SteamDB session to
    DD account on rotation.
B5. Config engine: layered global/app/job merge with provenance, safe defaults,
    profiles, validated raw passthrough. Define the resolved-settings object that
    the UI renders.
B6. Resilience engine: error taxonomy, retry/backoff, resume, policy config,
    preflight.
B7. DLC/depot association + historical builds + branch handling: title model
    (base + DLC + depots + shared/redist), manifest-history sourcing (PICS ->
    SteamDB -> manual), per-depot `-manifest` historical downloads, merged vs
    separate output (user choice), self-describing output metadata.
B8. Backend API contract: freeze the command/event model the UI binds to
    (prereq for the UI track and drop-in system).

UI track (after the contract in B8): drop-in loader + hot reload, then the
primary list, per-app window, session/overlay views - iterated as drop-ins first.

---

## 9. Open questions

- Q-ARCH-1 (biggest): Do we keep OmniPacker on the DepotDownloader sidecar, or
  move the engine toward SteamKit2 directly (as SteamForge already does) - or even
  share/extract a common engine between the two projects? DD is simpler and
  already patched; a SteamKit2 engine makes cheap PICS/ownership/manifest-history
  queries and rotation far easier and is what SteamForge already implements.
- Q-CFG-1: Config storage - portable-aware JSON files (current style) or SQLite
  (shared with the library/dedupe/job store)?
- Q-CFG-2: Profiles - are they app-scoped, global-and-assignable, or both? Where
  do they live and how are they referenced by a job?
- Q-ACCT-1: Account rotation UX - fully automatic behind the app list, or an
  explicit "active account" concept the user can also pin?
- Q-SDB-1: SteamDB webview cross-platform - do we require it to work on all three
  OS webviews, or gate SteamDB to Windows (WebView2) first and treat Linux/macOS
  as best-effort?
- Q-SDB-2: SteamDB scope - just manifest/build history + DLC/depot associations,
  or also ratings/prices/other metadata? What is the minimum we cache?
- Q-SDB-3: Background fetch service - default rate limit and a hard cap? Should it
  be per-account, and pause on any Cloudflare challenge until the user clears it?
- Q-UI-1: Drop-in mechanism - loose HTML/CSS/JS files in a known folder that
  override the bundled UI at runtime? Any sandboxing, or full trust (community
  drop-ins are essentially plugins)?
- Q-UI-2: Contract-first - are you OK freezing a backend command/event API before
  serious UI work, accepting that the API is the real product surface and the UI
  is a swappable client?
- Q-SCOPE-1: Output/upload - SteamForge uploads to Gofile and prunes. Is any
  upload/share/expiry behavior in scope for OmniPacker, or is it strictly a local
  archiver (package to disk / 7-Zip only)?

---

## 10. Decisions log - review 2 (2026-09-20)

Answers locked this round:
- SCOPE: local archiver only (package to disk / optional 7-Zip). Upload/share is
  out for now; could be a later optional plugin. (Resolves Q-SCOPE-1.)
- API-FIRST: yes. Freeze a backend command/event contract before serious UI work;
  UI is a swappable, drop-in-overridable client. (Resolves Q-UI-2.)
- STEAMDB OS: implement Windows (WebView2) first, but build the platform-webview
  behind an abstraction so macOS/Linux support is a minimal, well-documented
  community PR - do not force contributors to touch core logic. (Resolves Q-SDB-1
  with a developer-friendliness requirement: clean extension points + docs.)
- ENGINE: adopt SteamForge's SteamKit2 engine rather than duplicate it, subject to
  the integration analysis below. (Direction for Q-ARCH-1.)

### 10.0 Config, profiles, rotation (locked)

- CONFIG STORE: split. User-editable config + profiles as portable, diffable JSON
  files (drop-in friendly, matches the loose-file/override ethos). Machine data -
  library cache, dedupe, history, job state - in the engine daemon's SQLite.
  (Resolves Q-CFG-1.)
- PROFILES: a GLOBAL library of named profiles; each app/title can be assigned a
  default profile; any job can select any profile. One place to edit, reused
  across apps. (Resolves Q-CFG-2.)
- ROTATION UX: automatic by default (AccountRouter-style, invisible owner switch)
  with an optional manual "pin active account" override for power users. The
  primary app list still spans all enabled accounts. (Resolves Q-ACCT-1.)

### 10.1 SteamForge engine - adoption analysis

Capabilities present (verified):
- `SteamContentClient` (1753 LOC): CDN auth tokens, depot keys, branch-scoped
  manifest download, chunk download/decrypt/decompress, PICS appinfo cache with
  build ids + depot manifest GIDs (dedupe ground truth). Manifest fetch takes an
  explicit manifestId + branch, so HISTORICAL builds are natively trivial (pass
  the old manifest id) - no SteamDB needed for the download itself.
- `AccountRouter`: auto-switches the active session to an account that OWNS the
  app (`EnsureOwnerAsync`/`RunWithOwnerAsync`, single active account, FIFO) - this
  is exactly the invisible multi-account rotation we want.
- `SteamSessionManager` + `WebAuthenticator` + `SteamTokenStore`: QR + credential
  auth, token persistence, multi-account.
- Jobs: `OwnershipScanner`, `LibraryRescanService`, `PicsChangeWatcher`,
  `JobStore` (SQLite), `StorageMonitor`, `ExpiryPruner`, `PipelineRunner`.
- Plugins: `Game` (app/game download), `Workshop`, `SevenZip`, `Images`, `Gofile`.

Gaps vs the DepotDownloader sidecar OmniPacker uses today (build locally):
- Branch PASSWORD (`-betapassword`), os/osarch/language depot filtering,
  `-validate`, and the fork-only enumeration markers. `Plugins.Game` is the place
  to check/extend for the download option matrix.
- The steamapps/.acf layout: OmniPacker already owns this (job_finalization,
  acf_generator); the engine only needs to yield raw depot content + metadata.

Integration facts / constraints:
- Cross-language: engine is C#/.NET 10; OmniPacker backend is Rust/Tauri. Reuse =
  run the engine as a HEADLESS SIDECAR/DAEMON that the Rust backend drives (the
  way it drives DD), NOT a linked library.
- No headless host exists (only `SteamForge.Web`). We must build a small headless
  host project exposing the engine over an IPC contract.
- SteamForge is production (VPS) and local-only (no GitHub remote), and its docs
  carry VPS IP/admin details. So OmniPacker must take its OWN copy and evolve it;
  changes NEVER go back to the production SteamForge tree. Record the source
  commit for provenance.
- Adopting the engine can eventually RETIRE the DD sidecar (one sidecar, all our
  code), which is the real "reduce size deployed" win. Until the engine downloader
  reaches DD parity, DD (the vendored submodule) stays as the working downloader.

### 10.2 Proposed engine phases (fit into the backend-first plan)

- E0: Bring the engine under OmniPacker's control (copy/vendor Engine +
  Abstractions + Plugins.Game, scrubbed of VPS/secret material), pin the source
  commit, add a headless daemon host + IPC contract skeleton.
- E1: Prove auth (QR + credentials, multi-account) + ownership enumeration through
  the daemon; replace `owned_apps`/watcher sidecar calls (B1/B2) with it.
- E2: Route metadata/PICS/manifest-history through the engine (B3, B7 history).
- E3: Route downloads through the engine (port branch-password/os/arch/language/
  validate into `Plugins.Game`), keep OmniPacker finalization/.acf on top; retire
  the DD sidecar + submodule once at parity.

### 10.3 Engine decisions (locked)

- Q-ENG-1 (intake): DECIDED - vendored COPY inside OmniPacker (Engine +
  Abstractions + Plugins.Game), scrubbed of VPS/secret material, source commit
  recorded for provenance. Isolates production SteamForge; OmniPacker evolves its
  own copy. Changes never go back to the production tree.
- Q-ENG-2 (IPC): DECIDED - transport-agnostic line-JSON message protocol over an
  abstract read/write stream ("virtual file"). Implement the STDIO PIPE transport
  first (minimal, fully cross-OS); a local socket is a later drop-in transport
  with no protocol/logic change. Rationale: portability risk is in the transport,
  not the protocol - pipes are identical across OSes; sockets vary and add
  port/lifecycle/auth work. This keeps the socket capability path open at
  near-zero extra cost now.
- Q-ENG-3 (DD future): DECIDED - retire the DD sidecar/submodule once the engine
  downloader reaches parity (branch password, os/arch/language, validate). DD
  stays only as the working downloader until then.
