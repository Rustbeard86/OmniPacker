using System.Text.Json.Nodes;
using OmniPacker.EngineHost.Protocol;

namespace OmniPacker.EngineHost.Tests;

public class CodecTests
{
    [Fact]
    public void Parses_ping_request_fixture()
    {
        var msg = Codec.Parse(Fixtures.ReadLine("req_ping.json"));
        var req = Assert.IsType<RequestMessage>(msg);
        Assert.Equal(1UL, req.Id);
        Assert.Equal("ping", req.Method);
        Assert.Equal("abc123", req.Params!["nonce"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_hello_request_with_null_params()
    {
        var req = Assert.IsType<RequestMessage>(Codec.Parse(Fixtures.ReadLine("req_hello.json")));
        Assert.Equal("hello", req.Method);
        Assert.Null(req.Params);
    }

    [Fact]
    public void Parses_ok_response_fixture()
    {
        var res = Assert.IsType<ResponseMessage>(Codec.Parse(Fixtures.ReadLine("res_ping_ok.json")));
        Assert.True(res.Ok);
        Assert.Null(res.Error);
        Assert.True(res.Result!["pong"]!.GetValue<bool>());
        Assert.Equal("abc123", res.Result!["nonce"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_error_response_fixture()
    {
        var res = Assert.IsType<ResponseMessage>(Codec.Parse(Fixtures.ReadLine("res_error.json")));
        Assert.False(res.Ok);
        Assert.Null(res.Result);
        Assert.Equal("unknown_method", res.Error!.Code);
        Assert.False(res.Error.Retriable);
    }

    [Fact]
    public void Parses_transient_error_as_retriable()
    {
        var res = Assert.IsType<ResponseMessage>(Codec.Parse(Fixtures.ReadLine("res_error_transient.json")));
        Assert.False(res.Ok);
        Assert.Equal("transient", res.Error!.Code);
        Assert.True(res.Error.Retriable);
    }

    [Fact]
    public void Parses_ready_event_fixture()
    {
        var evt = Assert.IsType<EventMessage>(Codec.Parse(Fixtures.ReadLine("evt_ready.json")));
        Assert.Equal("ready", evt.Event);
        Assert.Equal(1, evt.Data!["protocol"]!.GetValue<int>());
        var caps = evt.Data!["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("ping", caps);
        Assert.Contains("hello", caps);
    }

    [Fact]
    public void Parses_log_event_fixture()
    {
        var evt = Assert.IsType<EventMessage>(Codec.Parse(Fixtures.ReadLine("evt_log.json")));
        Assert.Equal("log", evt.Event);
        Assert.Equal("info", evt.Data!["level"]!.GetValue<string>());
        Assert.Equal("engine", evt.Data!["source"]!.GetValue<string>());
    }

    [Fact]
    public void Roundtrips_a_constructed_response()
    {
        var original = ResponseMessage.Success(42, new JsonObject { ["pong"] = true, ["nonce"] = "z" });
        var reparsed = Assert.IsType<ResponseMessage>(Codec.Parse(Codec.Encode(original)));
        Assert.Equal(42UL, reparsed.Id);
        Assert.True(reparsed.Ok);
        Assert.Equal("z", reparsed.Result!["nonce"]!.GetValue<string>());
    }

    [Fact]
    public void Roundtrips_a_constructed_error()
    {
        var original = ResponseMessage.Failure(3, RpcError.Of(RpcError.DepotAccessDenied, "no license"));
        var reparsed = Assert.IsType<ResponseMessage>(Codec.Parse(Codec.Encode(original)));
        Assert.False(reparsed.Ok);
        Assert.Equal("depot_access_denied", reparsed.Error!.Code);
        Assert.Equal("no license", reparsed.Error.Message);
    }

    [Fact]
    public void Encoded_message_has_no_embedded_newline()
    {
        var line = Codec.Encode(new EventMessage("log", new JsonObject
        {
            ["message"] = "line one\nline two",
        }));
        Assert.DoesNotContain('\n', line);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"id\":1,\"method\":\"ping\"}")]      // missing t
    [InlineData("{\"t\":\"bogus\"}")]                    // unknown discriminator
    [InlineData("{\"t\":\"req\",\"id\":1}")]             // missing method
    [InlineData("{\"t\":\"req\",\"method\":\"ping\"}")]  // missing id
    [InlineData("{\"t\":\"req\",\"id\":1,\"method\":\"ping\",\"params\":5}")] // params not object
    public void Rejects_malformed_lines(string line)
    {
        Assert.Throws<ProtocolException>(() => Codec.Parse(line));
    }
}
