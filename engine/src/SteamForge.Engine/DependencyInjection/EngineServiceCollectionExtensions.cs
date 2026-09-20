using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Logging;
using SteamForge.Engine.Jobs;
using SteamForge.Engine.Logging;
using SteamForge.Engine.Steam;

namespace SteamForge.Engine.DependencyInjection;

public static class EngineServiceCollectionExtensions
{
    /// <summary>
    /// Register the SteamForge engine: log broadcaster, Steam session manager,
    /// token store, and the startup service that runs the callback pump and
    /// attempts a silent token resume.
    /// </summary>
    public static IServiceCollection AddSteamForgeEngine(this IServiceCollection services)
    {
        services.AddOptions<SteamOptions>()
            .BindConfiguration(SteamOptions.SectionName);

        services.AddSingleton<ILogBroadcaster, LogBroadcaster>();
        // Durable on-disk sink for error lines (survives rebuilds/restarts; /data volume).
        services.AddHostedService<PersistentErrorLog>();

        services.AddSingleton<SteamTokenStore>(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var options = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<SteamOptions>>().Value;
            var dataDir = Path.IsPathRooted(options.DataDirectory)
                ? options.DataDirectory
                : Path.Combine(env.ContentRootPath, options.DataDirectory);
            return new SteamTokenStore(dataDir);
        });

        services.AddSingleton<SteamSessionManager>();
        services.AddSingleton<AccountRouter>();
        services.AddSingleton<SteamForge.Engine.Content.SteamContentClient>();
        services.AddHostedService<SteamStartupService>();

        // Job queue + storage + dedupe history.
        services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName);

        services.AddSingleton(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var dataDir = Path.IsPathRooted(options.DataDirectory)
                ? options.DataDirectory
                : Path.Combine(env.ContentRootPath, options.DataDirectory);
            return new StoragePaths(
                dataDir,
                Path.Combine(dataDir, options.DatabaseFileName),
                Path.Combine(dataDir, options.WorkDirectoryName),
                Path.Combine(dataDir, "staging"),
                Path.Combine(dataDir, "archives"));
        });

        services.AddSingleton(sp => new JobStore(sp.GetRequiredService<StoragePaths>().DatabasePath));
        services.AddSingleton<SteamForge.Engine.Settings.RuntimeSettingsStore>();
        services.AddSingleton<StorageMonitor>();
        services.AddSingleton<JobNotifier>();
        services.AddSingleton<SteamForge.Engine.Security.IpRateLimiter>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<LibraryRescanService>();
        services.AddSingleton<PipelineRunner>();
        services.AddHostedService<JobQueueWorker>();
        services.AddHostedService<ExpiryPruner>();
        services.AddHostedService<OwnershipScanner>();
        services.AddHostedService<PicsChangeWatcher>();

        return services;
    }
}
