using OmniPacker.EngineHost.Engine;

namespace OmniPacker.EngineHost.Tests;

public class EngineCompositionTests
{
    [Fact]
    public async Task Resolves_the_steam_core_service_graph()
    {
        using var temp = new TempData();
        await using var engine = EngineServices.Create(temp.Dir);

        // Resolving each proves the vendored engine graph is wireable in our host
        // (no missing dependency), and guards against a future vendored update
        // breaking construction. None of this connects to Steam.
        Assert.NotNull(engine.Log);
        Assert.NotNull(engine.TokenStore);
        Assert.NotNull(engine.Session);
        Assert.NotNull(engine.Content);
        Assert.NotNull(engine.Router);
        Assert.NotNull(engine.Store);
    }

    [Fact]
    public void Default_data_dir_is_absolute_per_user_and_outside_the_repo()
    {
        var dir = EngineServices.DefaultDataDir();
        Assert.True(Path.IsPathRooted(dir), "default data dir must be absolute");
        Assert.EndsWith(Path.Combine("OmniPacker", "engine"), dir);
        // Must not live under this repository (tokens must never reach the repo).
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        Assert.False(Path.GetFullPath(dir).StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase),
            $"default data dir {dir} must not be inside the repo {repoRoot}");
    }

    [Fact]
    public async Task Fresh_data_dir_starts_disconnected_with_no_accounts()
    {
        using var temp = new TempData();
        await using var engine = EngineServices.Create(temp.Dir);

        Assert.Equal(SteamForge.Engine.Steam.SteamAuthState.Disconnected, engine.Session.Status.State);
        Assert.Empty(engine.Session.StoredAccounts);
    }
}
