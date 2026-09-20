using System.Text.Json.Nodes;
using OmniPacker.EngineHost.Protocol;

namespace OmniPacker.EngineHost;

/// <summary>A method handler: receives params (may be null), returns a result node (may be null).</summary>
public delegate Task<JsonNode?> MethodHandler(JsonObject? @params, CancellationToken cancellationToken);

/// <summary>
/// Registry of RPC methods. Turns a RequestMessage into a ResponseMessage,
/// mapping RpcException to a structured error, an unknown method to
/// `unknown_method`, and any other exception to `internal`.
/// </summary>
public sealed class Dispatcher
{
    private readonly Dictionary<string, MethodHandler> _handlers = new(StringComparer.Ordinal);

    public void Register(string method, MethodHandler handler) => _handlers[method] = handler;

    /// <summary>Registered method names, sorted - reported via `hello`/`ready` capabilities.</summary>
    public IReadOnlyList<string> Capabilities => _handlers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    public async Task<ResponseMessage> HandleAsync(RequestMessage request, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(request.Method, out var handler))
        {
            return ResponseMessage.Failure(request.Id,
                RpcError.Of(RpcError.UnknownMethod, $"No such method '{request.Method}'"));
        }

        try
        {
            var result = await handler(request.Params, cancellationToken).ConfigureAwait(false);
            return ResponseMessage.Success(request.Id, result);
        }
        catch (RpcException ex)
        {
            return ResponseMessage.Failure(request.Id, ex.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ResponseMessage.Failure(request.Id, RpcError.Of(RpcError.Cancelled, "Operation cancelled"));
        }
        catch (Exception ex)
        {
            return ResponseMessage.Failure(request.Id, RpcError.Of(RpcError.Internal, ex.Message));
        }
    }
}
