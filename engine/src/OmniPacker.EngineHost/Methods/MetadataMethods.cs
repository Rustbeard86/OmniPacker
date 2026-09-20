using System.Text.Json.Nodes;
using OmniPacker.EngineHost.Engine;
using OmniPacker.EngineHost.Protocol;

namespace OmniPacker.EngineHost.Methods;

/// <summary>
/// App metadata (branches + the per-branch/OS depot plan). Both need a logged-on
/// session; the interactive success path is exercised by manual/live runs, but
/// the not-logged-on guard IS hermetic.
/// </summary>
public static class MetadataMethods
{
    public static void Register(Dispatcher dispatcher, EngineServices engine)
    {
        // Branches for an app: name, current build id, whether a password is needed,
        // plus supported OSes - what a branch/OS picker needs.
        dispatcher.Register("app.branches", async (p, ct) =>
        {
            var appId = RequireAppId(p);
            RequireLoggedOn(engine);
            using var timeout = TimeBox(ct, TimeSpan.FromSeconds(60));

            var b = await engine.Content.ListBranchesAsync(appId, timeout.Token);
            var branches = new JsonArray();
            foreach (var branch in b.Branches)
            {
                branches.Add(new JsonObject
                {
                    ["name"] = branch.Name,
                    ["buildId"] = branch.BuildId,
                    ["passwordRequired"] = branch.PasswordRequired,
                });
            }
            return new JsonObject
            {
                ["appId"] = b.AppId,
                ["name"] = b.Name,
                ["branches"] = branches,
                ["supportedOs"] = new JsonArray(b.SupportedOs.Select(o => (JsonNode)o!).ToArray()),
            };
        });

        // Resolved depot plan for a branch/OS: install dir, build id, and each depot
        // (with DLC association). manifestId is a string to avoid JSON precision loss.
        dispatcher.Register("app.depots", async (p, ct) =>
        {
            var appId = RequireAppId(p);
            var branch = OptionalString(p, "branch") ?? "public";
            var os = OptionalString(p, "os") ?? "windows";
            RequireLoggedOn(engine);
            using var timeout = TimeBox(ct, TimeSpan.FromSeconds(90));

            var info = await engine.Content.ResolveGameAsync(appId, branch, os, timeout.Token);
            var depots = new JsonArray();
            foreach (var d in info.Depots)
            {
                depots.Add(new JsonObject
                {
                    ["depotId"] = d.DepotId,
                    ["manifestId"] = d.ManifestId.ToString(),
                    ["name"] = d.Name,
                    ["isDlc"] = d.IsDlc,
                });
            }
            return new JsonObject
            {
                ["appId"] = info.AppId,
                ["name"] = info.Name,
                ["installDir"] = info.InstallDir,
                ["buildId"] = info.BuildId,
                ["branch"] = branch,
                ["os"] = os,
                ["depots"] = depots,
            };
        });
    }

    internal static uint RequireAppId(JsonObject? p)
    {
        if (p?["appId"] is not JsonValue v)
            throw RpcException.BadRequest("Missing required param 'appId'.");

        // Accept a wire-parsed number, a CLR-backed number, or a numeric string.
        if (v.TryGetValue<uint>(out var u))
            return u;
        if (v.TryGetValue<long>(out var l) && l is >= 0 and <= uint.MaxValue)
            return (uint)l;
        if (v.TryGetValue<int>(out var i) && i >= 0)
            return (uint)i;
        if (v.TryGetValue<string>(out var s) && uint.TryParse(s, out var us))
            return us;

        throw RpcException.BadRequest("Param 'appId' must be a positive integer.");
    }

    internal static string? OptionalString(JsonObject? p, string name) =>
        (p?[name] as JsonValue)?.GetValue<string>();

    internal static void RequireLoggedOn(EngineServices engine)
    {
        if (!engine.Session.IsLoggedOn)
            throw new RpcException(RpcError.Unauthenticated, "Not logged on; sign in first.");
    }

    internal static CancellationTokenSource TimeBox(CancellationToken ct, TimeSpan budget)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);
        return cts;
    }
}
