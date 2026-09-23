using System.Collections.Concurrent;
using System.Security.Cryptography;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;

namespace PlcMcp.Engineering.Workers.External;

/// <summary>
/// Security policy options for external workers.
/// </summary>
public sealed class ExternalWorkerSecurityPolicy
{
    /// <summary>
    /// Explicit allowlisted executable paths allowed to be launched.
    /// If null or empty, no external process can be launched (fail closed).
    /// </summary>
    public HashSet<string> AllowedExecutablePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicit allowlisted workspace roots within which projects and artifacts must reside.
    /// </summary>
    public HashSet<string> AllowedWorkspaceRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum allowed output size (stdout + stderr in bytes) before killing process.
    /// Default: 10 MB.
    /// </summary>
    public long MaxOutputBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Step timeout for individual RPC calls (e.g. handshake, doctor, status).
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan RpcTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Heartbeat interval for active long-running jobs.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum allowed overall lifetime of an external worker process before forceful kill.
    /// Default: 300 seconds.
    /// </summary>
    public TimeSpan MaxProcessLifetime { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Default allowed offline operation set: Inspect, Export, Compile, Diff.
    /// </summary>
    public static readonly HashSet<string> DefaultAllowedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Inspect",
        "InspectProject",
        "Export",
        "ExportPou",
        "Compile",
        "CompileProject",
        "Validate",
        "ValidatePou",
        "Diff",
        "DiffProjects"
    };

    /// <summary>
    /// Dangerous operations strictly requiring external cryptographic authorization tokens.
    /// In this foundational version, all these are hard rejected.
    /// </summary>
    public static readonly HashSet<string> ProhibitedDangerousOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Download",
        "DownloadProject",
        "Run",
        "Stop",
        "RunStop",
        "SetRunMode",
        "Force",
        "ForceIo",
        "HmiPublish",
        "PublishHmi"
    };

    /// <summary>
    /// Hard reject safety verification.
    /// </summary>
    public static bool IsHardRejectedOperation(string operation, IReadOnlyDictionary<string, string>? options, out string rejectionReason)
    {
        if (ProhibitedDangerousOperations.Contains(operation))
        {
            rejectionReason = $"Operation '{operation}' is classified as high-risk physical/network action (Download/RunStop/Force/HmiPublish). In this foundational security layer, it is HARD REJECTED without exception.";
            return true;
        }

        if (options != null)
        {
            foreach (var kvp in options)
            {
                string key = kvp.Key.ToLowerInvariant();
                string val = kvp.Value?.ToLowerInvariant() ?? string.Empty;

                if (key.Contains("download") || val.Contains("download") ||
                    key.Contains("run") || key.Contains("stop") ||
                    key.Contains("force") || val.Contains("force") ||
                    key.Contains("hmipublish") || val.Contains("hmipublish") ||
                    key.Contains("hmi_publish") || val.Contains("hmi_publish") ||
                    key.Contains("plc_write") || val.Contains("plc_write") ||
                    key.Contains("flash") || val.Contains("flash"))
                {
                    rejectionReason = $"Security violation: option '{kvp.Key}' requests dangerous operation or hardware modification which is hard rejected.";
                    return true;
                }
            }
        }

        rejectionReason = string.Empty;
        return false;
    }

    /// <summary>
    /// Validates whether an executable path is explicitly allowlisted and physically exists.
    /// </summary>
    public void ValidateExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path cannot be null or empty.", nameof(executablePath));
        }

        string fullPath = Path.GetFullPath(executablePath);

        if (AllowedExecutablePaths.Count == 0)
        {
            throw new UnauthorizedAccessException("External process execution refused: No executable paths are configured in the allowlist (fail-closed).");
        }

        if (!AllowedExecutablePaths.Contains(fullPath))
        {
            throw new UnauthorizedAccessException($"External process execution refused: Path '{fullPath}' is not in the explicit allowlist.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Allowlisted executable does not exist at '{fullPath}'.", fullPath);
        }
    }

    /// <summary>
    /// Validates whether a file/directory path is strictly within the allowed workspace roots.
    /// </summary>
    public string ValidateAndSanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        foreach (char c in path)
        {
            if (c < 0x20 && c != '\t')
            {
                throw new ArgumentException($"Path contains illegal control character (code: {(int)c}).", nameof(path));
            }
        }

        string fullPath = Path.GetFullPath(path);

        if (AllowedWorkspaceRoots.Count == 0)
        {
            throw new UnauthorizedAccessException("Workspace access refused: No workspace roots are configured in the allowlist (fail-closed).");
        }

        bool allowed = false;
        foreach (var root in AllowedWorkspaceRoots)
        {
            string fullRoot = Path.GetFullPath(root);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                fullRoot += Path.DirectorySeparatorChar;
            }

            if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullPath, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
                break;
            }
        }

        if (!allowed)
        {
            throw new UnauthorizedAccessException($"Path traversal or unauthorized root: '{fullPath}' is not inside any allowed workspace root.");
        }

        return fullPath;
    }
}
