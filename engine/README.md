# OmniPacker engine (.NET)

The SteamKit2 engine OmniPacker's Rust/Tauri backend drives as a headless daemon
over the IPC contract in `docs/IPC-CONTRACT.md`. See
`docs/ARCHITECTURE-DISCUSSION.md` for the why.

## Layout

```
engine/
  OmniPacker.Engine.slnx
  src/
    SteamForge.Abstractions/   vendored - plugin contracts + DTOs
    SteamForge.Engine/         vendored - SteamKit2 session/auth, content client,
                               account router, ownership/PICS, SQLite job store
    SteamForge.Plugins.Game/   vendored - app/game content source
    OmniPacker.EngineHost/     the headless daemon (IPC over stdio, our code)
  tests/
    OmniPacker.EngineHost.Tests/  xUnit: codec/dispatcher/host-loop + fixtures
```

## Provenance (vendored, changes local-only)

`SteamForge.*` under `src/` is a VENDORED COPY of the private SteamForge project
(`Z:/source/repos/SteamWorkshopDownloader.NET`), taken from commit
`48a4fef6b9844907a8355ac6ca667c5cfa0b0163` (2026-09-11). SteamForge is deployed
in production, so we NEVER push changes back to it; OmniPacker evolves this copy
independently. When we modify a vendored file, note it in the file.

## Build / test

```
dotnet build engine/OmniPacker.Engine.slnx
dotnet test  engine/OmniPacker.Engine.slnx
```

The daemon reads line-JSON requests on stdin and writes responses/events on
stdout; stderr is human diagnostics only.

## Shipping (self-contained sidecar)

`scripts/build-engine-host.ps1 [-Rid <rid>]` publishes the daemon self-contained
(single file, no .NET runtime needed) into `src-tauri/binaries/<platform>/
OmniPackerEngine(.exe)`, alongside the DepotDownloader/7-Zip sidecars. It is
gitignored (81 MB) and rebuilt on demand. `bundle.resources` already globs
`binaries/<platform>/*`, so a release bundle picks it up automatically; the Tauri
backend resolves it via the resource path (falling back to `dotnet <dll>` in dev).
Run this before packaging a release (TODO: call it from the per-platform release
scripts).

