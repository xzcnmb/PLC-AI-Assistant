using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Workspace;

namespace PlcMcp.Engineering.Workers.External;

/// <summary>
/// Configuration for launching and connecting to an external engineering worker executable.
/// </summary>
public sealed class ExternalEngineeringWorkerConfig
{
    public string ExecutablePath { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = new();
    public List<string> AllowedExecutablePaths { get; set; } = new();
    public List<string> AllowedWorkspaceRoots { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;
    public long MaxOutputBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Pinned expected worker name. If null, identity is unpinned and runs as Experimental or fails closed if RequirePinnedIdentity=true.
    /// </summary>
    public string? ExpectedWorkerName { get; set; }

    /// <summary>
    /// Pinned expected worker version.
    /// </summary>
    public string? ExpectedWorkerVersion { get; set; }

    /// <summary>
    /// Pinned expected protocol version (default "1.0").
    /// </summary>
    public string ExpectedProtocolVersion { get; set; } = "1.0";

    /// <summary>
    /// When true, unpinned identity or mismatched identity fails closed (Success=false).
    /// If false, unpinned identity runs only as unverified/Experimental.
    /// Defaults to false.
    /// </summary>
    public bool RequirePinnedIdentity { get; set; } = false;
}

/// <summary>
/// Concrete IEngineeringWorker that interfaces with an external JSON-RPC worker executable.
/// Strictly enforces:
/// - Explicit allowlisted executable path
/// - Argument arrays without shell injection
/// - Sandbox working copies with SHA-256 integrity verification
/// - Stoppage and hard rejection of dangerous online operations (Download/Run/Stop/Force/HmiPublish)
/// - Never pretending to be a vendor compiler when unverified
/// </summary>
public sealed class ExternalEngineeringWorker : IEngineeringWorker
{
    public PlcVendor Vendor { get; }
    public string Name { get; }

    private readonly ExternalEngineeringWorkerConfig _config;
    private readonly IVendorProfileDetector _profileDetector;
    private readonly IProjectWorkspaceManager _workspaceManager;
    private readonly IProcessRunner _processRunner;
    private readonly ExternalWorkerSecurityPolicy _securityPolicy;

    public bool IsAvailable
    {
        get
        {
            var profile = _profileDetector.DetectProfile();
            bool hasExeConfigured = !string.IsNullOrWhiteSpace(_config.ExecutablePath) &&
                                    File.Exists(_config.ExecutablePath) &&
                                    _securityPolicy.AllowedExecutablePaths.Contains(Path.GetFullPath(_config.ExecutablePath));
            return profile.Installed && hasExeConfigured;
        }
    }

    public ExternalEngineeringWorker(
        PlcVendor vendor,
        string name,
        ExternalEngineeringWorkerConfig config,
        IVendorProfileDetector profileDetector,
        IProjectWorkspaceManager workspaceManager,
        IProcessRunner? processRunner = null)
    {
        Vendor = vendor;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _profileDetector = profileDetector ?? throw new ArgumentNullException(nameof(profileDetector));
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _processRunner = processRunner ?? new DefaultProcessRunner();

        _securityPolicy = new ExternalWorkerSecurityPolicy
        {
            RpcTimeout = TimeSpan.FromSeconds(Math.Max(5, _config.TimeoutSeconds)),
            MaxOutputBytes = _config.MaxOutputBytes
        };

        foreach (var p in _config.AllowedExecutablePaths)
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                _securityPolicy.AllowedExecutablePaths.Add(Path.GetFullPath(p));
            }
        }

        foreach (var r in _config.AllowedWorkspaceRoots)
        {
            if (!string.IsNullOrWhiteSpace(r))
            {
                ExternalWorkerSecurityPolicy.ValidateWorkspaceRoot(r);
                _securityPolicy.AllowedWorkspaceRoots.Add(Path.GetFullPath(r));
            }
        }
    }

    public async Task<EngineeringExecutionResult> ExecuteAsync(
        EngineeringJobRequest job,
        CancellationToken cancellationToken = default)
    {
        // 1. Strict hard rejection of dangerous physical/online operations
        string opName = job.JobType.ToString();
        if (ExternalWorkerSecurityPolicy.IsHardRejectedOperation(opName, job.Options, out string rejectionReason))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Operation refused by safety policy.",
                Details: rejectionReason);
        }

        // 2. Validate vendor software installation status
        var profile = _profileDetector.DetectProfile();
        if (!profile.Installed)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"{_profileDetector.ToolchainName} is not installed or detected on host.",
                Details: profile.Details);
        }

        // 3. Validate executable configuration & allowlist
        if (string.IsNullOrWhiteSpace(_config.ExecutablePath))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"No external worker executable is configured for '{Name}'.",
                Details: "Configure an allowlisted worker executable path in external worker configuration.");
        }

        try
        {
            _securityPolicy.ValidateExecutablePath(_config.ExecutablePath);
        }
        catch (Exception ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"External worker executable path validation failed: {ex.Message}");
        }

        // 4. Validate project path within workspace
        string sanitizedProjectPath;
        try
        {
            sanitizedProjectPath = _securityPolicy.ValidateAndSanitizePath(job.ProjectPath);
        }
        catch (Exception ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Project path validation failed: {ex.Message}");
        }

        if (!File.Exists(sanitizedProjectPath))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Project file does not exist: '{sanitizedProjectPath}'");
        }

        // 5. Create isolated read-only working copy for safety
        var snapshot = _workspaceManager.CreateWorkingCopy(sanitizedProjectPath, readOnly: true);

        // Ensure the working copy directory is explicitly added to the security policy allowed workspace roots
        // so that the external worker process and artifact verifications operate on the exact constrained workcopy directory.
        string constrainedWorkDir = Path.GetDirectoryName(snapshot.WorkingPath) ?? snapshot.WorkingPath;
        _securityPolicy.AllowedWorkspaceRoots.Add(Path.GetFullPath(constrainedWorkDir));

        // 6. Connect to external worker client
        await using var client = new ExternalWorkerClient(
            _config.ExecutablePath,
            _config.Arguments,
            _securityPolicy,
            _processRunner);

        try
        {
            await client.StartAsync(cancellationToken);

            // Handshake
            var handshake = await client.HandshakeAsync(
                hostVersion: "1.0.0",
                supportedVendors: new[] { Vendor.ToString(), _profileDetector.VendorName },
                cancellationToken: cancellationToken);

            // 7. Identity & Version pinning verification
            bool hasPinnedIdentity = !string.IsNullOrWhiteSpace(_config.ExpectedWorkerName) ||
                                     !string.IsNullOrWhiteSpace(_config.ExpectedWorkerVersion);

            if (!hasPinnedIdentity)
            {
                if (_config.RequirePinnedIdentity)
                {
                    return new EngineeringExecutionResult(
                        JobId: job.JobId,
                        Success: false,
                        Status: CapabilityStatus.Experimental,
                        Message: "Worker identity is not pinned in configuration (fail-closed).",
                        Details: "ExternalEngineeringWorker requires pinned ExpectedWorkerName and ExpectedWorkerVersion when RequirePinnedIdentity=true.");
                }
            }
            else
            {
                if (!handshake.MatchesExpectation(_config.ExpectedWorkerName, _config.ExpectedWorkerVersion, _config.ExpectedProtocolVersion, out string mismatchReason))
                {
                    return new EngineeringExecutionResult(
                        JobId: job.JobId,
                        Success: false,
                        Status: CapabilityStatus.Experimental,
                        Message: $"Worker identity verification failed: {mismatchReason}",
                        Details: $"Expected Name='{_config.ExpectedWorkerName}', Version='{_config.ExpectedWorkerVersion}', Protocol='{_config.ExpectedProtocolVersion}'. Actual: '{handshake.WorkerName}', '{handshake.WorkerVersion}', '{handshake.ProtocolVersion}'.");
                }
            }

            // Submit job with working copy path
            var submitReq = new WorkerSubmitJobRequest(
                JobId: job.JobId,
                Operation: opName,
                ProjectPath: snapshot.WorkingPath,
                Options: job.Options);

            var status = await client.SubmitJobAsync(submitReq, cancellationToken);

            // Poll status with timeout
            var deadline = DateTime.UtcNow.AddSeconds(_config.TimeoutSeconds);
            while (string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.UtcNow > deadline)
                {
                    await client.CancelJobAsync(job.JobId, CancellationToken.None);
                    throw new TimeoutException($"Job '{job.JobId}' exceeded maximum execution timeout of {_config.TimeoutSeconds}s.");
                }

                await Task.Delay(200, cancellationToken);
                status = await client.GetStatusAsync(job.JobId, cancellationToken);
            }

            bool reportedCompleted = string.Equals(status.State, "completed", StringComparison.OrdinalIgnoreCase);

            // Retrieve artifacts
            var artifactsResp = await client.GetArtifactsAsync(job.JobId, cancellationToken);

            // If operation is CompileProject, worker must produce at least one verifiable artifact.
            // A fake worker merely reporting completed without any artifacts cannot claim compilation succeeded.
            if (job.JobType == EngineeringJobType.CompileProject && reportedCompleted && (artifactsResp.Artifacts == null || artifactsResp.Artifacts.Count == 0))
            {
                return new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Experimental,
                    Message: "Compilation failed: worker reported completion but produced no build artifacts.",
                    Details: $"TrustLevel={WorkerTrustLevel.Untrusted}; Worker '{handshake.WorkerName}' reported state 'completed' with 0 artifacts for CompileProject.");
            }

            // Verify each artifact strictly:
            // Must reside within the constrained workcopy directory, exist physically on disk, and match the declared SHA-256 hash.
            var verifiedArtifactDict = new Dictionary<string, string>();
            if (artifactsResp.Artifacts != null && artifactsResp.Artifacts.Count > 0)
            {
                foreach (var art in artifactsResp.Artifacts)
                {
                    try
                    {
                        string verifiedPath = ExternalWorkerSecurityPolicy.ValidateAndVerifyArtifact(constrainedWorkDir, art);
                        verifiedArtifactDict[art.Name] = verifiedPath;
                    }
                    catch (Exception ex)
                    {
                        return new EngineeringExecutionResult(
                            JobId: job.JobId,
                            Success: false,
                            Status: CapabilityStatus.Experimental,
                            Message: $"Artifact validation failed for '{art.Name}': {ex.Message}",
                            Details: $"TrustLevel={WorkerTrustLevel.Untrusted}; Artifact path: '{art.RelativePath}', SHA: '{art.Sha256}'.");
                    }
                }
            }

            if (!reportedCompleted)
            {
                return new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Experimental,
                    Message: status.Message ?? $"Operation failed with state '{status.State}'.",
                    Details: $"TrustLevel={WorkerTrustLevel.Untrusted}; Worker: {handshake.WorkerName} {handshake.WorkerVersion} ({handshake.Bitness}). ExitCode: {client.ExitCode}. Stderr: {client.StderrTail.Trim()}");
            }

            // Trust calibration:
            // Self-reported completed from an external worker can NEVER be promoted to Supported.
            // Even when valid artifacts are verified, it remains at CapabilityStatus.Experimental.
            var trustLevel = hasPinnedIdentity ? WorkerTrustLevel.Experimental : WorkerTrustLevel.Untrusted;
            var finalStatus = CapabilityStatus.Experimental;

            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: true,
                Status: finalStatus,
                Message: status.Message ?? "Operation completed successfully (External worker, Experimental status).",
                ExportedOutputs: verifiedArtifactDict,
                Details: $"TrustLevel={trustLevel}; Worker: {handshake.WorkerName} {handshake.WorkerVersion} ({handshake.Bitness}). ExitCode: {client.ExitCode}. Stderr: {client.StderrTail.Trim()}");
        }
        catch (Exception ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"External worker execution error: {ex.Message}",
                Details: $"ExitCode: {client.ExitCode}, Stderr: {client.StderrTail.Trim()}");
        }
    }
}
