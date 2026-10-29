using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace PlcMcp.GxWorks3.Worker
{
    public sealed class JsonRpcDispatcher
    {
        private const int MaxRequestSizeBytes = 64 * 1024; // 64 KiB
        private readonly MetadataScanner _scanner;
        private readonly JavaScriptSerializer _serializer;

        public JsonRpcDispatcher(CommandLineOptions options)
        {
            _scanner = new MetadataScanner(options);
            _serializer = new JavaScriptSerializer
            {
                MaxJsonLength = 10 * 1024 * 1024
            };
        }

        public int RunLoop(TextReader input, TextWriter output, TextWriter error)
        {
            while (true)
            {
                bool lengthExceeded;
                string? line = SafeReadLine(input, MaxRequestSizeBytes, out lengthExceeded);

                if (lengthExceeded)
                {
                    error.WriteLine("[WARN] Incoming request exceeded 64 KiB limit.");
                    SendError(output, null, -32600, "Request line exceeds maximum allowed size of 64 KiB.");
                    continue;
                }

                if (line == null)
                {
                    // Stdin closed (EOF)
                    error.WriteLine("[INFO] Stdin reached EOF. Exiting worker cleanly.");
                    return 0;
                }

                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                // Check for batch requests (JSON array)
                if (trimmed.StartsWith("["))
                {
                    error.WriteLine("[WARN] Batch request received and rejected.");
                    SendError(output, null, -32600, "Batch requests are not supported.");
                    continue;
                }

                // Deserialize JSON
                object? parsed;
                try
                {
                    parsed = _serializer.DeserializeObject(trimmed);
                }
                catch (Exception ex)
                {
                    error.WriteLine("[WARN] JSON deserialization failed: " + ex.Message);
                    SendError(output, null, -32700, "Parse error: invalid JSON payload.");
                    continue;
                }

                var rpcObject = parsed as Dictionary<string, object>;
                if (rpcObject == null)
                {
                    SendError(output, null, -32600, "Invalid Request: root must be a JSON object.");
                    continue;
                }

                // Verify "jsonrpc" == "2.0"
                if (!rpcObject.ContainsKey("jsonrpc") || !string.Equals(Convert.ToString(rpcObject["jsonrpc"]), "2.0", StringComparison.Ordinal))
                {
                    object? rawId = rpcObject.ContainsKey("id") ? rpcObject["id"] : null;
                    SendError(output, rawId, -32600, "Invalid JSON-RPC version: expected '2.0'.");
                    continue;
                }

                // Verify "id" presence. Notifications without "id" MUST NOT be executed.
                if (!rpcObject.ContainsKey("id") || rpcObject["id"] == null)
                {
                    error.WriteLine("[INFO] Notification received without id; ignoring without execution per policy.");
                    continue;
                }

                object id = rpcObject["id"];

                // Verify "method"
                if (!rpcObject.ContainsKey("method") || !(rpcObject["method"] is string))
                {
                    SendError(output, id, -32600, "Invalid Request: 'method' must be a string.");
                    continue;
                }

                string method = (string)rpcObject["method"];

                // Extract params
                object? rpcParams = rpcObject.ContainsKey("params") ? rpcObject["params"] : null;

                // Dispatch method
                bool shouldExit;
                int exitCode = HandleMethod(method, rpcParams, id, output, error, out shouldExit);
                if (shouldExit)
                {
                    return exitCode;
                }
            }
        }

        private int HandleMethod(
            string method,
            object? rpcParams,
            object id,
            TextWriter output,
            TextWriter error,
            out bool shouldExit)
        {
            shouldExit = false;

            switch (method)
            {
                case "handshake":
                    return HandleHandshake(rpcParams, id, output, error);

                case "doctor":
                    return HandleDoctor(rpcParams, id, output, error);

                case "shutdown":
                    shouldExit = true;
                    return HandleShutdown(rpcParams, id, output, error);

                default:
                    // Unsupported RPC error for all unknown methods (including submit/cancel/engineering)
                    error.WriteLine(string.Format("[WARN] Unsupported RPC method called: '{0}'.", method));
                    SendError(output, id, -32601, string.Format("Method '{0}' is unsupported. GxWorks3-Metadata-Worker is restricted to diagnostic metadata operations and declares no engineering capabilities.", method));
                    return 0;
            }
        }

        private int HandleHandshake(object? rpcParams, object id, TextWriter output, TextWriter error)
        {
            // Params whitelist: null or object
            if (rpcParams != null && !(rpcParams is Dictionary<string, object>))
            {
                SendError(output, id, -32602, "Invalid params: 'handshake' accepts an object or null.");
                return 0;
            }

            var handshakeResult = new Dictionary<string, object>
            {
                { "workerName", "GxWorks3-Metadata-Worker" },
                { "workerVersion", "1.0" },
                { "protocolVersion", "1.0" },
                { "vendor", "Mitsubishi" },
                { "bitness", "32-bit" },
                { "runtimeEnvironment", ".NET Framework 4.8 (x86)" },
                { "capabilities", new string[] { "HostDiagnostics", "DependencyMetadata" } },
                { "capabilityEvidence", new Dictionary<string, string>
                    {
                        { "HostDiagnostics", "Inspects GXW3 executable, version, PE bitness, and configuration metadata." },
                        { "DependencyMetadata", "Inspects Service.config service keys and vendor managed DLL assembly target frameworks without code execution." }
                    }
                }
            };

            SendSuccess(output, id, handshakeResult);
            return 0;
        }

        private int HandleDoctor(object? rpcParams, object id, TextWriter output, TextWriter error)
        {
            // Strict params whitelist: must be null or empty object {}
            if (rpcParams != null)
            {
                var dictParams = rpcParams as Dictionary<string, object>;
                if (dictParams == null || dictParams.Count > 0)
                {
                    SendError(output, id, -32602, "Invalid params: 'doctor' accepts only an empty object or null.");
                    return 0;
                }
            }

            var report = _scanner.PerformDoctorDiagnosis();
            SendSuccess(output, id, report);
            return 0;
        }

        private int HandleShutdown(object? rpcParams, object id, TextWriter output, TextWriter error)
        {
            // Strict params whitelist: must be null or empty object {}
            if (rpcParams != null)
            {
                var dictParams = rpcParams as Dictionary<string, object>;
                if (dictParams == null || dictParams.Count > 0)
                {
                    SendError(output, id, -32602, "Invalid params: 'shutdown' accepts only an empty object or null.");
                    return 0;
                }
            }

            var result = new Dictionary<string, object>
            {
                { "status", "ok" },
                { "message", "Worker shutting down cleanly." }
            };

            SendSuccess(output, id, result);
            error.WriteLine("[INFO] Shutdown confirmed via RPC. Clean exit 0.");
            return 0;
        }

        private void SendSuccess(TextWriter output, object? id, object result)
        {
            var resp = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id! },
                { "result", result }
            };

            string json = _serializer.Serialize(resp);
            output.WriteLine(json);
            output.Flush();
        }

        private void SendError(TextWriter output, object? id, int code, string message)
        {
            var resp = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id! },
                { "error", new Dictionary<string, object>
                    {
                        { "code", code },
                        { "message", message }
                    }
                }
            };

            string json = _serializer.Serialize(resp);
            output.WriteLine(json);
            output.Flush();
        }

        private static string? SafeReadLine(TextReader reader, int maxBytes, out bool lengthExceeded)
        {
            lengthExceeded = false;
            var sb = new StringBuilder();
            int byteCount = 0;

            while (true)
            {
                int ch = reader.Read();
                if (ch == -1)
                {
                    if (sb.Length == 0) return null;
                    break;
                }

                if (ch == '\r')
                {
                    if (reader.Peek() == '\n')
                    {
                        reader.Read();
                    }
                    break;
                }

                if (ch == '\n')
                {
                    break;
                }

                char c = (char)ch;
                int charBytes = (c <= 0x7F) ? 1 : ((c <= 0x7FF) ? 2 : 3);
                byteCount += charBytes;

                if (byteCount > maxBytes)
                {
                    lengthExceeded = true;
                    // Drain remaining characters until newline or EOF
                    while (true)
                    {
                        int next = reader.Read();
                        if (next == -1 || next == '\n') break;
                        if (next == '\r')
                        {
                            if (reader.Peek() == '\n') reader.Read();
                            break;
                        }
                    }
                    return null;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
