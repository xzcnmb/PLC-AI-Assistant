using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Engineering.Workers.External;

/// <summary>
/// Low-level abstraction over external process execution, facilitating deterministic mock testing.
/// </summary>
public interface IProcessRunner
{
    IExternalProcess Start(ProcessStartInfo startInfo);
}

public interface IExternalProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    StreamWriter StandardInput { get; }
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    void Kill(bool entireProcessTree = true);
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}

public sealed class DefaultProcessRunner : IProcessRunner
{
    public IExternalProcess Start(ProcessStartInfo startInfo)
    {
        var proc = new Process { StartInfo = startInfo };
        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{startInfo.FileName}'.");
        }
        return new ExternalProcessWrapper(proc);
    }

    private sealed class ExternalProcessWrapper : IExternalProcess
    {
        private readonly Process _process;

        public ExternalProcessWrapper(Process process)
        {
            _process = process;
        }

        public int Id => _process.Id;
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;
        public StreamWriter StandardInput => _process.StandardInput;
        public StreamReader StandardOutput => _process.StandardOutput;
        public StreamReader StandardError => _process.StandardError;

        public void Kill(bool entireProcessTree = true)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            return _process.WaitForExitAsync(cancellationToken);
        }

        public void Dispose()
        {
            _process.Dispose();
        }
    }
}

/// <summary>
/// General-purpose robust JSON-RPC client over standard I/O for external engineering workers.
/// Enforces:
/// - Explicit allowlisted executable paths only
/// - Argument arrays without shell interpolation (UseShellExecute = false)
/// - Strict process lifetime and individual call timeouts
/// - Stdout/stderr streaming with byte quotas (MaxOutputBytes)
/// - Child process tree killing upon cancellation or error
/// - Handshake validation (version, bitness, capabilities evidence)
/// - Hard rejection of unauthenticated/dangerous operations
/// </summary>
public sealed class ExternalWorkerClient : IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly IReadOnlyList<string> _arguments;
    private readonly ExternalWorkerSecurityPolicy _securityPolicy;
    private readonly IProcessRunner _processRunner;

    private IExternalProcess? _process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ExternalRpcResponse>> _pendingRequests = new();
    private readonly StringBuilder _stderrBuffer = new();
    private readonly object _stderrLock = new();
    private long _totalOutputBytesReceived;
    private Task? _stdoutReadLoop;
    private Task? _stderrReadLoop;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _requestIdCounter;
    private bool _isDisposed;

    public WorkerHandshakeResponse? HandshakeData { get; private set; }
    public string StderrTail => GetStderrTail();
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;
    public bool IsRunning => _process != null && !_process.HasExited && !_isDisposed;

    public ExternalWorkerClient(
        string executablePath,
        IReadOnlyList<string>? arguments = null,
        ExternalWorkerSecurityPolicy? securityPolicy = null,
        IProcessRunner? processRunner = null)
    {
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _arguments = arguments ?? Array.Empty<string>();
        _securityPolicy = securityPolicy ?? new ExternalWorkerSecurityPolicy();
        _processRunner = processRunner ?? new DefaultProcessRunner();
    }

    /// <summary>
    /// Starts the external worker process and begins reading stdout/stderr streams.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(ExternalWorkerClient));

        // 1. Validate security policy for executable
        _securityPolicy.ValidateExecutablePath(_executablePath);

        // 2. Prepare ProcessStartInfo strictly with argument array and NO shell execution
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(_executablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in _arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // 3. Start process
        _process = _processRunner.Start(startInfo);

        // 4. Hook up stdout and stderr readers
        _stdoutReadLoop = Task.Run(ReadStdoutAsync, _lifetimeCts.Token);
        _stderrReadLoop = Task.Run(ReadStderrAsync, _lifetimeCts.Token);

        // 5. Setup lifetime timeout watch
        _ = Task.Delay(_securityPolicy.MaxProcessLifetime, _lifetimeCts.Token)
            .ContinueWith(_ =>
            {
                if (!_lifetimeCts.IsCancellationRequested && IsRunning)
                {
                    KillProcessTree("MaxProcessLifetime exceeded");
                }
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// Performs handshake with external worker.
    /// </summary>
    public async Task<WorkerHandshakeResponse> HandshakeAsync(
        string hostVersion = "1.0.0",
        IReadOnlyList<string>? supportedVendors = null,
        CancellationToken cancellationToken = default)
    {
        var req = new WorkerHandshakeRequest(
            ProtocolVersion: "1.0",
            HostVersion: hostVersion,
            SupportedVendors: supportedVendors ?? new[] { "CODESYS", "TIA", "GX", "Sysmac", "InoProShop" });

        var response = await SendRequestAsync<WorkerHandshakeResponse>("handshake", req, cancellationToken);
        if (response == null)
        {
            throw new InvalidOperationException("External worker returned null handshake result.");
        }

        if (string.IsNullOrWhiteSpace(response.WorkerName) || string.IsNullOrWhiteSpace(response.WorkerVersion))
        {
            throw new InvalidOperationException("External worker handshake missing mandatory identification fields (workerName/workerVersion).");
        }

        HandshakeData = response;
        return response;
    }

    /// <summary>
    /// Invokes the 'doctor' diagnostic method on the worker.
    /// </summary>
    public async Task<WorkerDoctorReport> DoctorAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<WorkerDoctorReport>("doctor", new { }, cancellationToken);
        return response ?? new WorkerDoctorReport(
            Healthy: false,
            Installed: false,
            ToolchainPath: null,
            ToolchainVersion: null,
            Bitness: null,
            Details: "Doctor method returned empty response.");
    }

    /// <summary>
    /// Submits an engineering job to the worker.
    /// Guarded by strict allowlist and hard rejection of dangerous physical/network operations.
    /// </summary>
    public async Task<WorkerJobStatusResponse> SubmitJobAsync(
        WorkerSubmitJobRequest request,
        CancellationToken cancellationToken = default)
    {
        // Safety guard: hard reject dangerous operations
        if (ExternalWorkerSecurityPolicy.IsHardRejectedOperation(request.Operation, request.Options, out string reason))
        {
            throw new InvalidOperationException($"Operation rejected by safety policy: {reason}");
        }

        // Validate project path within workspace
        _securityPolicy.ValidateAndSanitizePath(request.ProjectPath);

        var response = await SendRequestAsync<WorkerJobStatusResponse>("submit", request, cancellationToken);
        if (response == null)
        {
            throw new InvalidOperationException("Submit job returned null response.");
        }

        return response;
    }

    /// <summary>
    /// Queries job status.
    /// </summary>
    public async Task<WorkerJobStatusResponse> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<WorkerJobStatusResponse>("status", new { jobId }, cancellationToken);
        if (response == null)
        {
            throw new InvalidOperationException($"Status query for job '{jobId}' returned null.");
        }
        return response;
    }

    /// <summary>
    /// Sends a cancel request for a running job.
    /// </summary>
    public async Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<JsonElement>("cancel", new { jobId }, cancellationToken);
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("cancelled", out var c))
        {
            return c.GetBoolean();
        }
        return true;
    }

    /// <summary>
    /// Retrieves job artifacts descriptor.
    /// </summary>
    public async Task<WorkerArtifactsResponse> GetArtifactsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<WorkerArtifactsResponse>("artifacts", new { jobId }, cancellationToken);
        if (response == null)
        {
            return new WorkerArtifactsResponse(jobId, Array.Empty<WorkerArtifactDescriptor>());
        }
        return response;
    }

    /// <summary>
    /// Generic JSON-RPC method invocation over stdin/stdout.
    /// </summary>
    public async Task<T?> SendRequestAsync<T>(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        if (_process == null || _process.HasExited)
        {
            throw new InvalidOperationException($"External worker process is not running. Exit code: {ExitCode}, Stderr: {StderrTail}");
        }

        string id = Interlocked.Increment(ref _requestIdCounter).ToString();
        var tcs = new TaskCompletionSource<ExternalRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        try
        {
            var rpcReq = ExternalRpcRequest.Create(id, method, parameters);
            string line = JsonSerializer.Serialize(rpcReq);

            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(_securityPolicy.RpcTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _lifetimeCts.Token);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, linkedCts.Token)).ConfigureAwait(false);
            if (completedTask != tcs.Task)
            {
                if (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"RPC method '{method}' timed out after {_securityPolicy.RpcTimeout.TotalSeconds} seconds.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("Operation cancelled or worker terminated.");
            }

            var response = await tcs.Task.ConfigureAwait(false);

            if (response.Error != null)
            {
                throw new InvalidOperationException($"Worker returned RPC error [code {response.Error.Code}]: {response.Error.Message}");
            }

            if (response.Result == null || response.Result.Value.ValueKind == JsonValueKind.Null || response.Result.Value.ValueKind == JsonValueKind.Undefined)
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(response.Result.Value.GetRawText());
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    private async Task ReadStdoutAsync()
    {
        if (_process == null) return;

        try
        {
            while (!_lifetimeCts.IsCancellationRequested && !_process.HasExited)
            {
                string? line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;

                int byteCount = Encoding.UTF8.GetByteCount(line);
                long total = Interlocked.Add(ref _totalOutputBytesReceived, byteCount);
                if (total > _securityPolicy.MaxOutputBytes)
                {
                    KillProcessTree($"MaxOutputBytes exceeded ({total} > {_securityPolicy.MaxOutputBytes})");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var rpcResp = JsonSerializer.Deserialize<ExternalRpcResponse>(line);
                    if (rpcResp != null && rpcResp.Id != null && _pendingRequests.TryGetValue(rpcResp.Id, out var tcs))
                    {
                        tcs.TrySetResult(rpcResp);
                    }
                }
                catch (JsonException)
                {
                    // Ignore non-json debug lines on stdout or log them
                }
            }
        }
        catch (Exception ex)
        {
            FailPendingRequests(ex);
        }
        finally
        {
            FailPendingRequests(new InvalidOperationException($"Worker stdout closed. Exit code: {ExitCode}. Stderr: {StderrTail}"));
        }
    }

    private async Task ReadStderrAsync()
    {
        if (_process == null) return;

        try
        {
            while (!_lifetimeCts.IsCancellationRequested && !_process.HasExited)
            {
                string? line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;

                int byteCount = Encoding.UTF8.GetByteCount(line);
                long total = Interlocked.Add(ref _totalOutputBytesReceived, byteCount);
                if (total > _securityPolicy.MaxOutputBytes)
                {
                    KillProcessTree($"MaxOutputBytes exceeded on stderr ({total} > {_securityPolicy.MaxOutputBytes})");
                    break;
                }

                lock (_stderrLock)
                {
                    if (_stderrBuffer.Length < 64 * 1024)
                    {
                        _stderrBuffer.AppendLine(line);
                    }
                }
            }
        }
        catch
        {
            // Suppress error reader failures
        }
    }

    private void FailPendingRequests(Exception ex)
    {
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }

    private void KillProcessTree(string reason)
    {
        lock (_stderrLock)
        {
            _stderrBuffer.AppendLine($"[HOST KILL]: {reason}");
        }

        try
        {
            _process?.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore
        }
    }

    private string GetStderrTail()
    {
        lock (_stderrLock)
        {
            return _stderrBuffer.ToString();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _lifetimeCts.Cancel();

        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore kill errors during disposal
        }

        FailPendingRequests(new ObjectDisposedException(nameof(ExternalWorkerClient)));

        _process?.Dispose();
        _lifetimeCts.Dispose();
    }
}
