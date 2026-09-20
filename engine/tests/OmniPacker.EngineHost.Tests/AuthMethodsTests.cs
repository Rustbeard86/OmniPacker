using System.Text.Json.Nodes;
using OmniPacker.EngineHost;
using OmniPacker.EngineHost.Engine;
using OmniPacker.EngineHost.Methods;
using OmniPacker.EngineHost.Protocol;

namespace OmniPacker.EngineHost.Tests;

public class AuthMethodsTests
{
    private static async Task<(Dispatcher, EngineServices, TempData)> New()
    {
        var temp = new TempData();
        var engine = EngineServices.Create(temp.Dir);
        var dispatcher = new Dispatcher();
        AuthMethods.Register(dispatcher, engine);
        return (dispatcher, engine, temp);
    }

    [Fact]
    public async Task Registers_the_interactive_auth_methods()
    {
        var (dispatcher, engine, temp) = await New();
        await using (engine)
        {
            Assert.Contains("auth.beginQr", dispatcher.Capabilities);
            Assert.Contains("auth.beginCredentials", dispatcher.Capabilities);
            Assert.Contains("auth.submitGuard", dispatcher.Capabilities);
            Assert.Contains("auth.logout", dispatcher.Capabilities);
        }
        temp.Dispose();
    }

    [Fact]
    public async Task BeginCredentials_rejects_missing_username()
    {
        var (dispatcher, engine, temp) = await New();
        await using (engine)
        {
            var res = await dispatcher.HandleAsync(
                new RequestMessage(1, "auth.beginCredentials", new JsonObject { ["password"] = "x" }),
                CancellationToken.None);
            Assert.False(res.Ok);
            Assert.Equal(RpcError.BadRequest, res.Error!.Code);
        }
        temp.Dispose();
    }

    [Fact]
    public async Task SubmitGuard_rejects_missing_code()
    {
        var (dispatcher, engine, temp) = await New();
        await using (engine)
        {
            var res = await dispatcher.HandleAsync(
                new RequestMessage(2, "auth.submitGuard", new JsonObject()),
                CancellationToken.None);
            Assert.False(res.Ok);
            Assert.Equal(RpcError.BadRequest, res.Error!.Code);
        }
        temp.Dispose();
    }

    [Fact]
    public async Task SubmitGuard_with_no_login_in_progress_is_not_accepted()
    {
        var (dispatcher, engine, temp) = await New();
        await using (engine)
        {
            var res = await dispatcher.HandleAsync(
                new RequestMessage(3, "auth.submitGuard", new JsonObject { ["code"] = "ABCDE" }),
                CancellationToken.None);
            Assert.True(res.Ok);
            Assert.False(res.Result!["accepted"]!.GetValue<bool>());
        }
        temp.Dispose();
    }
}
