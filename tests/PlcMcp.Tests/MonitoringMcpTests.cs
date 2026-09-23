using System.Text.Json;
using PlcMcp.Adapters;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Tests;

public sealed class MonitoringMcpTests
{
    private static async Task<JsonElement> CallAsync(string arguments)
    {
        var server = new McpServer(new McpToolRouter(DefaultPlcComposition.Create()));
        using var output = new StringWriter();
        using var log = new StringWriter();
        await server.ProcessLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"plc_monitor_window\",\"arguments\":{arguments}}}}}", output, log);
        using var response = JsonDocument.Parse(output.ToString());
        return response.RootElement.GetProperty("result").Clone();
    }

    [Fact]
    public async Task FiniteWindow_IsReadOnlyAndStatesNoAtomicSnapshot()
    {
        var response = await CallAsync("""{"targetId":"sim-siemens","tags":["目标压力"],"intervalMs":50,"durationSeconds":1,"maxSamples":2}""");
        Assert.False(response.GetProperty("isError").GetBoolean());
        Assert.Equal(2, response.GetProperty("sampleCount").GetInt32());
        Assert.Equal("none", response.GetProperty("snapshotGuarantee").GetString());
        Assert.Equal("PressureSetpoint", response.GetProperty("canonicalTags")[0].GetString());
    }

    [Theory]
    [InlineData("{\"targetId\":\"sim-siemens\",\"tags\":[\"目标压力\"],\"intervalMs\":1}")]
    [InlineData("{\"targetId\":\"sim-siemens\",\"tags\":[\"目标压力\"],\"maxSamples\":1000}")]
    [InlineData("{\"targetId\":\"sim-siemens\",\"tags\":[\"目标压力\"],\"durationSeconds\":1000}")]
    public async Task RejectsUnboundedOrHighFrequencyRequests(string args)
    {
        var response = await CallAsync(args);
        Assert.True(response.GetProperty("isError").GetBoolean());
    }
}
