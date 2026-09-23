using System.Text.Json;
using PlcMcp.Adapters;
using PlcMcp.Server.Governance;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Tests;

public sealed class FileToolBoundaryTests
{
    private static async Task<JsonElement> CallAsync(McpToolRouter router, string tool, object parameters)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var result = await router.CallToolAsync(tool, args.RootElement, CancellationToken.None);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonRpc.SerializerOptions));
        return serialized.RootElement.Clone();
    }

    [Fact]
    public async Task NoEngineeringRoot_ExposesInlineLintButNoFileReadOrCompare()
    {
        var root = Path.Combine(Path.GetTempPath(), "plcmcp_file_boundary_" + Guid.NewGuid().ToString("N"));
        try
        {
            var router = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(root));
            Assert.DoesNotContain(router.ToolDescriptors, t => t.Name is "plc_compare_projects" or "plc_hmi_generate" or "plc_get_job");
            var rejected = await CallAsync(router, "plc_lint_program", new { filePath = Path.Combine(root, "secret.txt") });
            Assert.True(rejected.GetProperty("isError").GetBoolean());
            var inline = await CallAsync(router, "plc_lint_program", new { source = "PROGRAM Main\nEND_PROGRAM" });
            Assert.False(inline.GetProperty("isError").GetBoolean());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EngineeringRoot_RejectsTraversalAndOversizedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "plcmcp_file_boundary_" + Guid.NewGuid().ToString("N"));
        var allowed = Path.Combine(root, "allowed");
        var outside = Path.Combine(root, "outside.st");
        Directory.CreateDirectory(allowed);
        try
        {
            File.WriteAllText(outside, "SECRET");
            var large = Path.Combine(allowed, "large.st");
            using (var stream = File.Create(large)) stream.SetLength(1_048_577);
            var router = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(Path.Combine(root, "data"), allowed));
            Assert.True((await CallAsync(router, "plc_lint_program", new { filePath = outside })).GetProperty("isError").GetBoolean());
            Assert.True((await CallAsync(router, "plc_lint_program", new { filePath = large })).GetProperty("isError").GetBoolean());
            Assert.True((await CallAsync(router, "plc_compare_projects", new { leftPath = outside, rightPath = outside })).GetProperty("isError").GetBoolean());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
