using System.Text.Json.Nodes;

namespace OmniPacker.EngineHost.Protocol;

/// <summary>
/// A single IPC message. See docs/IPC-CONTRACT.md. The envelope is frozen at v1;
/// method/event payloads (Params/Result/Data) are arbitrary JSON so they can grow
/// without an envelope change.
/// </summary>
public abstract record Message;

/// <summary>Host -> engine call. Params is null or a JSON object.</summary>
public sealed record RequestMessage(ulong Id, string Method, JsonObject? Params) : Message;

/// <summary>Engine -> host reply, one per request Id. Exactly one of Result/Error is set.</summary>
public sealed record ResponseMessage(ulong Id, bool Ok, JsonNode? Result, RpcError? Error) : Message
{
    public static ResponseMessage Success(ulong id, JsonNode? result) => new(id, true, result, null);
    public static ResponseMessage Failure(ulong id, RpcError error) => new(id, false, null, error);
}

/// <summary>Engine -> host unsolicited event (no id).</summary>
public sealed record EventMessage(string Event, JsonNode? Data) : Message;

/// <summary>Structured error payload for a failed response.</summary>
public sealed record RpcError(string Code, string Message, bool Retriable)
{
    // Stable error codes (see docs/IPC-CONTRACT.md).
    public const string BadRequest = "bad_request";
    public const string UnknownMethod = "unknown_method";
    public const string Unauthenticated = "unauthenticated";
    public const string NoOwningAccount = "no_owning_account";
    public const string DepotAccessDenied = "depot_access_denied";
    public const string Transient = "transient";
    public const string Cancelled = "cancelled";
    public const string Internal = "internal";

    public static RpcError Of(string code, string message, bool retriable = false) =>
        new(code, message, retriable);
}
