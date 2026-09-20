using System.Text.Json.Nodes;
using OmniPacker.EngineHost.Protocol;

namespace OmniPacker.EngineHost.Methods;

/// <summary>
/// v1 methods that need no Steam session: `ping`, `hello`, `shutdown`. Steam
/// auth/library/download methods land in their own registrars as they are built.
/// </summary>
public static class CoreMethods
{
    /// <summary>Envelope protocol version. Bumped only for a breaking envelope change.</summary>
    public const int Protocol = 1;

    public const string HostName = "OmniPacker.EngineHost";

    /// <summary>Version of the vendored SteamForge engine assembly - also proves it is linked.</summary>
    public static string EngineVersion =>
        typeof(SteamForge.Engine.Steam.SteamSessionManager).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>Payload shared by the `hello` result and the `ready` event.</summary>
    public static JsonObject BuildHello(Dispatcher dispatcher) => new()
    {
        ["protocol"] = Protocol,
        ["engine"] = EngineVersion,
        ["host"] = HostName,
        ["capabilities"] = new JsonArray(dispatcher.Capabilities.Select(c => (JsonNode)c!).ToArray()),
    };

    public static void Register(Dispatcher dispatcher, HostLoop loop)
    {
        dispatcher.Register("ping", (@params, _) =>
        {
            var result = new JsonObject
            {
                ["pong"] = true,
                ["nonce"] = @params?["nonce"]?.DeepClone(),
            };
            return Task.FromResult<JsonNode?>(result);
        });

        dispatcher.Register("hello", (_, _) =>
            Task.FromResult<JsonNode?>(BuildHello(dispatcher)));

        dispatcher.Register("shutdown", (_, _) =>
        {
            loop.RequestStop();
            return Task.FromResult<JsonNode?>(new JsonObject { ["stopping"] = true });
        });
    }
}
