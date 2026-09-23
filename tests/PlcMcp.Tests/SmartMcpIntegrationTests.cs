using System.Text.Json;
using PlcMcp.Adapters;
using PlcMcp.Server.Governance;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Tests;

public sealed class SmartMcpIntegrationTests
{
    [Fact]
    public async Task ConfiguredSmartRoot_ExposesInspectAndReadsV2WithoutTouchingOriginal()
    {
        var source = @"D:\SMart200 MCP\work\_autoflow.smart";
        if (!File.Exists(source)) return;
        var dataRoot = Path.Combine(Path.GetTempPath(), "plcmcp_smart_mcp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var before = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source));
            var governance = new ServerGovernanceServices(dataRoot, Path.GetDirectoryName(source));
            if (governance.SmartWorker is null) return;
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);
            Assert.Contains(router.ToolDescriptors, x => x.Name == "plc_smart_inspect");
            Assert.Contains(router.ToolDescriptors, x => x.Name == "plc_smart_validate");
            using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { projectPath = source }));
            var response = await router.CallToolAsync("plc_smart_inspect", args.RootElement, CancellationToken.None);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(response, JsonRpc.SerializerOptions));
            Assert.False(doc.RootElement.GetProperty("isError").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("parsedProject").GetProperty("pous").GetArrayLength() > 0);
            var after = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source));
            Assert.Equal(before, after);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                foreach (var file in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DefaultMode_HidesSmartEngineTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "plcmcp_smart_mcp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var router = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(root));
            Assert.DoesNotContain(router.ToolDescriptors, x => x.Name is "plc_smart_inspect" or "plc_smart_validate" or "plc_project_inspect");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
