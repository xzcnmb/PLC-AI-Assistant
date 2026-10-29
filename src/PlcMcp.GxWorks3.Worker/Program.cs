using System;
using System.Text;

namespace PlcMcp.GxWorks3.Worker
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Configure UTF-8 encoding for stdio
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);

            CommandLineOptions? options;
            string? errorMessage;

            if (!CommandLineOptions.TryParse(args, out options, out errorMessage) || options == null)
            {
                Console.Error.WriteLine("[FATAL] Invalid arguments: " + errorMessage);
                Console.Error.WriteLine("Usage: PlcMcp.GxWorks3.Worker.exe --gx-exe <absolute-path-to-GXW3.exe> --expected-version <exact-version>");
                return 1;
            }

            try
            {
                var dispatcher = new JsonRpcDispatcher(options);
                return dispatcher.RunLoop(Console.In, Console.Out, Console.Error);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FATAL] Unhandled worker exception: " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 2;
            }
        }
    }
}
