# OmniPacker Engine IPC Contract

Status: v1 (frozen envelope). The Rust/Tauri backend ("host") drives the C#
engine daemon ("engine") over this protocol. This is the product's real backend
surface; the UI binds to commands the host derives from it.

See `docs/ARCHITECTURE-DISCUSSION.md` for why (vendored SteamKit2 engine as a
headless daemon). Golden wire fixtures live in `contracts/fixtures/` and are used
by BOTH sides' tests, so the two implementations can never silently drift.

## Transport

- Framing: newline-delimited JSON ("JSON Lines"), UTF-8. One message per line.
  A message MUST NOT contain a raw newline; strings are JSON-escaped.
- Transport is an abstract duplex byte stream. The default (v1) transport is the
  engine child process: host writes requests to the engine's stdin, reads
  responses and events from the engine's stdout. The engine's stderr is
  human-readable diagnostics only and is NEVER part of the protocol.
- A local socket is a permitted future transport: the message protocol below is
  transport-agnostic, so swapping stdio for a socket changes only the byte
  stream, not any message shape or logic.

## Envelope (frozen)

Every message is a single JSON object with a `t` discriminator:

Request (host -> engine):
```
{"t":"req","id":<u64>,"method":"<string>","params":<object|null>}
```
Response (engine -> host, exactly one per request `id`):
```
{"t":"res","id":<u64>,"ok":true,"result":<value|null>}
{"t":"res","id":<u64>,"ok":false,"error":{"code":"<string>","message":"<string>","retriable":<bool>}}
```
Event (engine -> host, unsolicited, no `id`):
```
{"t":"evt","event":"<string>","data":<value|null>}
```

Rules:
- `id` is a host-chosen, monotonically increasing unsigned integer. The engine
  echoes it on the matching response. Events carry no `id`.
- `params`, `result`, and `data` are arbitrary JSON values (usually objects).
  The ENVELOPE is frozen at v1; method/event PAYLOADS may add fields over time.
  Both sides MUST ignore unknown payload fields (forward-compatible).
- Unknown `method` -> error `unknown_method`. Malformed line -> the engine emits
  an `error` event and continues (it does not crash the stream).

## Error codes (stable)

- `bad_request`      - malformed params for a known method (retriable=false)
- `unknown_method`   - no such method (false)
- `unauthenticated`  - no logged-on account for an operation that needs one (false)
- `no_owning_account`- no signed-in account owns the requested app (false)
- `depot_access_denied` - Steam denied a depot key / entitlement (false)
- `transient`        - transient Steam/CDN/network failure (retriable=true)
- `cancelled`        - operation was cancelled (false)
- `internal`         - unexpected engine error (false)

## Methods - v1 implemented

- `hello` params:null -> result:
  `{"protocol":1,"engine":"<engine asm version>","host":"OmniPacker.EngineHost","capabilities":[<method names>]}`
- `ping` params:`{"nonce":<any>?}` -> result:`{"pong":true,"nonce":<echoed or null>}`
- `shutdown` params:null -> result:`{"stopping":true}`; the engine then exits 0.
- `auth.status` params:null -> result:
  `{"state":"<SteamAuthState>","accountName":<string|null>,"qrChallengeUrl":<string|null>,"prompt":<string|null>,"error":<string|null>}`
  Reads current login state without connecting. `state` is a SteamAuthState name:
  `Disconnected|Connecting|AwaitingQrScan|AwaitingGuardCode|LoggingIn|LoggedOn|Failed`.
- `account.list` params:null -> result:
  `{"accounts":[{"account":"<name>","obtainedAt":"<ISO-8601 UTC>"}, ...]}`
  Accounts with a stored durable refresh token; empty on a fresh install. Offline.
- `auth.resume` params:null -> result:`{"resumed":<bool>,"status":<auth.status payload>}`
  Silent login from the stored refresh token. Connects to Steam.
- `auth.beginQr` params:null -> result:`{"started":true}`
  Starts QR login in the background; the challenge URL then arrives as an
  `auth.status` event with state `AwaitingQrScan` and `qrChallengeUrl` set.
- `auth.beginCredentials` params:`{"username":"...","password":"..."}` -> result:
  `{"started":true}`; may then emit an `auth.status` `AwaitingGuardCode` event.
- `auth.submitGuard` params:`{"code":"..."}` -> result:`{"accepted":<bool>}`
  Feeds a Steam Guard code to the in-progress login (`accepted:false` if none waits).
- `auth.logout` params:null -> result:`{"loggedOut":true}` (stored token kept).
- `library.enumerate` params:null -> result:`{"account":"<name>","count":<n>,"sample":[{"appId":<id>,"name":"..."}]}`
  Enumerates owned apps for the logged-on account and persists them to the
  ownership catalog. Errors `unauthenticated` if not logged on.

## Events - v1 implemented

- `ready` data:`{"protocol":1,"engine":"<version>","host":"...","capabilities":[...]}`
  emitted once on startup before any request is processed.
- `log` data:`{"ts":"<ISO-8601 UTC>","level":"debug|info|warning|error","source":"<tag>","message":"<text>"}`
  every engine `ILogBroadcaster` line is forwarded here (already redacted).
- `auth.status` data: same shape as the `auth.status` result above; emitted on every
  Steam login state change so the host tracks QR url / guard prompt / logged-on live.

## Methods - reserved (planned, not yet implemented)

Documented so the contract is stable as they land. Payloads TBD when built.

- Auth/accounts: `account.switch`, `account.pin`.
- Library/metadata: `library.list` (aggregate across accounts), `app.info`,
  `app.buildHistory`, `app.depots`.
- Download: `download.start`, `download.cancel`, `download.status`.

Reserved events: `progress` (`{jobId,percent,message}`), `download.done`,
`download.failed`.

## Versioning

- `protocol` integer in `hello`/`ready` is bumped only for a BREAKING envelope
  change. Additive payload fields do not bump it. The host refuses to drive an
  engine whose `protocol` it does not support and says so.
