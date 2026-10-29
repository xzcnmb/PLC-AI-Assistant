using System.Text;
using PlcMcp.Adapters;
using PlcMcp.Engineering.Workers.AutoShop;
using PlcMcp.Engineering.Workers.Codesys;
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

const string usage = "Usage: PlcMcp.Server [--config <readonly-targets.json>] [--smart-project-root <absolute-directory>] " +
    "[--autoshop-bridge] [--autoshop-bridge-path <register-path>] [--autoshop-compile-rva <hex>] " +
    "[--autoshop-allow-physical-write] [--autoshop-allow-physical-download] " +
    "[--codesys-exe <path> --codesys-profile <name> --codesys-version <version> --codesys-script <path> --codesys-script-sha256 <sha256> --codesys-project-root <absolute-directory>] " +
    "[--gxworks3-probe <path> --gxworks3-probe-sha256 <sha256> --gxworks3-exe <path> --gxworks3-version <version>]";

string? configPath = null;
string? smartProjectRoot = null;
bool autoShopBridge = false;
string? autoShopBridgePath = null;
string? autoShopCompileRva = null;
bool autoShopAllowPhysicalWrite = false;
bool autoShopAllowPhysicalDownload = false;
var codesysValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var gxworks3Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    string? ReadValue()
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine(usage);
            return null;
        }
        return args[++i];
    }

    switch (args[i])
    {
        case "--config":
            if (configPath is not null || (configPath = ReadValue()) is null) { Console.Error.WriteLine(usage); return 2; }
            break;
        case "--smart-project-root":
            if (smartProjectRoot is not null || (smartProjectRoot = ReadValue()) is null) { Console.Error.WriteLine(usage); return 2; }
            break;
        case "--autoshop-bridge":
            if (autoShopBridge) { Console.Error.WriteLine("Each option can be supplied only once."); return 2; }
            autoShopBridge = true;
            break;
        case "--autoshop-bridge-path":
            if (autoShopBridgePath is not null || (autoShopBridgePath = ReadValue()) is null) { Console.Error.WriteLine(usage); return 2; }
            break;
        case "--autoshop-compile-rva":
            if (autoShopCompileRva is not null || (autoShopCompileRva = ReadValue()) is null) { Console.Error.WriteLine(usage); return 2; }
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
            if (args[i].StartsWith("--codesys-", StringComparison.Ordinal))
            {
                var value = ReadValue();
                if (value is null || !codesysValues.TryAdd(args[i], value))
                {
                    Console.Error.WriteLine($"Option '{args[i]}' can be supplied only once.");
                    return 2;
                }
            }
            else if (args[i].StartsWith("--gxworks3-", StringComparison.Ordinal))
            {
                var value = ReadValue();
                if (value is null || !gxworks3Values.TryAdd(args[i], value))
                {
                    Console.Error.WriteLine($"Option '{args[i]}' can be supplied only once.");
                    return 2;
                }
            }
            else
            {
                Console.Error.WriteLine(usage);
                return 2;
            }
            break;
    }
}

CodesysWorkerConfig? codesysConfig = null;
GxWorks3ProbeConfig? gxworks3Config = null;

if (codesysValues.Count > 0)
{
    string[] required = ["--codesys-exe", "--codesys-profile", "--codesys-version", "--codesys-script", "--codesys-script-sha256", "--codesys-project-root"];
    var missing = required.Where(k => !codesysValues.ContainsKey(k)).ToArray();
    if (missing.Length > 0)
    {
        Console.Error.WriteLine("CODESYS configuration is incomplete; required options: " + string.Join(", ", required));
        return 2;
    }

    codesysConfig = new CodesysWorkerConfig
    {
        CodesysExePath = codesysValues["--codesys-exe"],
        ProfileName = codesysValues["--codesys-profile"],
        CodesysVersion = codesysValues["--codesys-version"],
        DriverScriptPath = codesysValues["--codesys-script"],
        DriverScriptSha256 = codesysValues["--codesys-script-sha256"],
        ProjectRoot = codesysValues["--codesys-project-root"]
    };
}

if (gxworks3Values.Count > 0)
{
    string[] required = ["--gxworks3-probe", "--gxworks3-probe-sha256", "--gxworks3-exe", "--gxworks3-version"];
    var missing = required.Where(k => !gxworks3Values.ContainsKey(k)).ToArray();
    if (missing.Length > 0)
    {
        Console.Error.WriteLine("GX Works3 configuration is incomplete; required options: " + string.Join(", ", required));
        return 2;
    }

    string probePath = gxworks3Values["--gxworks3-probe"];
    string gxw3Path = gxworks3Values["--gxworks3-exe"];
    if (!Path.IsPathRooted(probePath))
    {
        Console.Error.WriteLine($"--gxworks3-probe path must be absolute: '{probePath}'");
        return 2;
    }
    if (!Path.IsPathRooted(gxw3Path))
    {
        Console.Error.WriteLine($"--gxworks3-exe path must be absolute: '{gxw3Path}'");
        return 2;
    }

    gxworks3Config = new GxWorks3ProbeConfig
    {
        ProbeExePath = probePath,
        ProbeExeSha256 = gxworks3Values["--gxworks3-probe-sha256"],
        Gxw3ExePath = gxw3Path,
        ExpectedVersion = gxworks3Values["--gxworks3-version"]
    };
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
    governance = new ServerGovernanceServices(
        Path.Combine(AppContext.BaseDirectory, "data"),
        smartProjectRoot,
        autoShopBridgeConfig,
        codesysConfig,
        gxworks3Config,
        autoShopInjectOptions: autoShopInjectOptions,
        allowPhysicalWrite: autoShopAllowPhysicalWrite,
        allowPhysicalDownload: autoShopAllowPhysicalDownload);
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