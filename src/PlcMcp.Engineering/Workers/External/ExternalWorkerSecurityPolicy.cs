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
    /// Maximum external process lifetime. A dedicated ScriptEngine process may
    /// live across several JSON-RPC calls but must always be bounded.
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
    /// Validates a raw command-line argument string used only for vendor parsers that
    /// require embedded quotes (for example, CODESYS --profile="name with spaces").
    /// Shell execution remains disabled; control characters and shell metacharacters are
    /// refused to prevent accidental command construction from untrusted values.
    /// </summary>
    public static void ValidateRawArguments(string rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
            throw new ArgumentException("Raw arguments cannot be empty.", nameof(rawArguments));
        if (rawArguments.Length > 8192)
            throw new ArgumentException("Raw arguments exceed the 8192-character limit.", nameof(rawArguments));

        bool insideQuote = false;
        foreach (char c in rawArguments)
        {
            if (char.IsControl(c) || c is '&' or '|' or '<' or '>' or '^' or '%')
                throw new ArgumentException($"Raw arguments contain a prohibited character (U+{(int)c:X4}).", nameof(rawArguments));
            if (c == '"') insideQuote = !insideQuote;
        }

        if (insideQuote)
            throw new ArgumentException("Raw arguments contain an unmatched quote.", nameof(rawArguments));
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
            if (IsBroadOrDangerousRoot(root, out string broadReason))
            {
                throw new UnauthorizedAccessException($"Allowlisted workspace root '{root}' is too broad: {broadReason}");
            }

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

    /// <summary>
    /// Checks whether a workspace root is overly broad (e.g. drive root, system folder, raw temp root, user root).
    /// </summary>
    public static bool IsBroadOrDangerousRoot(string path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Workspace root cannot be null or empty.";
            return true;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            reason = $"Invalid path format: {ex.Message}";
            return true;
        }

        string root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) || fullPath.Length <= 3)
        {
            reason = $"Root path '{fullPath}' is a filesystem or drive root, which is too broad.";
            return true;
        }

        var dangerousFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void TryAdd(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                try
                {
                    dangerousFolders.Add(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
                catch { }
            }
        }

        TryAdd(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        TryAdd(Environment.GetFolderPath(Environment.SpecialFolder.System));
        TryAdd(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        TryAdd(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        TryAdd(Path.GetTempPath());

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            TryAdd(Path.GetDirectoryName(userProfile)); // e.g. C:\Users
        }

        if (dangerousFolders.Contains(fullPath))
        {
            reason = $"Path '{fullPath}' is a system, root, or broad temporary directory. A dedicated subdirectory must be specified.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Validates that a workspace root is not overly broad.
    /// </summary>
    public static void ValidateWorkspaceRoot(string root)
    {
        if (IsBroadOrDangerousRoot(root, out string reason))
        {
            throw new UnauthorizedAccessException($"Workspace root refused: {reason}");
        }
    }

    /// <summary>
    /// Validates that an artifact relative path is well-formed, does not escape the constrained working directory,
    /// exists on disk, and matches the expected SHA-256 hash.
    /// </summary>
    public static string ValidateAndVerifyArtifact(
        string constrainedWorkingDirectory,
        WorkerArtifactDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(constrainedWorkingDirectory))
        {
            throw new ArgumentException("Constrained working directory cannot be empty.", nameof(constrainedWorkingDirectory));
        }

        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        if (string.IsNullOrWhiteSpace(descriptor.RelativePath))
        {
            throw new ArgumentException($"Artifact '{descriptor.Name}' has an empty or null relative path.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.Sha256))
        {
            throw new ArgumentException($"Artifact '{descriptor.Name}' has an empty or null SHA-256 hash.");
        }

        string rawRel = descriptor.RelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(rawRel))
        {
            throw new UnauthorizedAccessException($"Artifact path '{descriptor.RelativePath}' must be relative to the constrained workspace.");
        }

        foreach (char c in rawRel)
        {
            if (c < 0x20 && c != '\t')
            {
                throw new ArgumentException($"Artifact path contains illegal control character (code: {(int)c}).");
            }
        }

        string fullWorkDir = Path.GetFullPath(constrainedWorkingDirectory);
        if (!fullWorkDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            fullWorkDir += Path.DirectorySeparatorChar;
        }

        string combinedPath = Path.GetFullPath(Path.Combine(fullWorkDir, rawRel));

        if (!combinedPath.StartsWith(fullWorkDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Artifact path traversal detected: '{descriptor.RelativePath}' escapes constrained working directory '{constrainedWorkingDirectory}'.");
        }

        if (!File.Exists(combinedPath))
        {
            throw new FileNotFoundException($"Artifact file does not exist on disk at '{combinedPath}'.", combinedPath);
        }

        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(combinedPath);
        byte[] hashBytes = sha256.ComputeHash(stream);
        string actualSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
        string expectedSha256 = descriptor.Sha256.Trim().ToLowerInvariant();

        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Artifact '{descriptor.Name}' integrity check failed. Expected SHA-256: '{expectedSha256}', Actual: '{actualSha256}'.");
        }

        return combinedPath;
    }
}
