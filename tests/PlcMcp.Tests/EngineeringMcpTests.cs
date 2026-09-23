using System.Text.Json;
using PlcMcp.Adapters;
using PlcMcp.Server.Governance;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Tests;

public sealed class EngineeringMcpTests
{
    private static async Task<JsonElement> CallAsync(McpServer server, string tool, object arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        await server.ProcessLineAsync(request, output, error);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    [Fact]
    public async Task DoctorAndCapabilityReport_AreReadOnlyAndDoNotClaimVendorCompiler()
    {
        var root = Path.Combine(Path.GetTempPath(), "plcmcp_mcp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var server = new McpServer(new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(root)));
            var doctor = await CallAsync(server, "plc_doctor", new { });
            Assert.False(doctor.GetProperty("isError").GetBoolean());
            var report = await CallAsync(server, "plc_get_capabilities_report", new { targetId = "sim-siemens" });
            Assert.False(report.GetProperty("isError").GetBoolean());
            Assert.Equal("capability-report-v1", report.GetProperty("schemaVersion").GetString());
            Assert.Equal("sim-siemens", report.GetProperty("targetId").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LintProgram_StatesThatItIsNotVendorCompilation()
    {
        var root = Path.Combine(Path.GetTempPath(), "plcmcp_mcp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var server = new McpServer(new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(root)));
            var lint = await CallAsync(server, "plc_lint_program", new { source = "PROGRAM X\nVAR x : BOOL; END_VAR\nIF x THEN x := FALSE; END_IF;\nEND_PROGRAM" });
            Assert.False(lint.GetProperty("isError").GetBoolean());
            Assert.False(lint.GetProperty("isVendorCompiler").GetBoolean());
            var unsupported = await CallAsync(server, "plc_compile_project", new { targetId = "sim-siemens" });
            Assert.True(unsupported.GetProperty("isError").GetBoolean());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AuditQuery_VerifiesEmptyLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), "plcmcp_mcp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var server = new McpServer(new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(root)));
            var audit = await CallAsync(server, "plc_get_audit", new { });
            Assert.True(audit.GetProperty("verification").GetProperty("isValid").GetBoolean());
            Assert.Equal(0, audit.GetProperty("records").GetArrayLength());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
