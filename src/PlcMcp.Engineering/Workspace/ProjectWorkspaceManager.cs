using System.Security.Cryptography;
using PlcMcp.Engineering.Models;

namespace PlcMcp.Engineering.Workspace;

public interface IProjectWorkspaceManager
{
    ProjectSnapshot CreateWorkingCopy(string sourcePath, string? targetDirectory = null, bool readOnly = true);
    string ComputeSha256(string filePath);
    void VerifyIntegrity(string filePath, string expectedSha256);
}

public sealed class ProjectWorkspaceManager : IProjectWorkspaceManager
{
    private readonly string _defaultWorkspaceRoot;

    public ProjectWorkspaceManager(string? defaultWorkspaceRoot = null)
    {
        _defaultWorkspaceRoot = string.IsNullOrWhiteSpace(defaultWorkspaceRoot)
            ? Path.Combine(Path.GetTempPath(), "PlcMcp_Engineering_Workspaces")
            : defaultWorkspaceRoot;
    }

    public static string SanitizePath(string path, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty.", nameof(path));

        var fullAllowed = Path.GetFullPath(allowedRoot);
        var fullTarget = Path.GetFullPath(path);

        if (!Path.IsPathFullyQualified(path) ||
            (!string.Equals(fullTarget, fullAllowed, StringComparison.OrdinalIgnoreCase) &&
             !fullTarget.StartsWith(Path.TrimEndingDirectorySeparator(fullAllowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException($"Path traversal detected: '{path}' escapes allowed root '{allowedRoot}'.");
        }

        return fullTarget;
    }

    public string ComputeSha256(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Source file not found: {filePath}", filePath);

        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void VerifyIntegrity(string filePath, string expectedSha256)
    {
        var current = ComputeSha256(filePath);
        if (!string.Equals(current, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"File integrity check failed for '{filePath}'. Expected: {expectedSha256}, Actual: {current}");
        }
    }

    public ProjectSnapshot CreateWorkingCopy(string sourcePath, string? targetDirectory = null, bool readOnly = true)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source project file not found: {sourcePath}", sourcePath);

        var fullSource = Path.GetFullPath(sourcePath);
        var originalSha256 = ComputeSha256(fullSource);
        var originalLength = new FileInfo(fullSource).Length;

        var targetRoot = string.IsNullOrWhiteSpace(targetDirectory)
            ? Path.Combine(_defaultWorkspaceRoot, Guid.NewGuid().ToString("N"))
            : targetDirectory;

        var allowedWorkspaceRoot = Path.GetFullPath(_defaultWorkspaceRoot);
        var sanitizedRoot = SanitizePath(Path.GetFullPath(targetRoot), allowedWorkspaceRoot);
        if (string.Equals(sanitizedRoot, allowedWorkspaceRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A dedicated working directory is required.", nameof(targetDirectory));
        if (Directory.Exists(sanitizedRoot) && Directory.EnumerateFileSystemEntries(sanitizedRoot).Any())
            throw new IOException("Working directory must be empty; existing files will never be overwritten.");
        Directory.CreateDirectory(sanitizedRoot);

        var fileName = Path.GetFileName(fullSource);
        var destinationPath = Path.Combine(sanitizedRoot, fileName);
        SanitizePath(destinationPath, sanitizedRoot);

        File.Copy(fullSource, destinationPath, overwrite: false);

        if (readOnly)
        {
            File.SetAttributes(destinationPath, File.GetAttributes(destinationPath) | FileAttributes.ReadOnly);
        }

        // Verify copied file matches original SHA256
        var copySha256 = ComputeSha256(destinationPath);
        if (!string.Equals(originalSha256, copySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Working copy SHA-256 mismatch. Original: {originalSha256}, Copied: {copySha256}");
        }

        // Verify original file remained strictly unchanged
        var checkSourceSha = ComputeSha256(fullSource);
        if (!string.Equals(originalSha256, checkSourceSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source file was modified during copy operation!");
        }

        return new ProjectSnapshot(
            SourcePath: fullSource,
            WorkingPath: destinationPath,
            Sha256: copySha256,
            ByteLength: originalLength,
            CreatedAt: DateTimeOffset.UtcNow,
            ReadOnly: readOnly);
    }
}
