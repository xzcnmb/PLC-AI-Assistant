using System.Text;
using PlcMcp.Adapters;
using PlcMcp.Server.Mcp;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (args.Length > 0 && (args.Length != 2 || args[0] != "--config"))
{
    Console.Error.WriteLine("Usage: PlcMcp.Server [--config <readonly-targets.json>]");
    return 2;
}

PlcRuntimeHost host;
try
{
    host = args.Length == 0 ? DefaultPlcComposition.Create() : ConfiguredPlcComposition.Load(args[1]);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Configuration rejected: {ex.Message}");
    return 2;
}
var router = new McpToolRouter(host);
var server = new McpServer(router);

try
{
    await server.RunAsync(Console.In, Console.Out, Console.Error, cts.Token);
}
catch (OperationCanceledException)
{
}
return 0;
