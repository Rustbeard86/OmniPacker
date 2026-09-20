using System.Text.Json.Nodes;
using OmniPacker.EngineHost;
using OmniPacker.EngineHost.Methods;
using OmniPacker.EngineHost.Protocol;
using OmniPacker.EngineHost.Transport;

namespace OmniPacker.EngineHost.Tests;

public class DispatcherTests
{
    private static (Dispatcher, HostLoop) NewCore()
    {
        var dispatcher = new Dispatcher();
        var loop = new HostLoop(new ChannelTransport(), dispatcher);
        CoreMethods.Register(dispatcher, loop);
        return (dispatcher, loop);
    }

    [Fact]
    public async Task Ping_echoes_nonce()
    {
        var (dispatcher, _) = NewCore();
        var req = new RequestMessage(1, "ping", new JsonObject { ["nonce"] = "xyz" });

        var res = await dispatcher.HandleAsync(req, CancellationToken.None);

        Assert.True(res.Ok);
        Assert.Equal(1UL, res.Id);
        Assert.True(res.Result!["pong"]!.GetValue<bool>());
        Assert.Equal("xyz", res.Result!["nonce"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ping_with_no_params_returns_null_nonce()
    {
        var (dispatcher, _) = NewCore();
        var res = await dispatcher.HandleAsync(new RequestMessage(2, "ping", null), CancellationToken.None);
        Assert.True(res.Ok);
        Assert.Null(res.Result!["nonce"]);
    }

    [Fact]
    public async Task Hello_reports_protocol_and_capabilities()
    {
        var (dispatcher, _) = NewCore();
        var res = await dispatcher.HandleAsync(new RequestMessage(3, "hello", null), CancellationToken.None);

        Assert.True(res.Ok);
        Assert.Equal(CoreMethods.Protocol, res.Result!["protocol"]!.GetValue<int>());
        Assert.Equal(CoreMethods.HostName, res.Result!["host"]!.GetValue<string>());
        var caps = res.Result!["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("ping", caps);
        Assert.Contains("hello", caps);
        Assert.Contains("shutdown", caps);
        // Engine version proves the vendored engine assembly is linked and loadable.
        Assert.False(string.IsNullOrWhiteSpace(res.Result!["engine"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Unknown_method_returns_unknown_method_error()
    {
        var (dispatcher, _) = NewCore();
        var res = await dispatcher.HandleAsync(new RequestMessage(4, "does.not.exist", null), CancellationToken.None);
        Assert.False(res.Ok);
        Assert.Equal(RpcError.UnknownMethod, res.Error!.Code);
    }

    [Fact]
    public async Task Handler_throwing_RpcException_maps_to_error_code()
    {
        var dispatcher = new Dispatcher();
        dispatcher.Register("boom", (_, _) => throw RpcException.Transient("cdn blip"));
        var res = await dispatcher.HandleAsync(new RequestMessage(5, "boom", null), CancellationToken.None);
        Assert.False(res.Ok);
        Assert.Equal(RpcError.Transient, res.Error!.Code);
        Assert.True(res.Error.Retriable);
    }

    [Fact]
    public async Task Handler_throwing_unexpected_maps_to_internal()
    {
        var dispatcher = new Dispatcher();
        dispatcher.Register("boom", (_, _) => throw new InvalidOperationException("kaboom"));
        var res = await dispatcher.HandleAsync(new RequestMessage(6, "boom", null), CancellationToken.None);
        Assert.False(res.Ok);
        Assert.Equal(RpcError.Internal, res.Error!.Code);
    }
}
