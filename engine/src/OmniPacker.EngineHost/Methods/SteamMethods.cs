using System.Text.Json.Nodes;
using OmniPacker.EngineHost.Engine;
using OmniPacker.EngineHost.Protocol;

namespace OmniPacker.EngineHost.Methods;

/// <summary>
/// Steam methods that read engine state WITHOUT a live connection: `auth.status`
/// and `account.list`. Interactive login, enumeration and download methods (which
/// require a Steam connection) land here as they are built and integration-tested.
/// </summary>
public static class SteamMethods
{
    public static void Register(Dispatcher dispatcher, EngineServices engine)
    {
        // Current login state. Disconnected until an auth method connects.
        dispatcher.Register("auth.status", (_, _) =>
            Task.FromResult<JsonNode?>(EventBridge.AuthStatusPayload(engine.Session.Status)));

        // Accounts with a stored refresh token (durable logins), from the token store.
        dispatcher.Register("account.list", (_, _) =>
        {
            var accounts = new JsonArray();
            foreach (var token in engine.Session.StoredAccounts)
            {
                accounts.Add(new JsonObject
                {
                    ["account"] = token.AccountName,
                    ["obtainedAt"] = token.ObtainedAt.ToString("o"),
                });
            }
            return Task.FromResult<JsonNode?>(new JsonObject { ["accounts"] = accounts });
        });

        // Switch the active session to another stored account (silent, via its
        // durable token). The invisible multi-account rotation behind the app list.
        dispatcher.Register("account.switch", async (p, ct) =>
        {
            var account = (p?["account"] as JsonValue)?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(account))
                throw RpcException.BadRequest("Missing required param 'account'.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            engine.Session.StartPump();
            var switched = await engine.Session.SwitchToAccountAsync(account, timeout.Token);
            return new JsonObject
            {
                ["switched"] = switched,
                ["status"] = EventBridge.AuthStatusPayload(engine.Session.Status),
            };
        });

        // --- Live methods (connect to Steam; exercised by manual/integration runs,
        // not CI). Each is time-boxed so a hung Steam call cannot wedge the loop. ---

        // Silent login from a stored refresh token (no QR/password prompt).
        dispatcher.Register("auth.resume", async (_, ct) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            engine.Session.StartPump();
            var resumed = await engine.Session.TryResumeAsync(timeout.Token);
            return new JsonObject
            {
                ["resumed"] = resumed,
                ["status"] = EventBridge.AuthStatusPayload(engine.Session.Status),
            };
        });

        // Enumerate the logged-on account's owned apps and persist them to the
        // ownership catalog. Returns a count + a small sample (no full dump).
        dispatcher.Register("library.enumerate", async (_, ct) =>
        {
            if (!engine.Session.IsLoggedOn)
                throw RpcException.Unauthenticated("Not logged on; call auth.resume or an auth login method first.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));

            var owned = await engine.Content.EnumerateOwnedAppsAsync(timeout.Token);
            var account = engine.Session.AccountName ?? "unknown";
            engine.Store.ReplaceOwnership(account, owned);

            var sample = new JsonArray();
            foreach (var app in owned.Take(5))
                sample.Add(new JsonObject { ["appId"] = app.AppId, ["name"] = app.Name });

            return new JsonObject
            {
                ["account"] = account,
                ["count"] = owned.Count,
                ["sample"] = sample,
            };
        });
    }
}
