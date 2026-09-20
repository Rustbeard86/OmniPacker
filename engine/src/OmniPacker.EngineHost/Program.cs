using OmniPacker.EngineHost;
using OmniPacker.EngineHost.Engine;
using OmniPacker.EngineHost.Methods;
using OmniPacker.EngineHost.Protocol;
using OmniPacker.EngineHost.Transport;

// OmniPacker engine daemon: line-JSON IPC over stdio (see docs/IPC-CONTRACT.md).
// The Rust/Tauri host spawns this process and drives it; stdout carries protocol
// messages, stderr is human diagnostics only. Launching the daemon does NOT
// connect to Steam - that happens only when an auth method is called.

var dataDir = Environment.GetEnvironmentVariable("OMNIPACKER_ENGINE_DATA")
    ?? EngineServices.DefaultDataDir();

var transport = new StdioTransport();
var dispatcher = new Dispatcher();
var loop = new HostLoop(transport, dispatcher);

CoreMethods.Register(dispatcher, loop);

await using var engine = EngineServices.Create(dataDir);
EventBridge.Attach(engine, loop);
SteamMethods.Register(dispatcher, engine);

// Announce readiness (capabilities + versions) before processing any request.
await loop.EmitAsync(new EventMessage("ready", CoreMethods.BuildHello(dispatcher)));

await loop.RunAsync();
return 0;
