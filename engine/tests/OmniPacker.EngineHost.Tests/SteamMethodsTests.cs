using OmniPacker.EngineHost;
using OmniPacker.EngineHost.Engine;
using OmniPacker.EngineHost.Methods;
using OmniPacker.EngineHost.Protocol;

namespace OmniPacker.EngineHost.Tests;

public class SteamMethodsTests
{
    [Fact]
    public async Task Auth_status_reports_disconnected_before_login()
    {
        using var temp = new TempData();
        await using var engine = EngineServices.Create(temp.Dir);
        var dispatcher = new Dispatcher();
        SteamMethods.Register(dispatcher, engine);

        var res = await dispatcher.HandleAsync(new RequestMessage(1, "auth.status", null), CancellationToken.None);

        Assert.True(res.Ok);
        Assert.Equal("Disconnected", res.Result!["state"]!.GetValue<string>());
        Assert.Null(res.Result!["accountName"]);
    }

    [Fact]
    public async Task Account_list_is_empty_for_a_fresh_data_dir()
    {
        using var temp = new TempData();
        await using var engine = EngineServices.Create(temp.Dir);
        var dispatcher = new Dispatcher();
        SteamMethods.Register(dispatcher, engine);

        var res = await dispatcher.HandleAsync(new RequestMessage(2, "account.list", null), CancellationToken.None);

        Assert.True(res.Ok);
        Assert.Empty(res.Result!["accounts"]!.AsArray());
    }

    [Fact]
    public async Task Registers_expected_capabilities()
    {
        using var temp = new TempData();
        await using var engine = EngineServices.Create(temp.Dir);
        var dispatcher = new Dispatcher();
        SteamMethods.Register(dispatcher, engine);

        Assert.Contains("auth.status", dispatcher.Capabilities);
        Assert.Contains("account.list", dispatcher.Capabilities);
        Assert.Contains("auth.resume", dispatcher.Capabilities);
        Assert.Contains("library.enumerate", dispatcher.Capabilities);
    }

    [Fact]
    public async Task Library_enumerate_refuses_when_not_logged_on()
    {
        using var temp = new TempData();
        await using var engine = EngineServices.Create(temp.Dir);
        var dispatcher = new Dispatcher();
        SteamMethods.Register(dispatcher, engine);

        var res = await dispatcher.HandleAsync(new RequestMessage(3, "library.enumerate", null), CancellationToken.None);

        Assert.False(res.Ok);
        Assert.Equal(RpcError.Unauthenticated, res.Error!.Code);
    }
}
