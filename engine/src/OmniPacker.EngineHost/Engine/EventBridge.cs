using System.Text.Json.Nodes;
using SteamForge.Abstractions.Logging;
using SteamForge.Engine.Steam;

namespace OmniPacker.EngineHost.Engine;

/// <summary>
/// Forwards engine notifications to the IPC event stream: every broadcast log
/// line becomes a `log` event, and every Steam auth state change an `auth.status`
/// event. The payload builders are public so tests and fixtures share one shape.
/// </summary>
public static class EventBridge
{
    /// <summary>`log` event data. Level is lowercase severity (debug/info/warning/error).</summary>
    public static JsonObject LogPayload(LogEntry e) => new()
    {
        ["ts"] = e.Timestamp.ToString("o"),
        ["level"] = e.Level.ToString().ToLowerInvariant(),
        ["source"] = e.Source,
        ["message"] = e.Message,
    };

    /// <summary>`auth.status` event / `auth.status` result data. State is the SteamAuthState name.</summary>
    public static JsonObject AuthStatusPayload(SteamAuthStatus s) => new()
    {
        ["state"] = s.State.ToString(),
        ["accountName"] = s.AccountName,
        ["qrChallengeUrl"] = s.QrChallengeUrl,
        ["prompt"] = s.Prompt,
        ["error"] = s.Error,
    };

    /// <summary>Subscribe the sink to engine log + auth-status notifications.</summary>
    public static void Attach(EngineServices engine, IEventSink sink)
    {
        engine.Log.Published += entry =>
            _ = sink.EmitAsync(new Protocol.EventMessage("log", LogPayload(entry)));

        engine.Session.StatusChanged += status =>
            _ = sink.EmitAsync(new Protocol.EventMessage("auth.status", AuthStatusPayload(status)));
    }
}
