using System.Security.Cryptography;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Workers.External;
using ExternalDoctorCheckItem = PlcMcp.Engineering.Workers.External.WorkerDoctorCheckItem;

namespace PlcMcp.Engineering.Workers.GxWorks3;

/// <summary>
/// Engineering worker for Mitsubishi GX Works3 acting as a P0/P1 Safety Gate.
///
/// Principles & Invariants:
/// 1. Phase 0/1: Zero internal DLL loading, zero process launching of GXW3, zero project file opening, zero online PLC calls, zero ServiceBus invocation.
/// 2. Performs read-only static doctor/health checks and configuration validation.
/// 3. Offline allowlist mapping: InspectProject, ExportPou, ValidatePou, DiffProjects are the only recognized offline candidate jobs.
/// 4. Honest reporting: Because internal ServiceBus and native APIs are unverified, ExecuteAsync returns Unsupported for ALL engineering jobs.
/// 5. Hard rejection: Any dangerous physical/online operations (Download, Run, Stop, Force, Import, Write, Password, MemoryDump, etc.) are immediately refused.
/// </summary>
public sealed class GxWorks3Worker : IEngineeringWorker
{
    private static readonly HashSet<EngineeringJobType> OfflineCandidateJobs =
    [
        EngineeringJobType.InspectProject,
        EngineeringJobType.ExportPou,
        EngineeringJobType.ValidatePou,
        EngineeringJobType.DiffProjects
    ];

    private readonly GxWorks3WorkerConfig _config;
    private readonly IVendorProfileDetector _profileDetector;
    private readonly ExternalWorkerSecurityPolicy _securityPolicy;

    public PlcVendor Vendor => PlcVendor.Mitsubishi;
    public string Name => "GxWorks3-Static-Worker";

    public bool IsAvailable => false; // P0 阶段恒为 false，表达无工程执行能力

    public GxWorks3Worker(GxWorks3WorkerConfig config, IVendorProfileDetector? profileDetector = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _profileDetector = profileDetector ?? new GxWorksProfileDetector(config.Gxw3ExePath);

        _securityPolicy = new ExternalWorkerSecurityPolicy
        {
            RpcTimeout = TimeSpan.FromSeconds(Math.Max(5, _config.TimeoutSeconds)),
            MaxOutputBytes = _config.MaxOutputBytes
        };

        if (!string.IsNullOrWhiteSpace(_config.Gxw3ExePath) && Path.IsPathRooted(_config.Gxw3ExePath))
        {
            try
            {
                _securityPolicy.AllowedExecutablePaths.Add(Path.GetFullPath(_config.Gxw3ExePath));
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(_config.ProjectRoot) && Path.IsPathRooted(_config.ProjectRoot))
        {
            try
            {
                if (!ExternalWorkerSecurityPolicy.IsBroadOrDangerousRoot(_config.ProjectRoot, out _))
                {
                    _securityPolicy.AllowedWorkspaceRoots.Add(Path.GetFullPath(_config.ProjectRoot));
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Performs a non-invasive static doctor inspection on the local environment and configuration.
    /// Does NOT launch any processes, does NOT load vendor assemblies, and does NOT alter disk state.
    /// </summary>
    public WorkerDoctorReport CheckDoctor(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (healthy, details, checks) = CheckConfiguration();
        var profile = _profileDetector.DetectProfile();

        return new WorkerDoctorReport(
            Healthy: healthy,
            Installed: profile.Installed,
            ToolchainPath: profile.ExecutablePath ?? (File.Exists(_config.Gxw3ExePath) ? Path.GetFullPath(_config.Gxw3ExePath) : null),
            ToolchainVersion: profile.Version ?? _config.Gxw3Version,
            Bitness: profile.Bitness ?? (string.IsNullOrWhiteSpace(profile.ExecutablePath) ? null : BaseVendorProfileDetector.DetectPeBitness(profile.ExecutablePath)),
            Details: details,
            Checks: checks);
    }

    /// <summary>
    /// Executes an engineering job under the P0/P1 Safety Gate.
    /// </summary>
    public Task<EngineeringExecutionResult> ExecuteAsync(
        EngineeringJobRequest job,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Strict options allowlist: P0 does not support ANY options; reject non-empty options dictionaries immediately
        if (job.Options != null && job.Options.Count > 0)
        {
            var firstKey = job.Options.Keys.FirstOrDefault() ?? "unknown";
            return Task.FromResult(new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Security violation: no options are supported under GxWorks3 P0 safety policy. Received option '{firstKey}'.",
                Details: $"Rejected option '{firstKey}' with value '{job.Options[firstKey]}'. Options must be null or empty."));
        }

        // 2. Reject dangerous physical/online operations and dangerous operation names
        string opName = job.JobType.ToString();
        if (IsDangerousOrOnlineOperation(opName, job.Options, out string rejectionReason))
        {
            return Task.FromResult(new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Operation refused by Mitsubishi GX Works3 safety policy.",
                Details: rejectionReason));
        }

        // 3. Reject mismatched vendor
        if (job.Vendor != PlcVendor.Mitsubishi && job.Vendor != PlcVendor.Generic)
        {
            return Task.FromResult(new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"GxWorks3Worker cannot execute a job for vendor '{job.Vendor}'. Expected Mitsubishi.",
                Details: "Vendor mismatch."));
        }

        // 4. Reject jobs that are not in the supported offline mapping candidates (e.g. CompileProject or unknown)
        if (!OfflineCandidateJobs.Contains(job.JobType))
        {
            return Task.FromResult(new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"GxWorks3Worker does not support job type '{job.JobType}'.",
                Details: $"Job '{job.JobType}' is not an allowed offline candidate for Mitsubishi GX Works3. Allowed offline candidates: {string.Join(", ", OfflineCandidateJobs)}."));
        }

        // 5. Validate doctor / configuration readiness
        var doctor = CheckDoctor(cancellationToken);
        if (!doctor.Healthy)
        {
            return Task.FromResult(new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "GxWorks3Worker configuration or environment is not ready.",
                Details: doctor.Details));
        }

        // 6. Honest reporting: ServiceBus and internal APIs are unverified in this initial release.
        // In P0, we do not touch, read, or open any customer project files.
        return Task.FromResult(new EngineeringExecutionResult(
            JobId: job.JobId,
            Success: false,
            Status: CapabilityStatus.Unsupported,
            Message: $"Offline operation '{job.JobType}' is recognized as an offline candidate, but execution is currently Unsupported: GX Works3 ServiceBus integration is unverified on this host.",
            Details: "Phase 0/1 Safety Gate active: no GX DLLs loaded, no GXW3.exe process launched, no project files read or opened. To prevent workspace corruption or crash, execution is refused until the internal ServiceBus integration has undergone closed verification."));
    }

    /// <summary>
    /// Checks for dangerous physical/online operations, write options, download, force, password, import, etc.
    /// </summary>
    public static bool IsDangerousOrOnlineOperation(
        string operation,
        IReadOnlyDictionary<string, string>? options,
        out string rejectionReason)
    {
        // Extended security prohibitions for GX Works3
        string[] forbiddenOps =
        [
            "Download", "DownloadProject", "Upload", "UploadProject", "Run", "Stop", "RunStop",
            "SetRunMode", "Force", "ForceIo", "HmiPublish", "PublishHmi", "Write", "WritePlc",
            "OnlineMonitor", "OnlineConnect", "MemoryDump", "DumpMemory", "SetPassword",
            "ClearPassword", "UnlockPlc", "Import", "ImportPou", "ImportProject"
        ];

        foreach (var f in forbiddenOps)
        {
            if (string.Equals(operation, f, StringComparison.OrdinalIgnoreCase))
            {
                rejectionReason = $"Operation '{operation}' is classified as a dangerous online/write/physical modification action and is strictly hard-rejected by GxWorks3 safety policy.";
                return true;
            }
        }

        if (options != null)
        {
            string[] dangerousKeywords =
            [
                "download", "upload", "online", "run", "stop", "force", "write", "flash",
                "dump", "password", "unlock", "import", "servicebus_invoke", "eval", "raw_exec", "plc_write", "hmipublish"
            ];

            foreach (var kvp in options)
            {
                string key = kvp.Key.ToLowerInvariant();
                string val = kvp.Value?.ToLowerInvariant() ?? string.Empty;

                foreach (var kw in dangerousKeywords)
                {
                    if (key.Contains(kw) || val.Contains(kw))
                    {
                        rejectionReason = $"Security violation: option '{kvp.Key}' contains dangerous keyword '{kw}'. Hardware modification, online communication, and write operations are hard-rejected.";
                        return true;
                    }
                }
            }
        }

        // Check standard external worker hard rejections as fallback
        if (ExternalWorkerSecurityPolicy.IsHardRejectedOperation(operation, options, out string stdReason))
        {
            rejectionReason = stdReason;
            return true;
        }

        rejectionReason = string.Empty;
        return false;
    }

    private (bool Healthy, string Details, IReadOnlyList<ExternalDoctorCheckItem> Checks) CheckConfiguration()
    {
        var items = new List<ExternalDoctorCheckItem>();
        var profile = _profileDetector.DetectProfile();

        // 1. Executable configured, absolute path, and exists
        bool exeConfigured = !string.IsNullOrWhiteSpace(_config.Gxw3ExePath) &&
                             Path.IsPathRooted(_config.Gxw3ExePath) &&
                             File.Exists(_config.Gxw3ExePath);
        items.Add(new("gxw3_executable", exeConfigured, exeConfigured
            ? $"GXW3 executable exists at '{_config.Gxw3ExePath}'."
            : $"Configured GXW3 executable is missing, relative, or not found at '{_config.Gxw3ExePath}'."));

        // 2. Installation root configured, absolute, and exists
        bool installRootValid = !string.IsNullOrWhiteSpace(_config.InstallationRoot) &&
                                Path.IsPathRooted(_config.InstallationRoot) &&
                                Directory.Exists(_config.InstallationRoot);
        items.Add(new("installation_root", installRootValid, installRootValid
            ? $"Installation root exists at '{_config.InstallationRoot}'."
            : $"Installation root is missing, relative, or does not exist: '{_config.InstallationRoot}'."));

        // 3. Managed directory configured, absolute, exists, and is inside InstallationRoot
        bool managedDirValid = false;
        string managedDirMsg = "Managed directory is missing or relative.";
        if (!string.IsNullOrWhiteSpace(_config.ManagedDirectory) && Path.IsPathRooted(_config.ManagedDirectory))
        {
            if (Directory.Exists(_config.ManagedDirectory))
            {
                if (installRootValid)
                {
                    var fullInstall = Path.GetFullPath(_config.InstallationRoot);
                    var fullManaged = Path.GetFullPath(_config.ManagedDirectory);
                    if (fullManaged.StartsWith(fullInstall, StringComparison.OrdinalIgnoreCase))
                    {
                        managedDirValid = true;
                        managedDirMsg = $"Managed directory exists at '{_config.ManagedDirectory}' under installation root.";
                    }
                    else
                    {
                        managedDirMsg = $"Managed directory '{_config.ManagedDirectory}' is not located inside installation root '{_config.InstallationRoot}'.";
                    }
                }
                else
                {
                    managedDirValid = true;
                    managedDirMsg = $"Managed directory exists at '{_config.ManagedDirectory}'.";
                }
            }
            else
            {
                managedDirMsg = $"Managed directory does not exist: '{_config.ManagedDirectory}'.";
            }
        }
        items.Add(new("managed_directory", managedDirValid, managedDirMsg));

        // 4. Exact 4-part version check if configured
        bool versionValid = true;
        string versionMessage = "No specific GXW3 version pin specified; skipped.";
        if (!string.IsNullOrWhiteSpace(_config.Gxw3Version))
        {
            string? detectedVer = profile.Version ?? profile.RegistryVersion;
            if (!string.IsNullOrWhiteSpace(detectedVer))
            {
                // Strict exact 4-part match (no StartsWith prefix matching)
                versionValid = string.Equals(detectedVer.Trim(), _config.Gxw3Version.Trim(), StringComparison.OrdinalIgnoreCase);
                versionMessage = versionValid
                    ? $"GX Works3 version '{detectedVer}' strictly matches configured exact version pin '{_config.Gxw3Version}'."
                    : $"GX Works3 version mismatch: detected '{detectedVer}', configured exact pin '{_config.Gxw3Version}'.";
            }
            else
            {
                versionValid = false;
                versionMessage = $"GX Works3 version pin '{_config.Gxw3Version}' was specified, but toolchain version could not be detected.";
            }
        }
        items.Add(new("version_pin", versionValid, versionMessage));

        // 5. Gxw3ExeSha256 hash check (optional installation binary pin)
        bool hashValid = true;
        string hashMessage = "Executable hash pin: not configured (optional).";
        if (!string.IsNullOrWhiteSpace(_config.Gxw3ExeSha256))
        {
            if (exeConfigured)
            {
                try
                {
                    string actualExeHash = ComputeSha256(_config.Gxw3ExePath);
                    hashValid = string.Equals(actualExeHash, _config.Gxw3ExeSha256, StringComparison.OrdinalIgnoreCase);
                    hashMessage = hashValid
                        ? $"Executable SHA-256 hash matched: {actualExeHash}."
                        : $"Executable SHA-256 hash mismatch. Expected '{_config.Gxw3ExeSha256}', actual '{actualExeHash}'.";
                }
                catch (Exception ex)
                {
                    hashValid = false;
                    hashMessage = $"Failed to compute binary hash: {ex.Message}";
                }
            }
            else
            {
                hashValid = false;
                hashMessage = "Cannot verify Gxw3ExeSha256 because GXW3 executable does not exist.";
            }
        }
        items.Add(new("executable_hash", hashValid, hashMessage));

        // 6. Project root bounded and valid (optional)
        bool projectRootValid = true;
        string projectRootMessage = "Project root is not configured (optional for static doctor).";
        if (!string.IsNullOrWhiteSpace(_config.ProjectRoot))
        {
            if (Path.IsPathRooted(_config.ProjectRoot))
            {
                if (Directory.Exists(_config.ProjectRoot))
                {
                    if (ExternalWorkerSecurityPolicy.IsBroadOrDangerousRoot(_config.ProjectRoot, out string broadReason))
                    {
                        projectRootValid = false;
                        projectRootMessage = $"Project root refused: {broadReason}";
                    }
                    else
                    {
                        projectRootMessage = $"Project root '{_config.ProjectRoot}' is bounded and valid.";
                    }
                }
                else
                {
                    projectRootValid = false;
                    projectRootMessage = $"Project root directory does not exist: '{_config.ProjectRoot}'.";
                }
            }
            else
            {
                projectRootValid = false;
                projectRootMessage = $"Project root must be an absolute path: '{_config.ProjectRoot}'.";
            }
        }
        items.Add(new("project_root", projectRootValid, projectRootMessage));

        bool healthy = exeConfigured && installRootValid && managedDirValid && versionValid && hashValid && projectRootValid;
        string details = healthy
            ? $"Mitsubishi GX Works3 static installation checks passed. Static diagnosis healthy != engineering API is usable (ServiceBus unverified)."
            : string.Join("; ", items.Where(x => !x.Passed).Select(x => x.Name + ": " + x.Message));

        return (healthy, details, items);
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
