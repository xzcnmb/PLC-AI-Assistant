using System.Security.Cryptography;
using System.Text;

namespace PlcMcp.Runtime.Governance;

public interface ITargetLeaseService
{
    ValueTask<ITargetLease?> TryAcquireAsync(string targetId, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

public interface ITargetLease : IAsyncDisposable, IDisposable
{
    string TargetId { get; }
    string LockFilePath { get; }
    DateTimeOffset AcquiredAt { get; }
    bool IsActive { get; }
}

public sealed class FileTargetLeaseService : ITargetLeaseService
{
    private readonly string _locksDirectory;

    public FileTargetLeaseService(string locksDirectory)
    {
        _locksDirectory = Path.GetFullPath(locksDirectory);
        if (!Directory.Exists(_locksDirectory))
        {
            Directory.CreateDirectory(_locksDirectory);
        }
    }

    public static string GetSafeLockFileName(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId) || targetId.Length > 200)
            throw new ArgumentException("TargetId must contain 1 to 200 characters.", nameof(targetId));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(targetId.Trim().ToUpperInvariant()));
        return $"{Convert.ToHexString(hash).ToLowerInvariant()}.lock";
    }

    public async ValueTask<ITargetLease?> TryAcquireAsync(
        string targetId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new ArgumentException("TargetId cannot be empty.", nameof(targetId));
        }

        var lockPath = Path.Combine(_locksDirectory, GetSafeLockFileName(targetId));
        var timeoutLimit = timeout ?? TimeSpan.Zero;
        var start = DateTimeOffset.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Exclusive file lock across processes and threads on this machine
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    4096,
                    FileOptions.DeleteOnClose);

                // Write diagnostic acquisition info
                using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 1024, leaveOpen: true))
                {
                    stream.SetLength(0);
                    writer.WriteLine($"Target: {targetId}");
                    writer.WriteLine($"PID: {Environment.ProcessId}");
                    writer.WriteLine($"AcquiredAt: {DateTimeOffset.UtcNow:O}");
                    writer.Flush();
                    stream.Flush(true);
                }

                return new FileTargetLease(targetId, lockPath, stream);
            }
            catch (IOException)
            {
                // Lock held by another process/handle
                if (DateTimeOffset.UtcNow - start >= timeoutLimit)
                {
                    return null;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class FileTargetLease : ITargetLease
    {
        private FileStream? _stream;

        public string TargetId { get; }
        public string LockFilePath { get; }
        public DateTimeOffset AcquiredAt { get; }
        public bool IsActive => _stream != null;

        public FileTargetLease(string targetId, string lockFilePath, FileStream stream)
        {
            TargetId = targetId;
            LockFilePath = lockFilePath;
            AcquiredAt = DateTimeOffset.UtcNow;
            _stream = stream;
        }

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _stream, null);
            if (s != null)
            {
                try
                {
                    s.Dispose();
                }
                catch
                {
                    // Ignore release errors
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
