using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Plugins;
using SteamForge.Engine.Content;
using SteamForge.Engine.Steam;

namespace SteamForge.Plugins.Game;

/// <summary>Game download configuration, bound from the "Game" config section.</summary>
public sealed class GameOptions
{
    public const string SectionName = "Game";

    /// <summary>
    /// Target OS for depot selection. Defaults to "windows" - the redistribution
    /// use case wants Windows builds even when the host VPS is Linux. Depots with
    /// no oslist (shared content) are always included.
    /// </summary>
    public string TargetOs { get; set; } = "windows";

    /// <summary>App branch to download. "public" is the default live build.</summary>
    public string Branch { get; set; } = "public";
}

/// <summary>
/// Content-source plugin for full Steam games. Resolves an app's depots/manifests
/// via PICS and downloads them through the engine's SteamKit2 content client -
/// the same chunk path the workshop source rides. Uses the CSF naming the
/// pipeline applies to <see cref="ContentKind.Game"/>.
/// </summary>
public sealed class GameContentSource : IContentSource
{
    private const string LogSource = "game";

    private readonly SteamContentClient _content;
    private readonly AccountRouter _router;
    private readonly GameOptions _options;

    public GameContentSource(SteamContentClient content, AccountRouter router, IOptions<GameOptions> options)
    {
        _content = content;
        _router = router;
        _options = options.Value;
    }

    public string Id => "game";
    public string Name => "Steam game downloader";
    public string Version => "1.0.0";

    public bool CanHandle(DownloadRequest request) =>
        request.Kind == ContentKind.Game && request.AppId != 0;

    private string BranchFor(DownloadRequest request) =>
        string.IsNullOrWhiteSpace(request.Branch) ? _options.Branch : request.Branch;

    private string OsFor(DownloadRequest request) =>
        string.IsNullOrWhiteSpace(request.TargetOs) ? _options.TargetOs : request.TargetOs.Trim().ToLowerInvariant();

    public async Task<ContentIdentity> ResolveAsync(DownloadRequest request, IPipelineContext context)
    {
        // Make sure the active session is an account that owns this app (swaps if
        // needed) - PICS access tokens and depot keys require ownership.
        await _router.EnsureOwnerAsync(request.AppId, context.CancellationToken);

        var info = await _content.ResolveGameAsync(
            request.AppId, BranchFor(request), OsFor(request), context.CancellationToken);

        // Dedupe on (app, build): identical build id == identical content. The
        // depot id slot carries the app id since a game spans many depots.
        return new ContentIdentity(info.AppId, info.AppId, info.BuildId, info.Name);
    }

    public async Task<ContentResult> DownloadAsync(DownloadRequest request, IPipelineContext context)
    {
        // Persistent, content-keyed staging so an interrupted download resumes.
        var outputDir = context.StagingDirectory;
        var branch = BranchFor(request);
        var os = OsFor(request);
        context.Reporter.Info(LogSource, $"Downloading game {request.AppId} (OS {os}, branch {branch})");

        // Route through an account that can actually download this app: the catalog can
        // list it as owned via a free-weekend/promo/family license that grants no
        // depot-key entitlement, so on AccessDenied the router rotates to the next owner.
        var result = await _router.RunWithOwnerAsync(
            request.AppId,
            ct => _content.DownloadGameAsync(
                request.AppId,
                outputDir,
                branch,
                os,
                (percent, message) => context.Reporter.Progress(percent, message),
                ct),
            context.CancellationToken);

        return new ContentResult(
            result.Directory, result.AppId, result.DepotId, result.ManifestId,
            result.FileCount, result.TotalBytes);
    }
}

public static class GameServiceCollectionExtensions
{
    /// <summary>Register the game content-source plugin.</summary>
    public static IServiceCollection AddGameContentSource(this IServiceCollection services)
    {
        services.AddOptions<GameOptions>().BindConfiguration(GameOptions.SectionName);
        services.AddSingleton<GameContentSource>();
        services.AddSingleton<IContentSource>(sp => sp.GetRequiredService<GameContentSource>());
        return services;
    }
}
