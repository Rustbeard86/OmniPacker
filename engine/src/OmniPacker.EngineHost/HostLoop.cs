using OmniPacker.EngineHost.Protocol;
using OmniPacker.EngineHost.Transport;

namespace OmniPacker.EngineHost;

/// <summary>Lets handlers and engine hooks push unsolicited events to the host.</summary>
public interface IEventSink
{
    Task EmitAsync(EventMessage evt, CancellationToken cancellationToken = default);
}

/// <summary>
/// The read/dispatch/write loop. Reads request lines from the transport,
/// dispatches them, and writes responses; also serves as the event sink so
/// handlers can emit events on the same stream. Malformed lines produce an
/// `error` event and do not break the loop. `RequestStop` lets the `shutdown`
/// handler end the loop after its response is written.
/// </summary>
public sealed class HostLoop(IDuplexTransport transport, Dispatcher dispatcher) : IEventSink
{
    private readonly CancellationTokenSource _stop = new();

    public void RequestStop() => _stop.Cancel();

    public Task EmitAsync(EventMessage evt, CancellationToken cancellationToken = default) =>
        transport.WriteLineAsync(Codec.Encode(evt), cancellationToken);

    /// <summary>Run until the inbound stream ends or a stop is requested.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var ct = linked.Token;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await transport.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
                break; // end of stream

            if (string.IsNullOrWhiteSpace(line))
                continue;

            Message message;
            try
            {
                message = Codec.Parse(line);
            }
            catch (ProtocolException ex)
            {
                await EmitAsync(new EventMessage("error",
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["code"] = RpcError.BadRequest,
                        ["message"] = ex.Message,
                    }), CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (message is not RequestMessage request)
                // The host only receives requests; ignore stray responses/events.
                continue;

            var response = await dispatcher.HandleAsync(request, ct).ConfigureAwait(false);
            await transport.WriteLineAsync(Codec.Encode(response), CancellationToken.None).ConfigureAwait(false);
        }
    }
}
