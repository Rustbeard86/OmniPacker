using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmniPacker.EngineHost.Protocol;

/// <summary>Thrown when a received line is not a valid protocol message.</summary>
public sealed class ProtocolException(string message) : Exception(message);

/// <summary>
/// Serializes/parses the frozen v1 envelope to/from a single JSON line. Kept
/// hand-written (rather than polymorphic attributes) so the exact wire shape is
/// obvious and stays lock-step with the Rust host and the golden fixtures.
/// </summary>
public static class Codec
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        // Compact, single-line output. WriteIndented stays false (default).
        WriteIndented = false,
    };

    /// <summary>Serialize a message to one newline-free JSON line.</summary>
    public static string Encode(Message message)
    {
        JsonObject obj = message switch
        {
            RequestMessage r => new JsonObject
            {
                ["t"] = "req",
                ["id"] = r.Id,
                ["method"] = r.Method,
                ["params"] = r.Params?.DeepClone(),
            },
            ResponseMessage r when r.Ok => new JsonObject
            {
                ["t"] = "res",
                ["id"] = r.Id,
                ["ok"] = true,
                ["result"] = r.Result?.DeepClone(),
            },
            ResponseMessage r => new JsonObject
            {
                ["t"] = "res",
                ["id"] = r.Id,
                ["ok"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = r.Error!.Code,
                    ["message"] = r.Error.Message,
                    ["retriable"] = r.Error.Retriable,
                },
            },
            EventMessage e => new JsonObject
            {
                ["t"] = "evt",
                ["event"] = e.Event,
                ["data"] = e.Data?.DeepClone(),
            },
            _ => throw new ProtocolException($"Unknown message type {message.GetType().Name}"),
        };

        return obj.ToJsonString(LineOptions);
    }

    /// <summary>Parse one JSON line into a typed message. Throws ProtocolException on malformed input.</summary>
    public static Message Parse(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"Invalid JSON: {ex.Message}");
        }

        if (node is not JsonObject obj)
            throw new ProtocolException("Message is not a JSON object");

        var t = (obj["t"] as JsonValue)?.GetValue<string>()
            ?? throw new ProtocolException("Missing discriminator 't'");

        switch (t)
        {
            case "req":
            {
                var id = ReadId(obj);
                var method = (obj["method"] as JsonValue)?.GetValue<string>()
                    ?? throw new ProtocolException("Request missing 'method'");
                var prms = obj["params"];
                if (prms is not null and not JsonObject)
                    throw new ProtocolException("Request 'params' must be an object or null");
                return new RequestMessage(id, method, (JsonObject?)prms?.DeepClone());
            }
            case "res":
            {
                var id = ReadId(obj);
                var ok = (obj["ok"] as JsonValue)?.GetValue<bool>()
                    ?? throw new ProtocolException("Response missing 'ok'");
                if (ok)
                    return ResponseMessage.Success(id, obj["result"]?.DeepClone());

                if (obj["error"] is not JsonObject err)
                    throw new ProtocolException("Failed response missing 'error'");
                var code = (err["code"] as JsonValue)?.GetValue<string>()
                    ?? throw new ProtocolException("Error missing 'code'");
                var msg = (err["message"] as JsonValue)?.GetValue<string>() ?? "";
                var retriable = (err["retriable"] as JsonValue)?.GetValue<bool>() ?? false;
                return ResponseMessage.Failure(id, new RpcError(code, msg, retriable));
            }
            case "evt":
            {
                var evt = (obj["event"] as JsonValue)?.GetValue<string>()
                    ?? throw new ProtocolException("Event missing 'event'");
                return new EventMessage(evt, obj["data"]?.DeepClone());
            }
            default:
                throw new ProtocolException($"Unknown message discriminator '{t}'");
        }
    }

    private static ulong ReadId(JsonObject obj)
    {
        if (obj["id"] is not JsonValue v || !v.TryGetValue<ulong>(out var id))
            throw new ProtocolException("Message missing valid unsigned integer 'id'");
        return id;
    }
}
