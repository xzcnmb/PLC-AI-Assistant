using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlcMcp.Engineering.Workers.Siemens;

public interface ISiemensSmartBridge
{
    bool IsAvailable { get; }
    string? ResolvedPythonPath { get; }
    string? ResolvedMicroWinPath { get; }
    Task<string> ExecuteCommandAsync(string commandName, IReadOnlyDictionary<string, string> args, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class SiemensSmartBridge : ISiemensSmartBridge
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly SiemensSmartBridgeConfig _config;
    private string? _resolvedPythonPath;
    private string? _resolvedMicroWinPath;
    private bool? _isAvailable;

    public string? ResolvedPythonPath => _resolvedPythonPath;
    public string? ResolvedMicroWinPath => _resolvedMicroWinPath;

    public bool IsAvailable
    {
        get
        {
            if (_isAvailable.HasValue)
                return _isAvailable.Value;

            InitializePaths();
            return _isAvailable ?? false;
        }
    }

    public SiemensSmartBridge(SiemensSmartBridgeConfig? config = null)
    {
        _config = config ?? new SiemensSmartBridgeConfig();
    }

    private void InitializePaths()
    {
        _resolvedPythonPath = FindPythonExecutable();
        if (_resolvedPythonPath == null || !File.Exists(_resolvedPythonPath))
        {
            _isAvailable = false;
            return;
        }

        // Verify smart200_mcp can be imported and check paths
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _resolvedPythonPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import smart200_mcp.paths as p; print('OK|' + (p.mwsmart() or '') + '|' + str(p.is_v28()))");

            if (!string.IsNullOrWhiteSpace(_config.Smart200PackagePath))
            {
                psi.EnvironmentVariables["PYTHONPATH"] = _config.Smart200PackagePath;
            }

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _isAvailable = false;
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            bool exited = proc.WaitForExit(5000);
            if (!exited)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore kill failure
                }

                _isAvailable = false;
                return;
            }

            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(2));
            string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

            if (proc.ExitCode == 0)
            {
                // Warnings may precede or follow the OK line in stdout/stderr, split lines and look for OK|
                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var okLine = lines.FirstOrDefault(l => l.StartsWith("OK|", StringComparison.OrdinalIgnoreCase));
                if (okLine != null)
                {
                    var parts = okLine.Split('|');
                    if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        _resolvedMicroWinPath = parts[1];
                    }
                    _isAvailable = true;
                    return;
                }
            }
        }
        catch
        {
            // Ignore failure during discovery
        }

        _isAvailable = false;
    }

    private string? FindPythonExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_config.PythonExecutablePath))
        {
            return File.Exists(_config.PythonExecutablePath) ? Path.GetFullPath(_config.PythonExecutablePath) : null;
        }

        // Candidate paths on this system
        var candidates = new[]
        {
            @"C:\Users\ASUS\AppData\Local\hermes\hermes-agent\venv\Scripts\python.exe",
            @"C:\Python312\python.exe",
            @"C:\Python311\python.exe",
            @"C:\Python310\python.exe",
            @"C:\Program Files\Python312\python.exe",
            @"C:\Program Files\Python311\python.exe"
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return Path.GetFullPath(c);
        }

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var testExe = Path.Combine(p, "python.exe");
            if (File.Exists(testExe))
                return Path.GetFullPath(testExe);
        }

        return null;
    }

    public static readonly string LockFilePath = Path.Combine(Path.GetTempPath(), "PlcMcp_SiemensSmartBridge.lock");

    public static async Task<FileStream> AcquireLockAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? customLockFilePath = null)
    {
        string lockPath = customLockFilePath ?? LockFilePath;
        var sw = Stopwatch.StartNew();
        int attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sw.Elapsed >= timeout)
            {
                throw new TimeoutException($"Timed out waiting for cross-process execution lock file '{lockPath}' ({timeout.TotalSeconds:F1}s).");
            }

            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);

                return stream;
            }
            catch (IOException)
            {
                // Lock held by another thread/process
            }
            catch (UnauthorizedAccessException)
            {
                // Lock held or ACL temporary collision
            }

            attempt++;
            int delayMs = attempt switch
            {
                < 5 => 10,
                < 15 => 25,
                < 30 => 50,
                _ => 100
            };

            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Timed out waiting for cross-process execution lock file '{lockPath}' ({timeout.TotalSeconds:F1}s).");
            }

            int waitMs = Math.Min(delayMs, (int)Math.Max(1, remaining.TotalMilliseconds));
            await Task.Delay(waitMs, cancellationToken);
        }
    }

    public async Task<string> ExecuteCommandAsync(
        string commandName,
        IReadOnlyDictionary<string, string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrEmpty(_resolvedPythonPath))
        {
            throw new InvalidOperationException("Siemens Smart backend is unavailable (Python / smart200_mcp not found or invalid).");
        }

        var sw = Stopwatch.StartNew();

        FileStream lockStream;
        try
        {
            lockStream = await AcquireLockAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Smart bridge command '{commandName}' timed out waiting for cross-process execution lock ({timeout.TotalSeconds:F1}s).");
        }

        await using (lockStream.ConfigureAwait(false))
        {
            var remainingTimeout = timeout - sw.Elapsed;
            if (remainingTimeout <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Smart bridge command '{commandName}' timed out waiting for cross-process execution lock.");
            }

            return await ExecuteCommandInternalAsync(commandName, args, remainingTimeout, cancellationToken);
        }
    }

    private async Task<string> ExecuteCommandInternalAsync(
        string commandName,
        IReadOnlyDictionary<string, string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Script runner using fixed python code template, passing JSON input via stdin or temp file
        // To be completely immune to shell quoting or injection, write arguments to a temporary payload file
        string tempDir = Path.Combine(Path.GetTempPath(), "PlcMcp_SmartBridge_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string payloadFile = Path.Combine(tempDir, "request.json");
            string responseFile = Path.Combine(tempDir, "response.json");

            var payloadObj = new JsonObject
            {
                ["command"] = commandName,
                ["args"] = JsonSerializer.SerializeToNode(args, JsonOpts)
            };

            await File.WriteAllTextAsync(payloadFile, payloadObj.ToJsonString(JsonOpts), cancellationToken);

            // Fixed python bootstrap script that reads payloadFile, invokes smart200_mcp API, and writes responseFile
            string scriptFile = Path.Combine(tempDir, "runner.py");
            await File.WriteAllTextAsync(scriptFile, RunnerPythonScript, cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = _resolvedPythonPath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add(scriptFile);
            psi.ArgumentList.Add(payloadFile);
            psi.ArgumentList.Add(responseFile);

            if (!string.IsNullOrWhiteSpace(_config.Smart200PackagePath))
            {
                psi.EnvironmentVariables["PYTHONPATH"] = _config.Smart200PackagePath;
            }

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var readStdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var readStderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(timeout);

            try
            {
                await proc.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore kill failure
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Smart bridge execution was canceled by caller.", cancellationToken);
                }
                throw new TimeoutException($"Smart bridge command '{commandName}' timed out after {timeout.TotalSeconds:F1}s.");
            }

            string stdout = await readStdoutTask;
            string stderr = await readStderrTask;

            if (proc.ExitCode != 0)
            {
                string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException($"Smart bridge worker process exited with code {proc.ExitCode}: {err.Trim()}");
            }

            if (!File.Exists(responseFile))
            {
                throw new InvalidOperationException($"Smart bridge response file not generated. Output: {stdout} {stderr}");
            }

            return await File.ReadAllTextAsync(responseFile, cancellationToken);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best effort
            }
        }
    }

    private const string RunnerPythonScript = @"# -*- coding: utf-8 -*-
import sys
import json
import os
import shutil

sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')

payload_path = sys.argv[1]
response_path = sys.argv[2]

with open(payload_path, 'r', encoding='utf-8') as f:
    req = json.load(f)

cmd = req.get('command')
args = req.get('args', {})

result = {}
try:
    if cmd == 'probe':
        import smart200_mcp.container as container
        result = container.probe(args['path'])

    elif cmd == 'overview':
        import smart200_mcp.container as container
        import smart200_mcp.project as project
        p = args['path']
        probe_res = container.probe(p)
        if not probe_res.get('offline_parsable', False):
            result = {
                'success': True,
                'path': p,
                'format': probe_res.get('format', 'unknown'),
                'version': probe_res.get('version'),
                'offline_parsable': False,
                'reason': probe_res.get('reason'),
                'symbol_count': 0,
                'pou_names': [],
                'function_block_kinds': 0,
                'function_block_total': 0,
                'top_function_blocks': [],
                'limitations': 'V3 (.smartV3) data segment is encrypted. Offline overview limited.'
            }
        else:
            proj = container.load(p)
            sm = project.summary(proj)
            result = {
                'success': True,
                'path': p,
                'format': probe_res.get('format', 'V2'),
                'version': sm.get('container_version'),
                'internal_version': sm.get('internal_version'),
                'project_name': sm.get('project_name'),
                'decompressed_bytes': sm.get('decompressed_bytes', 0),
                'system_table_found': sm.get('system_table_found', False),
                'symbol_count': sm.get('symbol_count', 0),
                'pou_names': sm.get('pou_names', []),
                'function_block_kinds': sm.get('function_block_kinds', 0),
                'function_block_total': sm.get('function_block_total', 0),
                'top_function_blocks': sm.get('top_function_blocks', []),
                'limitations': sm.get('limitations', '')
            }

    elif cmd == 'check_stl':
        import smart200_mcp.stlcheck as stlcheck
        awl_path = args['awl_path']
        chk = stlcheck.check_file(awl_path)
        result = {
            'success': True,
            'awl_path': awl_path,
            'valid': chk.get('valid', False),
            'total': chk.get('total', 0),
            'invalid_count': chk.get('invalid_count', 0),
            'invalid': chk.get('invalid', [])
        }

    elif cmd == 'analyze_awl':
        import smart200_mcp.awl as awl
        import smart200_mcp.autoflow as af
        awl_path = args['awl_path']
        parsed = awl.parse(af._read(awl_path))
        an = awl.analyze(parsed)
        result = {
            'success': True,
            'analysis': an
        }

    elif cmd == 'validate_project':
        import smart200_mcp.autoflow as autoflow
        proj_path = args['project_path']
        block_names = [b.strip() for b in args['block_names'].split(',') if b.strip()]
        res = autoflow.validate_project(proj_path, block_names)
        result = {
            'success': True,
            'completed': res.get('completed', False),
            'all_valid': res.get('all_valid', False),
            'blocks': res.get('blocks', {})
        }

    elif cmd == 'export_blocks':
        import smart200_mcp.engine as engine
        proj_path = args['project_path']
        names_to_paths = json.loads(args['names_to_paths'])
        exported, log = engine.export_blocks(proj_path, names_to_paths)
        result = {
            'success': True,
            'exported': exported,
            'log': log
        }

    elif cmd == 'compile_and_export':
        import smart200_mcp.engine as engine
        proj_path = args['project_path']
        names_to_paths = json.loads(args['names_to_paths'])
        compiled, exported, log = engine.compile_and_export(proj_path, names_to_paths)
        result = {
            'success': True,
            'compiled': compiled,
            'exported': exported,
            'log': log
        }

    elif cmd == 'deploy_verify':
        import smart200_mcp.autoflow as autoflow
        proj_path = args['project_path']
        awl_files = [f.strip() for f in args['awl_files'].split('|') if f.strip()]
        symbols = json.loads(args.get('symbols', '{}'))
        rep = autoflow.deploy(awl_files, project_path=proj_path, symbols=symbols if symbols else None)
        result = {
            'success': True,
            'report': rep
        }

    else:
        raise ValueError(f'Unknown command: {cmd}')

except Exception as ex:
    result = {
        'success': False,
        'error': str(ex),
        'type': ex.__class__.__name__
    }

with open(response_path, 'w', encoding='utf-8') as f:
    json.dump(result, f, ensure_ascii=False, indent=2)
";
}
