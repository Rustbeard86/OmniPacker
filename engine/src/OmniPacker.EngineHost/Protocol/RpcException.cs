namespace OmniPacker.EngineHost.Protocol;

/// <summary>
/// Thrown by a method handler to return a structured error response. The
/// dispatcher turns this into a failed ResponseMessage with the carried code.
/// </summary>
public sealed class RpcException(string code, string message, bool retriable = false)
    : Exception(message)
{
    public RpcError Error { get; } = new(code, message, retriable);

    public static RpcException BadRequest(string message) => new(RpcError.BadRequest, message);
    public static RpcException Unauthenticated(string message) => new(RpcError.Unauthenticated, message);
    public static RpcException Transient(string message) => new(RpcError.Transient, message, retriable: true);
}
