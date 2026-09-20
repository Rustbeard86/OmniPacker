using OmniPacker.EngineHost;
using OmniPacker.EngineHost.Methods;
using OmniPacker.EngineHost.Protocol;
using OmniPacker.EngineHost.Transport;

namespace OmniPacker.EngineHost.Tests;

public class HostLoopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static (HostLoop loop, Dispatcher dispatcher, ChannelTransport transport) NewHost()
    {
        var transport = new ChannelTransport();
        var dispatcher = new Dispatcher();
        var loop = new HostLoop(transport, dispatcher);
        CoreMethods.Register(dispatcher, loop);
        return (loop, dispatcher, transport);
    }

    private static async Task RunWithTimeout(Func<Task> body)
    {
        var task = body();
        var done = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(done == task, "host loop did not complete within timeout");
        await task; // observe exceptions
    }

    [Fact]
    public async Task Handles_a_ping_and_writes_a_pong()
    {
        var (loop, _, transport) = NewHost();
        transport.EnqueueInbound(Fixtures.ReadLine("req_ping.json"));
        transport.CompleteInbound();

        await RunWithTimeout(() => loop.RunAsync());

        Assert.True(transport.TryReadOutbound(out var line));
        var res = Assert.IsType<ResponseMessage>(Codec.Parse(line!));
        Assert.Equal(1UL, res.Id);
        Assert.True(res.Ok);
        Assert.Equal("abc123", res.Result!["nonce"]!.GetValue<string>());
    }

    [Fact]
    public async Task Malformed_line_yields_error_event_then_keeps_serving()
    {
        var (loop, _, transport) = NewHost();
        transport.EnqueueInbound("this is not json");
        transport.EnqueueInbound(Fixtures.ReadLine("req_ping.json"));
        transport.CompleteInbound();

        await RunWithTimeout(() => loop.RunAsync());

        Assert.True(transport.TryReadOutbound(out var first));
        var errEvt = Assert.IsType<EventMessage>(Codec.Parse(first!));
        Assert.Equal("error", errEvt.Event);

        Assert.True(transport.TryReadOutbound(out var second));
        var res = Assert.IsType<ResponseMessage>(Codec.Parse(second!));
        Assert.True(res.Ok);
    }

    [Fact]
    public async Task Shutdown_request_stops_the_loop_even_without_stream_end()
    {
        var (loop, _, transport) = NewHost();
        // Note: we do NOT complete the inbound stream; the loop must stop itself
        // when the shutdown handler calls RequestStop.
        transport.EnqueueInbound(Fixtures.ReadLine("req_shutdown.json"));

        await RunWithTimeout(() => loop.RunAsync());

        Assert.True(transport.TryReadOutbound(out var line));
        var res = Assert.IsType<ResponseMessage>(Codec.Parse(line!));
        Assert.True(res.Ok);
        Assert.True(res.Result!["stopping"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Emits_events_through_the_sink()
    {
        var (loop, _, transport) = NewHost();
        await loop.EmitAsync(new EventMessage("ready", CoreMethods.BuildHello(new Dispatcher())));
        Assert.True(transport.TryReadOutbound(out var line));
        var evt = Assert.IsType<EventMessage>(Codec.Parse(line!));
        Assert.Equal("ready", evt.Event);
    }
}
