using System.Text;
using PlcMcp.Adapters;
using PlcMcp.Server.Mcp;
using PlcMcp.Server.Governance;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

string? configPath = null;
string? smartProjectRoot = null;
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--config" or "--smart-project-root"))
    {
        Console.Error.WriteLine("Usage: PlcMcp.Server [--config <readonly-targets.json>] [--smart-project-root <absolute-directory>]");
        return 2;
    }
    if (args[i] == "--config" && configPath is null) configPath = args[i + 1];
    else if (args[i] == "--smart-project-root" && smartProjectRoot is null) smartProjectRoot = args[i + 1];
    else
    {
        Console.Error.WriteLine("Each option can be supplied only once.");
        return 2;
    }
}

PlcRuntimeHost host;
ServerGovernanceServices governance;
try
{
    host = configPath is null ? DefaultPlcComposition.Create() : ConfiguredPlcComposition.Load(configPath);
    governance = new ServerGovernanceServices(Path.Combine(AppContext.BaseDirectory, "data"), smartProjectRoot);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Configuration rejected: {ex.Message}");
    return 2;
}
await governance.Jobs.RecoverFromCrashAsync(cts.Token);
var router = new McpToolRouter(host, governance);
var server = new McpServer(router);

try
{
    await server.RunAsync(Console.In, Console.Out, Console.Error, cts.Token);
}
catch (OperationCanceledException)
{
}
return 0;
