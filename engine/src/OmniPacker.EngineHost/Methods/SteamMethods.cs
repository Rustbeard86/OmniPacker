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
    }
}
