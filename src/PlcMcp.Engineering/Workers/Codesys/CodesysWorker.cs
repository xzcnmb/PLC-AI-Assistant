using System.Security.Cryptography;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Workers.External;
using PlcMcp.Engineering.Workspace;
using ExternalDoctorCheckItem = PlcMcp.Engineering.Workers.External.WorkerDoctorCheckItem;

namespace PlcMcp.Engineering.Workers.Codesys;

/// <summary>
/// Offline-only CODESYS worker backed by the locally installed ScriptEngine.
/// </summary>
public sealed class CodesysWorker : IEngineeringWorker
{
    private static readonly HashSet<EngineeringJobType> AllowedJobs =
    [
        EngineeringJobType.InspectProject,
        EngineeringJobType.ExportPou,
        EngineeringJobType.ValidatePou,
        EngineeringJobType.CompileProject
    ];

    private readonly CodesysWorkerConfig _config;
    private readonly IVendorProfileDetector _profileDetector;
    private readonly IProjectWorkspaceManager _workspaceManager;
    private readonly IProcessRunner _processRunner;
    private readonly Lazy<Task<WorkerDoctorReport>> _startupEvidence;
    private readonly ExternalEngineeringWorker _external;

    public PlcVendor Vendor => PlcVendor.Generic;
    public string Name => _config.ExpectedWorkerName;

    public bool IsAvailable => CheckDoctor().Healthy;

    public CodesysWorker(CodesysWorkerConfig config)
        : this(config, null, null, null)
    {
    }

    public CodesysWorker(
        CodesysWorkerConfig config,
        IVendorProfileDetector? profileDetector = null,
        IProjectWorkspaceManager? workspaceManager = null,
        IProcessRunner? processRunner = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _profileDetector = profileDetector ?? new CodesysProfileDetector();
        _workspaceManager = workspaceManager ?? new ProjectWorkspaceManager(
            Path.Combine(string.IsNullOrWhiteSpace(config.ProjectRoot)
                ? Path.GetTempPath()
                : Path.GetFullPath(config.ProjectRoot), ".plcmcp-workspaces"));
        _processRunner = processRunner ?? new DefaultProcessRunner();
        _startupEvidence = new Lazy<Task<WorkerDoctorReport>>(
            ProbeStartupAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);

        _external = new ExternalEngineeringWorker(
            Vendor,
            Name,
            config.ToExternalWorkerConfig(),
            _profileDetector,
            _workspaceManager,
            _processRunner);
    }

    /// <summary>
    /// Checks the pinned local files, then starts CODESYS without opening a project
    /// and validates the ScriptEngine handshake. The result is cached for this worker.
    /// </summary>
    public WorkerDoctorReport CheckDoctor(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var staticCheck = CheckConfiguration(out var checks);
        var profile = _profileDetector.DetectProfile();
        if (!staticCheck.Healthy)
        {
            return new WorkerDoctorReport(
                Healthy: false,
                Installed: profile.Installed,
                ToolchainPath: profile.ExecutablePath,
                ToolchainVersion: profile.Version,
                Bitness: profile.Bitness,
                Details: staticCheck.Details,
                Checks: checks);
        }

        try
        {
            var startup = _startupEvidence.Value.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            return startup;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failedChecks = checks.ToList();
            failedChecks.Add(new ExternalDoctorCheckItem("scriptengine_handshake", false, ex.Message));
            return new WorkerDoctorReport(
                Healthy: false,
                Installed: profile.Installed,
                ToolchainPath: profile.ExecutablePath,
                ToolchainVersion: profile.Version,
                Bitness: profile.Bitness,
                Details: $"CODESYS ScriptEngine startup probe failed: {ex.Message}",
                Checks: failedChecks);
        }
    }

    public async Task<EngineeringExecutionResult> ExecuteAsync(
        EngineeringJobRequest job,
        CancellationToken cancellationToken = default)
    {
        if (!AllowedJobs.Contains(job.JobType))
        {
            return Rejected(job, $"CODESYS offline worker does not support job type '{job.JobType}'.");
        }

        if (ContainsProhibitedOption(job.Options, out var reason))
        {
            return Rejected(job, reason);
        }

        WorkerDoctorReport doctor;
        try
        {
            doctor = await _startupEvidence.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotReady(job, ex.Message);
        }

        if (!doctor.Healthy)
        {
            return NotReady(job, doctor.Details);
        }

        if (job.Vendor != PlcVendor.Generic && job.Vendor != Vendor)
        {
            return Rejected(job, $"CODESYS worker cannot execute a job for vendor '{job.Vendor}'.");
        }

        return await _external.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkerDoctorReport> ProbeStartupAsync()
    {
        var profile = _profileDetector.DetectProfile();
        var checks = new List<ExternalDoctorCheckItem>();

        try
        {
            await using var client = new ExternalWorkerClient(
                _config.CodesysExePath,
                securityPolicy: CreateProbePolicy(),
                processRunner: _processRunner,
                rawArguments: _config.BuildRawArguments(),
                gracefulShutdown: true);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_config.StartupProbeTimeoutSeconds, 5, 90)));
            await client.StartAsync(timeout.Token).ConfigureAwait(false);
            var handshake = await client.HandshakeAsync(
                hostVersion: "1.0.0",
                supportedVendors: new[] { "CODESYS" },
                cancellationToken: timeout.Token).ConfigureAwait(false);

            bool identityMatches = handshake.MatchesExpectation(
                _config.ExpectedWorkerName,
                _config.ExpectedWorkerVersion,
                _config.ExpectedProtocolVersion,
                out string identityReason);
            checks.Add(new("worker_identity", identityMatches,
                identityMatches ? $"Worker '{handshake.WorkerName}' {handshake.WorkerVersion} matched the pinned identity."
                    : identityReason));

            bool profileMatches = handshake.RuntimeEnvironment.Contains(_config.ProfileName, StringComparison.OrdinalIgnoreCase);
            checks.Add(new("scriptengine_profile", profileMatches,
                profileMatches
                    ? $"ScriptEngine reported the pinned profile '{_config.ProfileName}'."
                    : $"ScriptEngine runtime did not report configured profile '{_config.ProfileName}': {handshake.RuntimeEnvironment}"));

            bool vendorMatches = string.Equals(handshake.Vendor, "CODESYS", StringComparison.OrdinalIgnoreCase);
            checks.Add(new("scriptengine_vendor", vendorMatches, vendorMatches
                ? "ScriptEngine handshake reported CODESYS."
                : $"Unexpected worker vendor '{handshake.Vendor}'."));

            bool capabilitiesPresent = handshake.Capabilities.Contains("InspectProject") &&
                                       handshake.Capabilities.Contains("ExportPou") &&
                                       handshake.Capabilities.Contains("CompileProject");
            checks.Add(new("offline_capabilities", capabilitiesPresent, capabilitiesPresent
                ? "Worker advertised the required offline capabilities."
                : "Worker handshake is missing one or more required offline capabilities."));

            var report = await client.DoctorAsync(timeout.Token).ConfigureAwait(false);
            checks.Add(new("worker_doctor", report.Healthy, report.Details));

            bool healthy = identityMatches && profileMatches && vendorMatches && capabilitiesPresent && report.Healthy;
            string details = healthy
                ? $"CODESYS ScriptEngine handshake succeeded ({handshake.RuntimeEnvironment}); no project was opened and no online API was called."
                : string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Message));

            return new WorkerDoctorReport(
                Healthy: healthy,
                Installed: profile.Installed,
                ToolchainPath: profile.ExecutablePath,
                ToolchainVersion: profile.Version,
                Bitness: handshake.Bitness,
                Details: details,
                Checks: checks);
        }
        catch (Exception ex)
        {
            checks.Add(new("scriptengine_handshake", false, ex.Message));
            return new WorkerDoctorReport(
                Healthy: false,
                Installed: profile.Installed,
                ToolchainPath: profile.ExecutablePath,
                ToolchainVersion: profile.Version,
                Bitness: profile.Bitness,
                Details: $"CODESYS ScriptEngine startup probe failed: {ex.Message}",
                Checks: checks);
        }
    }

    private ExternalWorkerSecurityPolicy CreateProbePolicy()
    {
        var policy = new ExternalWorkerSecurityPolicy
        {
            RpcTimeout = TimeSpan.FromSeconds(Math.Clamp(_config.StartupProbeTimeoutSeconds, 5, 90)),
            MaxOutputBytes = Math.Min(_config.MaxOutputBytes, 2 * 1024 * 1024),
            MaxProcessLifetime = TimeSpan.FromSeconds(Math.Clamp(_config.StartupProbeTimeoutSeconds + 5, 10, 100))
        };
        policy.AllowedExecutablePaths.Add(Path.GetFullPath(_config.CodesysExePath));
        return policy;
    }

    private (bool Healthy, string Details) CheckConfiguration(out IReadOnlyList<ExternalDoctorCheckItem> checks)
    {
        var items = new List<ExternalDoctorCheckItem>();
        var profile = _profileDetector.DetectProfile();

        bool exeConfigured = !string.IsNullOrWhiteSpace(_config.CodesysExePath) && File.Exists(_config.CodesysExePath);
        items.Add(new("codesys_executable", exeConfigured, exeConfigured
            ? $"CODESYS executable exists at '{_config.CodesysExePath}'."
            : "Configured CODESYS executable is missing."));

        bool profileMatch = profile.Installed && exeConfigured &&
            string.Equals(Path.GetFullPath(profile.ExecutablePath ?? string.Empty),
                Path.GetFullPath(_config.CodesysExePath), StringComparison.OrdinalIgnoreCase);
        items.Add(new("detector_identity", profileMatch, profileMatch
            ? $"Detector found the configured executable ({profile.Version}, {profile.Bitness})."
            : "Detector did not find the configured CODESYS executable."));

        bool versionMatch = !string.IsNullOrWhiteSpace(_config.CodesysVersion) &&
            (string.Equals(profile.Version, _config.CodesysVersion, StringComparison.OrdinalIgnoreCase) ||
             (profile.Version?.StartsWith(_config.CodesysVersion, StringComparison.OrdinalIgnoreCase) ?? false));
        items.Add(new("version_pin", versionMatch, versionMatch
            ? $"Version pin '{_config.CodesysVersion}' is satisfied."
            : $"Expected a matching CODESYS version pin; actual '{profile.Version}', configured '{_config.CodesysVersion}'."));

        bool profileName = !string.IsNullOrWhiteSpace(_config.ProfileName);
        items.Add(new("profile_name", profileName, profileName
            ? $"Profile '{_config.ProfileName}' is configured."
            : "CODESYS profile name is required."));

        bool projectRoot = !string.IsNullOrWhiteSpace(_config.ProjectRoot) &&
            Directory.Exists(_config.ProjectRoot) &&
            !ExternalWorkerSecurityPolicy.IsBroadOrDangerousRoot(_config.ProjectRoot, out _);
        items.Add(new("project_root", projectRoot, projectRoot
            ? $"Project root '{_config.ProjectRoot}' is bounded."
            : "Project root is missing, broad, or does not exist."));

        bool scriptExists = !string.IsNullOrWhiteSpace(_config.DriverScriptPath) && File.Exists(_config.DriverScriptPath);
        bool scriptHash = false;
        string hashDetails = "Driver script is missing.";
        if (scriptExists)
        {
            try
            {
                string actual = ComputeSha256(_config.DriverScriptPath);
                scriptHash = IsSha256(_config.DriverScriptSha256) &&
                    string.Equals(actual, _config.DriverScriptSha256, StringComparison.OrdinalIgnoreCase);
                hashDetails = scriptHash
                    ? $"Driver script SHA-256 verified: {actual}."
                    : $"Driver script SHA-256 mismatch: expected '{_config.DriverScriptSha256}', actual '{actual}'.";
            }
            catch (Exception ex)
            {
                hashDetails = $"Driver script hash could not be read: {ex.Message}";
            }
        }
        items.Add(new("script_sha256", scriptHash, hashDetails));

        bool rawArgsValid = true;
        string rawArgsDetails = "Raw CODESYS arguments are valid.";
        try
        {
            ExternalWorkerSecurityPolicy.ValidateRawArguments(_config.BuildRawArguments());
        }
        catch (Exception ex)
        {
            rawArgsValid = false;
            rawArgsDetails = ex.Message;
        }
        items.Add(new("raw_arguments", rawArgsValid, rawArgsDetails));

        bool healthy = exeConfigured && profileMatch && versionMatch && profileName && projectRoot && scriptHash && rawArgsValid;
        checks = items;
        string failed = string.Join("; ", items.Where(x => !x.Passed).Select(x => x.Name + ": " + x.Message));
        return (healthy, healthy ? "CODESYS local configuration checks passed." : failed);
    }

    private static bool ContainsProhibitedOption(IReadOnlyDictionary<string, string>? options, out string reason)
    {
        if (options is not null)
        {
            foreach (var pair in options)
            {
                string text = $"{pair.Key}={pair.Value}".ToLowerInvariant();
                string[] forbidden = ["online", "login", "logout", "start", "stop", "reset", "force", "download", "upload", "save", "import", "delete", "move", "write"];
                var hit = forbidden.FirstOrDefault(text.Contains);
                if (hit is not null)
                {
                    reason = $"CODESYS offline worker rejected option '{pair.Key}': '{hit}' is not an offline operation.";
                    return true;
                }
            }
        }
        reason = string.Empty;
        return false;
    }

    private static EngineeringExecutionResult Rejected(EngineeringJobRequest job, string reason) =>
        new(job.JobId, false, CapabilityStatus.Unsupported, "Operation refused by CODESYS offline safety policy.", Details: reason);

    private static EngineeringExecutionResult NotReady(EngineeringJobRequest job, string details) =>
        new(job.JobId, false, CapabilityStatus.Unsupported, "CODESYS offline worker is not ready.", Details: details);

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
