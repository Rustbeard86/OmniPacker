using System.Text.Json.Nodes;
using OmniPacker.EngineHost;
using OmniPacker.EngineHost.Engine;
using OmniPacker.EngineHost.Methods;
using OmniPacker.EngineHost.Protocol;

namespace OmniPacker.EngineHost.Tests;

public class MetadataDownloadMethodsTests
{
    private sealed class NullSink : IEventSink
    {
        public Task EmitAsync(EventMessage evt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task<(Dispatcher, EngineServices, TempData)> New()
    {
        var temp = new TempData();
        var engine = EngineServices.Create(temp.Dir);
        var dispatcher = new Dispatcher();
        MetadataMethods.Register(dispatcher, engine);
        DownloadMethods.Register(dispatcher, engine, new NullSink());
        return (dispatcher, engine, temp);
    }

    [Fact]
    public async Task Registers_metadata_and_download_methods()
    {
        var (dispatcher, engine, temp) = await New();
        await using (engine)
        {
            foreach (var m in new[] { "app.branches", "app.depots", "download.start", "download.cancel" })
                Assert.Contains(m, dispatcher.Capabilities);
        }
        temp.Dispose();
    }

    [Theory]
    [InlineData("app.branches")]
    [InlineData("app.depots")]
    [InlineData("download.start")]
    public async Task App_methods_require_appId(string method)
    {
        var (dispatcher, engine, temp) = await New();
        await using (engine)
        {
            var res = await dispatcher.HandleAsync(new RequestMessage(1, method, new JsonObject()), CancellationToken.None);
            Assert.False(res.Ok);
            Assert.Equal(RpcError.BadRequest, res.Error!.Code);
        }
        temp.Dispose();
    }

    [Theory]
    [InlineData("app.branches")]
    [InlineData("app.depots")]
    [InlineData("download.start")]
    public async Task App_methods_require_login(string method)
    {
        var (dispatcher, engine, temp) = await New();
        await using (engine)
        {
            var res = await dispatcher.HandleAsync(
                new RequestMessage(2, method, new JsonObject { ["appId"] = 440 }),
                CancellationToken.None);
            Assert.False(res.Ok);
            Assert.Equal(RpcError.Unauthenticated, res.Error!.Code);
        }
        temp.Dispose();
    }

    [Fact]
    public async Task Download_cancel_requires_jobId()
    {
        var (dispatcher, engine, temp) = await New();
        await using (engine)
        {
            var res = await dispatcher.HandleAsync(new RequestMessage(3, "download.cancel", new JsonObject()), CancellationToken.None);
            Assert.False(res.Ok);
            Assert.Equal(RpcError.BadRequest, res.Error!.Code);
        }
        temp.Dispose();
    }

    [Fact]
    public async Task Download_cancel_unknown_job_is_not_cancelled()
    {
        var (dispatcher, engine, temp) = await New();
        await using (engine)
        {
            var res = await dispatcher.HandleAsync(
                new RequestMessage(4, "download.cancel", new JsonObject { ["jobId"] = "nope" }),
                CancellationToken.None);
            Assert.True(res.Ok);
            Assert.False(res.Result!["cancelled"]!.GetValue<bool>());
        }
        temp.Dispose();
    }
}
