using System.Text;
using PlcMcp.Adapters;
using PlcMcp.Engineering.Workers.AutoShop;
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

const string usage = "Usage: PlcMcp.Server [--config <readonly-targets.json>] [--smart-project-root <absolute-directory>] [--autoshop-bridge] [--autoshop-bridge-path <register-path>] [--autoshop-compile-rva <hex>] [--autoshop-allow-physical-write] [--autoshop-allow-physical-download]";

string? configPath = null;
string? smartProjectRoot = null;
bool autoShopBridge = false;
string? autoShopBridgePath = null;
string? autoShopCompileRva = null;
bool autoShopAllowPhysicalWrite = false;
bool autoShopAllowPhysicalDownload = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config":
            if (configPath is not null || i + 1 >= args.Length) { Console.Error.WriteLine(usage); return 2; }
            configPath = args[++i];
            break;
        case "--smart-project-root":
            if (smartProjectRoot is not null || i + 1 >= args.Length) { Console.Error.WriteLine(usage); return 2; }
            smartProjectRoot = args[++i];
            break;
        case "--autoshop-bridge":
            if (autoShopBridge) { Console.Error.WriteLine("Each option can be supplied only once."); return 2; }
            autoShopBridge = true;
            break;
        case "--autoshop-bridge-path":
            if (autoShopBridgePath is not null || i + 1 >= args.Length) { Console.Error.WriteLine(usage); return 2; }
            autoShopBridgePath = args[++i];
            break;
        case "--autoshop-compile-rva":
            if (autoShopCompileRva is not null || i + 1 >= args.Length) { Console.Error.WriteLine(usage); return 2; }
            autoShopCompileRva = args[++i];
            break;
        case "--autoshop-allow-physical-write":
            if (autoShopAllowPhysicalWrite) { Console.Error.WriteLine("Each option can be supplied only once."); return 2; }
            autoShopAllowPhysicalWrite = true;
            break;
        case "--autoshop-allow-physical-download":
            if (autoShopAllowPhysicalDownload) { Console.Error.WriteLine("Each option can be supplied only once."); return 2; }
            autoShopAllowPhysicalDownload = true;
            break;
        default:
            Console.Error.WriteLine(usage);
            return 2;
    }
}

AutoShopBridgeConfig? autoShopBridgeConfig = null;
AutoShopInjectToolsOptions? autoShopInjectOptions = null;
if (autoShopBridge)
{
    autoShopBridgeConfig = new AutoShopBridgeConfig
    {
        RegisterPath = string.IsNullOrWhiteSpace(autoShopBridgePath) ? "PlcMcp.AutoShopBridge" : autoShopBridgePath
    };
    if (!string.IsNullOrWhiteSpace(autoShopCompileRva))
    {
        // E3 compile-entry override; the default is the verified 0x15A2EC0 compile-all entry.
        autoShopInjectOptions = new AutoShopInjectToolsOptions
        {
            CompileEntryHint = $"{autoShopCompileRva} (compile-entry override)"
        };
    }
}

PlcRuntimeHost host;
ServerGovernanceServices governance;
try
{
    host = configPath is null ? DefaultPlcComposition.Create() : ConfiguredPlcComposition.Load(configPath);
    governance = new ServerGovernanceServices(Path.Combine(AppContext.BaseDirectory, "data"), smartProjectRoot, autoShopBridgeConfig, autoShopInjectOptions: autoShopInjectOptions, allowPhysicalWrite: autoShopAllowPhysicalWrite, allowPhysicalDownload: autoShopAllowPhysicalDownload);
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
finally
{
    // Clean up any owned AutoShop experiment instance (identity re-verified; journal recovery).
    if (governance.AutoShopInject is not null)
    {
        try
        {
            await governance.AutoShopInject.StopOwnedAsync(cts.Token);
        }
        catch
        {
            // shutdown cleanup is best-effort
        }
    }
}
return 0;
