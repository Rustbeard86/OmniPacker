using Microsoft.Extensions.Hosting;
using SteamForge.Abstractions.Logging;

namespace SteamForge.Engine.Steam;

/// <summary>
/// Starts the SteamKit callback pump at host startup and attempts a silent
/// login from a stored refresh token, so a previously authorized service comes
/// back online without operator interaction. If there is no valid token, the
/// session stays disconnected and the admin can start a QR login from the UI.
/// </summary>
internal sealed class SteamStartupService : IHostedService
{
    private readonly SteamSessionManager _session;
    private readonly ILogBroadcaster _log;

    public SteamStartupService(SteamSessionManager session, ILogBroadcaster log)
    {
        _session = session;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _session.StartPump();
        _log.Info("engine", "SteamForge engine started");

        // Resume off the request path so startup is not blocked by Steam I/O.
        _ = Task.Run(async () =>
        {
            try
            {
                await _session.TryResumeAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Warn("steam", $"Startup resume error: {ex.Message}");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _session.DisposeAsync();
    }
}
