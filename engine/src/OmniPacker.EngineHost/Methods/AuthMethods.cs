using System.Text.Json.Nodes;
using OmniPacker.EngineHost.Engine;
using OmniPacker.EngineHost.Protocol;

namespace OmniPacker.EngineHost.Methods;

/// <summary>
/// Interactive login methods. Login flows are long-running (QR polling, guard
/// wait), so `begin*` fire the flow on a background task and return immediately;
/// the URL, guard prompt, and final logged-on/failed state all arrive as
/// `auth.status` events (see EventBridge). `auth.submitGuard` feeds a code into
/// an in-progress flow. Interactive success needs a real Steam round-trip, so it
/// is covered by manual/scan runs, not CI - but param validation IS hermetic.
/// </summary>
public static class AuthMethods
{
    public static void Register(Dispatcher dispatcher, EngineServices engine)
    {
        // QR login: watch for the AwaitingQrScan auth.status event carrying the
        // challenge URL, render it, and scan with the Steam mobile app.
        dispatcher.Register("auth.beginQr", (p, ct) =>
        {
            engine.Session.StartPump();
            _ = Task.Run(() => engine.Session.StartQrLoginAsync(CancellationToken.None));
            return Task.FromResult<JsonNode?>(new JsonObject { ["started"] = true });
        });

        // Username/password login. May transition to AwaitingGuardCode (an
        // auth.status event); answer it with auth.submitGuard.
        dispatcher.Register("auth.beginCredentials", (p, ct) =>
        {
            var username = RequireString(p, "username");
            var password = RequireString(p, "password");
            engine.Session.StartPump();
            _ = Task.Run(() => engine.Session.StartCredentialLoginAsync(username, password, CancellationToken.None));
            return Task.FromResult<JsonNode?>(new JsonObject { ["started"] = true });
        });

        // Deliver a Steam Guard code to the in-progress login. `accepted` is false
        // if no login is currently waiting for a code.
        dispatcher.Register("auth.submitGuard", (p, ct) =>
        {
            var code = RequireString(p, "code");
            var accepted = engine.Session.SubmitGuardCode(code);
            return Task.FromResult<JsonNode?>(new JsonObject { ["accepted"] = accepted });
        });

        // Log off the active session (the stored token is kept for later resume).
        dispatcher.Register("auth.logout", async (p, ct) =>
        {
            await engine.Session.LogoutAsync();
            return new JsonObject { ["loggedOut"] = true };
        });
    }

    private static string RequireString(JsonObject? @params, string name)
    {
        var value = (@params?[name] as JsonValue)?.GetValue<string>();
        if (string.IsNullOrEmpty(value))
            throw RpcException.BadRequest($"Missing required string param '{name}'.");
        return value;
    }
}
