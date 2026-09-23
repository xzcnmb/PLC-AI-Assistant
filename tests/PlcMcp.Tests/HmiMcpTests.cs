using System.Security.Cryptography;
using System.Text.Json;
using PlcMcp.Adapters;
using PlcMcp.Server.Governance;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Tests;

public sealed class HmiMcpTests
{
    private static async Task<JsonElement> InvokeAsync(McpToolRouter router, string tool, object parameters)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var result = await router.CallToolAsync(tool, args.RootElement, CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonRpc.SerializerOptions));
        return json.RootElement.Clone();
    }

    [Fact]
    public async Task HmiValidateAndGenerate_UseConfiguredManifestAndKeepSourceUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "plcmcp_hmi_mcp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "hmi.json");
            var source = """
                {"manifestVersion":"1","hmiProjectName":"Cell","targetVendor":"Siemens","defaultCulture":"zh-CN","supportedCultures":["zh-CN"],"screens":[{"screenId":"main","name":"Main","boundTagNames":["Pressure"]}],"tagBindings":[{"hmiTagName":"Pressure","plcSymbolOrAddress":"PressureSetpoint","accessRight":"ReadOnly","expectedDataType":"Real","expectedUnit":"bar","screenIds":["main"]}],"alarms":[],"recipes":[],"localizations":[]}
                """;
            await File.WriteAllTextAsync(path, source);
            var original = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var router = new McpToolRouter(DefaultPlcComposition.Create(),
                new ServerGovernanceServices(Path.Combine(root, "data"), root));
            var validation = await InvokeAsync(router, "plc_hmi_validate", new { targetId = "sim-siemens", manifestPath = path });
            Assert.False(validation.GetProperty("isError").GetBoolean());
            Assert.True(validation.GetProperty("isValid").GetBoolean());
            var result = await InvokeAsync(router, "plc_hmi_generate", new { targetId = "sim-siemens", manifestPath = path });
            Assert.False(result.GetProperty("isError").GetBoolean());
            var output = result.GetProperty("outputDirectory").GetString()!;
            Assert.StartsWith(Path.Combine(root, "data", "workspaces", "hmi"), output, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(output, "package.json")));
            Assert.True(File.Exists(Path.Combine(output, "tags.csv")));
            Assert.Equal(original, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }
}
