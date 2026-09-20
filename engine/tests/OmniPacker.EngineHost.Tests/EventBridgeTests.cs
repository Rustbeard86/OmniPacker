using OmniPacker.EngineHost;
using OmniPacker.EngineHost.Engine;
using OmniPacker.EngineHost.Protocol;
using SteamForge.Abstractions.Logging;

namespace OmniPacker.EngineHost.Tests;

public class EventBridgeTests
{
    private sealed class CapturingSink : IEventSink
    {
        public List<EventMessage> Events { get; } = [];

        public Task EmitAsync(EventMessage evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Forwards_broadcast_log_lines_as_log_events()
    {
        using var temp = new TempData();
        await using var engine = EngineServices.Create(temp.Dir);
        var sink = new CapturingSink();
        EventBridge.Attach(engine, sink);

        engine.Log.Warn("scanner", "rate limited, backing off");

        var evt = Assert.Single(sink.Events, e => e.Event == "log");
        Assert.Equal("warning", evt.Data!["level"]!.GetValue<string>());
        Assert.Equal("scanner", evt.Data!["source"]!.GetValue<string>());
        Assert.Equal("rate limited, backing off", evt.Data!["message"]!.GetValue<string>());
    }

    [Fact]
    public void AuthStatus_payload_shape_matches_the_fixture()
    {
        var evt = Assert.IsType<EventMessage>(Codec.Parse(Fixtures.ReadLine("evt_auth_status.json")));
        Assert.Equal("auth.status", evt.Event);
        Assert.Equal("Disconnected", evt.Data!["state"]!.GetValue<string>());
        // Optional fields present as JSON null.
        Assert.True(evt.Data!.AsObject().ContainsKey("qrChallengeUrl"));
    }

    [Fact]
    public void AuthStatus_payload_builder_produces_the_contract_fields()
    {
        var status = new SteamForge.Engine.Steam.SteamAuthStatus(
            SteamForge.Engine.Steam.SteamAuthState.LoggedOn, AccountName: "archiver");
        var payload = EventBridge.AuthStatusPayload(status);
        Assert.Equal("LoggedOn", payload["state"]!.GetValue<string>());
        Assert.Equal("archiver", payload["accountName"]!.GetValue<string>());
    }
}
