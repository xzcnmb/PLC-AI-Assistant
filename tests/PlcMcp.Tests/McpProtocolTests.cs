using System.Text.Json;
using PlcMcp.Adapters;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Tests;

public sealed class McpProtocolTests
{
    private static McpServer CreateServer() => new(new McpToolRouter(DefaultPlcComposition.Create()));

    private static async Task<string> ExchangeAsync(string request, McpServer? server = null)
    {
        using var output = new StringWriter();
        using var log = new StringWriter();
        await (server ?? CreateServer()).ProcessLineAsync(request, output, log);
        return output.ToString();
    }

    private static JsonElement Payload(JsonElement response) =>
        JsonSerializer.Deserialize<JsonElement>(response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);

    [Fact]
    public async Task Initialize_PreservesIdAndAdvertisesOnlyImplementedCapabilities()
    {
        var output = await ExchangeAsync("""{"jsonrpc":"2.0","id":"init-1","method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"tests","version":"1"}}}""");
        using var response = JsonDocument.Parse(output);
        Assert.Equal("init-1", response.RootElement.GetProperty("id").GetString());
        var result = response.RootElement.GetProperty("result");
        Assert.Equal("plc-mcp", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Theory]
    [InlineData("initialize", "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}")]
    [InlineData("tools/list", "{}")]
    [InlineData("tools/call", "{\"name\":\"plc_list_targets\",\"arguments\":{}}")]
    [InlineData("tools/call", "{\"name\":\"plc_read_tags\",\"arguments\":{}}")]
    [InlineData("notifications/initialized", "{}")]
    [InlineData("not-a-method", "{}")]
    public async Task Notifications_NeverProduceResponses(string method, string arguments)
    {
        var request = $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{arguments}}}";
        Assert.Equal(string.Empty, await ExchangeAsync(request));
    }

    [Fact]
    public async Task ToolCatalog_UsesStandardAnnotationsAndExposesSymbolBrowse()
    {
        using var response = JsonDocument.Parse(await ExchangeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        var tools = response.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "plc_browse_symbols");
        Assert.All(tools, t =>
        {
            Assert.Equal("object", t.GetProperty("inputSchema").GetProperty("type").GetString());
            var annotation = t.GetProperty("annotations");
            Assert.True(annotation.TryGetProperty("readOnlyHint", out _));
            Assert.True(annotation.TryGetProperty("destructiveHint", out _));
        });
        Assert.DoesNotContain(tools, t => t.GetProperty("name").GetString() is "plc_download_project" or "plc_force_io");
    }

    [Fact]
    public async Task OutOfRangeNumericString_IsRejectedAtMcpBoundary()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"plc_plan_write","arguments":{"targetId":"sim-siemens","changes":{"PressureSetpoint":"999999"}}}}""";
        using var response = JsonDocument.Parse(await ExchangeAsync(request));
        Assert.True(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task AliasPlan_CanBeAppliedOnceAndReadBackThroughMcp()
    {
        var server = CreateServer();
        using var planned = JsonDocument.Parse(await ExchangeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"plc_plan_write","arguments":{"targetId":"sim-siemens","changes":{"目标压力":"4.5"}}}}""", server));
        Assert.False(planned.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        var plan = Payload(planned.RootElement);
        Assert.Equal("PressureSetpoint", plan.GetProperty("changes")[0].GetProperty("tag").GetString());
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = "plc_apply_write", arguments = new { planId = plan.GetProperty("planId").GetString(), approvalToken = plan.GetProperty("approvalToken").GetString() } }
        });
        using var applied = JsonDocument.Parse(await ExchangeAsync(request, server));
        Assert.False(applied.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        using var repeated = JsonDocument.Parse(await ExchangeAsync(request, server));
        Assert.True(repeated.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        using var read = JsonDocument.Parse(await ExchangeAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"plc_read_tags","arguments":{"targetId":"sim-siemens","tags":["目标压力"]}}}""", server));
        Assert.Equal(4.5, Payload(read.RootElement).GetProperty("values")[0].GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task InvalidJson_ReturnsParseErrorAndServerCanHandleNextRequest()
    {
        var server = CreateServer();
        using var invalid = JsonDocument.Parse(await ExchangeAsync("{", server));
        Assert.Equal(-32700, invalid.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        using var valid = JsonDocument.Parse(await ExchangeAsync("""{"jsonrpc":"2.0","id":7,"method":"ping"}""", server));
        Assert.Equal(7, valid.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task BrowseSymbols_UsesManifestAliasesWithoutNetworkAccess()
    {
        using var response = JsonDocument.Parse(await ExchangeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"plc_browse_symbols","arguments":{"targetId":"sim-siemens","filter":"目标压力"}}}"""));
        Assert.False(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        var payload = Payload(response.RootElement);
        Assert.Equal("PressureSetpoint", payload.GetProperty("tags")[0].GetProperty("name").GetString());
    }
}
