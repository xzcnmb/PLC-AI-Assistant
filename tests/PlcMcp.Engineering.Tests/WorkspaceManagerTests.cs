using PlcMcp.Engineering.Workspace;

namespace PlcMcp.Engineering.Tests;

public class WorkspaceManagerTests : IDisposable
{
    private readonly string _tempTestDir;
    private readonly ProjectWorkspaceManager _manager;

    public WorkspaceManagerTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "PlcMcp_TestWorkspace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
        _manager = new ProjectWorkspaceManager(_tempTestDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempTestDir))
            {
                // Remove readonly attributes before deleting
                foreach (var file in Directory.GetFiles(_tempTestDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_tempTestDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void CreateWorkingCopy_OriginalFileRemainsUntouched_Sha256Matches()
    {
        // Arrange
        var originalFile = Path.Combine(_tempTestDir, "source_original.smart");
        byte[] originalBytes = [0x50, 0x4C, 0x43, 0x01, 0x02, 0x03, 0x04];
        File.WriteAllBytes(originalFile, originalBytes);

        var originalSha = _manager.ComputeSha256(originalFile);
        var originalTimestamp = File.GetLastWriteTimeUtc(originalFile);

        // Act
        var snapshot = _manager.CreateWorkingCopy(originalFile, readOnly: true);

        // Assert
        Assert.NotNull(snapshot);
        Assert.True(File.Exists(snapshot.WorkingPath));
        Assert.Equal(originalSha, snapshot.Sha256);
        Assert.Equal(originalBytes.Length, snapshot.ByteLength);
        Assert.NotEqual(originalFile, snapshot.WorkingPath);

        // Check working copy is read-only
        var attributes = File.GetAttributes(snapshot.WorkingPath);
        Assert.True((attributes & FileAttributes.ReadOnly) != 0);

        // Verify original file is strictly untouched
        var postSha = _manager.ComputeSha256(originalFile);
        Assert.Equal(originalSha, postSha);
        Assert.Equal(originalBytes, File.ReadAllBytes(originalFile));
    }

    [Fact]
    public void SanitizePath_PathTraversalAttempt_ThrowsUnauthorizedAccessException()
    {
        var rootDir = Path.Combine(_tempTestDir, "safe_dir");
        Directory.CreateDirectory(rootDir);

        var maliciousPath = Path.Combine(rootDir, "..", "traversal_secret.txt");

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            ProjectWorkspaceManager.SanitizePath(maliciousPath, rootDir));

        Assert.Contains("Path traversal detected", ex.Message);
    }

    [Fact]
    public void VerifyIntegrity_WhenFileModified_ThrowsInvalidOperationException()
    {
        var testFile = Path.Combine(_tempTestDir, "test_file.txt");
        File.WriteAllText(testFile, "initial content");
        var sha = _manager.ComputeSha256(testFile);

        // Tamper with file
        File.WriteAllText(testFile, "modified content");

        Assert.Throws<InvalidOperationException>(() =>
            _manager.VerifyIntegrity(testFile, sha));
    }

    [Fact]
    public void CreateWorkingCopy_MissingSourceFile_ThrowsFileNotFoundException()
    {
        var nonExistent = Path.Combine(_tempTestDir, "does_not_exist.smart");

        Assert.Throws<FileNotFoundException>(() =>
            _manager.CreateWorkingCopy(nonExistent));
    }
}
