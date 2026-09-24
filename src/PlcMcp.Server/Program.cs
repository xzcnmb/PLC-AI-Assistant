using System.Text;
using PlcMcp.Adapters;
using PlcMcp.Server.Mcp;
using PlcMcp.Server.Governance;
using PlcMcp.Engineering.Workers.Codesys;

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
CodesysWorkerConfig? codesysConfig = null;
GxWorks3ProbeConfig? gxworks3Config = null;
var codesysValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var gxworks3Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not (
        "--config" or "--smart-project-root" or
        "--codesys-exe" or "--codesys-profile" or "--codesys-version" or "--codesys-script" or "--codesys-script-sha256" or "--codesys-project-root" or
        "--gxworks3-probe" or "--gxworks3-probe-sha256" or "--gxworks3-exe" or "--gxworks3-version"))
    {
        Console.Error.WriteLine("Usage: PlcMcp.Server [--config <readonly-targets.json>] [--smart-project-root <absolute-directory>] [--codesys-exe <path> --codesys-profile <name> --codesys-version <version> --codesys-script <path> --codesys-script-sha256 <sha256> --codesys-project-root <absolute-directory>] [--gxworks3-probe <path> --gxworks3-probe-sha256 <sha256> --gxworks3-exe <path> --gxworks3-version <version>]");
        return 2;
    }
    if (args[i] == "--config" && configPath is null) configPath = args[i + 1];
    else if (args[i] == "--smart-project-root" && smartProjectRoot is null) smartProjectRoot = args[i + 1];
    else if (args[i].StartsWith("--codesys-", StringComparison.Ordinal))
    {
        if (!codesysValues.TryAdd(args[i], args[i + 1]))
        {
            Console.Error.WriteLine($"Option '{args[i]}' can be supplied only once.");
            return 2;
        }
    }
    else if (args[i].StartsWith("--gxworks3-", StringComparison.Ordinal))
    {
        if (!gxworks3Values.TryAdd(args[i], args[i + 1]))
        {
            Console.Error.WriteLine($"Option '{args[i]}' can be supplied only once.");
            return 2;
        }
    }
    else
    {
        Console.Error.WriteLine("Each option can be supplied only once.");
        return 2;
    }
}

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

PlcRuntimeHost host;
ServerGovernanceServices governance;
try
{
    host = configPath is null ? DefaultPlcComposition.Create() : ConfiguredPlcComposition.Load(configPath);
    governance = new ServerGovernanceServices(Path.Combine(AppContext.BaseDirectory, "data"), smartProjectRoot, codesysConfig, gxworks3Config);
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
