using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Logging;
using SteamForge.Engine.Content;
using SteamForge.Engine.Jobs;
using SteamForge.Engine.Logging;
using SteamForge.Engine.Settings;
using SteamForge.Engine.Steam;

namespace OmniPacker.EngineHost.Engine;

/// <summary>
/// Composition root for the vendored SteamKit2 engine, wired for OmniPacker's
/// daemon. Deliberately registers ONLY the Steam core (session, content client,
/// account router, token store, log broadcaster, JobStore catalog) and does NOT
/// start any hosted service - the daemon decides when to connect, so launching
/// the process never talks to Steam on its own. `AddSteamForgeEngine` (the web
/// build) is intentionally not used.
/// </summary>
public sealed class EngineServices : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private EngineServices(ServiceProvider provider) => _provider = provider;

    public ILogBroadcaster Log => _provider.GetRequiredService<ILogBroadcaster>();
    public SteamTokenStore TokenStore => _provider.GetRequiredService<SteamTokenStore>();
    public SteamSessionManager Session => _provider.GetRequiredService<SteamSessionManager>();
    public SteamContentClient Content => _provider.GetRequiredService<SteamContentClient>();
    public AccountRouter Router => _provider.GetRequiredService<AccountRouter>();
    public JobStore Store => _provider.GetRequiredService<JobStore>();

    /// <summary>Build the engine service graph rooted at <paramref name="dataDir"/>.</summary>
    public static EngineServices Create(string dataDir)
    {
        Directory.CreateDirectory(dataDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddSingleton(Options.Create(new SteamOptions { DataDirectory = dataDir }));

        var paths = new StoragePaths(
            DataDir: dataDir,
            DatabasePath: Path.Combine(dataDir, "jobs.sqlite3"),
            WorkRoot: Path.Combine(dataDir, "work"),
            StagingRoot: Path.Combine(dataDir, "staging"),
            ArchiveRoot: Path.Combine(dataDir, "archives"));
        services.AddSingleton(paths);

        services.AddSingleton<ILogBroadcaster, LogBroadcaster>();
        services.AddSingleton(new SteamTokenStore(dataDir));
        services.AddSingleton<RuntimeSettingsStore>();
        services.AddSingleton(sp => new JobStore(sp.GetRequiredService<StoragePaths>().DatabasePath));
        services.AddSingleton<SteamSessionManager>();
        services.AddSingleton<SteamContentClient>();
        services.AddSingleton<AccountRouter>();

        return new EngineServices(services.BuildServiceProvider());
    }

    // The Steam session is IAsyncDisposable, so the provider must be disposed async.
    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
