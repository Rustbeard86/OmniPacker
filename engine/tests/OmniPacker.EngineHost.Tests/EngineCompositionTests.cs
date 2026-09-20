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
    public async Task Fresh_data_dir_starts_disconnected_with_no_accounts()
    {
        using var temp = new TempData();
        await using var engine = EngineServices.Create(temp.Dir);

        Assert.Equal(SteamForge.Engine.Steam.SteamAuthState.Disconnected, engine.Session.Status.State);
        Assert.Empty(engine.Session.StoredAccounts);
    }
}
