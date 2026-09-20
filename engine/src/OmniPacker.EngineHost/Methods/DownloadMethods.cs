using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OmniPacker.EngineHost.Engine;
using OmniPacker.EngineHost.Protocol;

namespace OmniPacker.EngineHost.Methods;

/// <summary>
/// Game downloads via the engine's SteamKit2 content client. `download.start`
/// fires the (long) download on a background task and returns a job id; progress
/// streams as `download.progress` events, ending in `download.done` or
/// `download.failed`. `download.cancel` cancels an in-flight job.
///
/// Parity with the DepotDownloader sidecar is being built up: branch, OS, and
/// DLC-depot association work today; branch passwords, os-arch/language filters,
/// and validate come next (see docs/ARCHITECTURE-DISCUSSION.md E3).
/// </summary>
public static class DownloadMethods
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Jobs = new();

    public static void Register(Dispatcher dispatcher, EngineServices engine, IEventSink sink)
    {
        dispatcher.Register("download.start", (p, ct) =>
        {
            var appId = MetadataMethods.RequireAppId(p);
            var branch = MetadataMethods.OptionalString(p, "branch") ?? "public";
            var os = MetadataMethods.OptionalString(p, "os") ?? "windows";
            var dest = MetadataMethods.OptionalString(p, "dest")
                ?? Path.Combine(engine.DataDir, "downloads", $"{appId}-{branch}-{os}");
            MetadataMethods.RequireLoggedOn(engine);

            var jobId = Guid.NewGuid().ToString("N");
            var cts = new CancellationTokenSource();
            Jobs[jobId] = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    void Progress(int percent, string message) =>
                        _ = sink.EmitAsync(new EventMessage("download.progress", new JsonObject
                        {
                            ["jobId"] = jobId,
                            ["percent"] = percent,
                            ["message"] = message,
                        }));

                    var result = await engine.Content.DownloadGameAsync(appId, dest, branch, os, Progress, cts.Token);

                    _ = sink.EmitAsync(new EventMessage("download.done", new JsonObject
                    {
                        ["jobId"] = jobId,
                        ["appId"] = result.AppId,
                        ["directory"] = result.Directory,
                        ["fileCount"] = result.FileCount,
                        ["totalBytes"] = result.TotalBytes,
                    }));
                }
                catch (OperationCanceledException)
                {
                    _ = sink.EmitAsync(new EventMessage("download.failed", new JsonObject
                    {
                        ["jobId"] = jobId,
                        ["cancelled"] = true,
                        ["error"] = "cancelled",
                    }));
                }
                catch (Exception ex)
                {
                    _ = sink.EmitAsync(new EventMessage("download.failed", new JsonObject
                    {
                        ["jobId"] = jobId,
                        ["error"] = ex.Message,
                    }));
                }
                finally
                {
                    Jobs.TryRemove(jobId, out _);
                    cts.Dispose();
                }
            });

            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["jobId"] = jobId,
                ["started"] = true,
                ["dest"] = dest,
            });
        });

        dispatcher.Register("download.cancel", (p, ct) =>
        {
            var jobId = (p?["jobId"] as JsonValue)?.GetValue<string>();
            if (string.IsNullOrEmpty(jobId))
                throw RpcException.BadRequest("Missing required param 'jobId'.");

            var found = Jobs.TryGetValue(jobId, out var cts);
            if (found)
                cts!.Cancel();
            return Task.FromResult<JsonNode?>(new JsonObject { ["cancelled"] = found });
        });
    }
}
