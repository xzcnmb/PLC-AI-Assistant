using System.Diagnostics;
using System.Text.Json;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime.Governance;

public interface IJobStateMachine
{
    ValueTask<EngineeringJob> CreateJobAsync(
        string targetId,
        EngineeringActionKind action,
        string projectHash,
        string? approvalId = null,
        CancellationToken cancellationToken = default);

    ValueTask<EngineeringJob> TransitionAsync(
        string jobId,
        JobState targetState,
        string? error = null,
        string? log = null,
        CancellationToken cancellationToken = default);

    ValueTask<EngineeringJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<EngineeringJob>> RecoverFromCrashAsync(CancellationToken cancellationToken = default);
}

public sealed class FileJobStateMachine : IJobStateMachine
{
    private readonly string _storageDir;
    private readonly object _syncLock = new();
    private readonly List<string> _discoveredOrphans = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public IReadOnlyList<string> DiscoveredOrphans
    {
        get
        {
            lock (_syncLock)
            {
                return _discoveredOrphans.AsReadOnly();
            }
        }
    }

    public IReadOnlyList<string> OrphanFiles => DiscoveredOrphans;

    public FileJobStateMachine(string storageDir)
    {
        _storageDir = Path.GetFullPath(storageDir);
        if (!Directory.Exists(_storageDir))
        {
            Directory.CreateDirectory(_storageDir);
        }

        CollectOrphanFiles();
    }

    public static bool IsValidJobId(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length != 36 ||
            !jobId.StartsWith("job_", StringComparison.Ordinal))
        {
            return false;
        }

        return jobId.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    public static void ValidateJobId(string jobId)
    {
        if (!IsValidJobId(jobId))
        {
            throw new ArgumentException("JobId must be a locally generated job_<hex> identifier.", nameof(jobId));
        }
    }

    private string GetJobFilePath(string jobId)
    {
        ValidateJobId(jobId);
        return Path.Combine(_storageDir, $"{jobId}.json");
    }

    public static bool IsValidTransition(JobState from, JobState to)
    {
        return (from, to) switch
        {
            // Pending can become Running, Failed, Cancelled, or Quarantined
            (JobState.Pending, JobState.Running) => true,
            (JobState.Pending, JobState.Failed) => true,
            (JobState.Pending, JobState.Cancelled) => true,
            (JobState.Pending, JobState.Quarantined) => true,

            // Running can become Succeeded, Failed, Cancelled, or Quarantined (e.g. after crash recovery)
            (JobState.Running, JobState.Succeeded) => true,
            (JobState.Running, JobState.Failed) => true,
            (JobState.Running, JobState.Cancelled) => true,
            (JobState.Running, JobState.Quarantined) => true,

            // Quarantined jobs can be manually failed or cancelled during administrative investigation
            (JobState.Quarantined, JobState.Failed) => true,
            (JobState.Quarantined, JobState.Cancelled) => true,

            // Terminal states cannot transition to other states
            _ => false
        };
    }

    private sealed class StoreLockScope : IDisposable
    {
        private FileStream? _stream;

        public StoreLockScope(FileStream stream)
        {
            _stream = stream;
        }

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _stream, null);
            s?.Dispose();
        }
    }

    private IDisposable AcquireStoreLock(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var lockPath = Path.Combine(_storageDir, ".store.lock");
        var timeoutLimit = timeout ?? TimeSpan.FromSeconds(10);
        var sw = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);

                return new StoreLockScope(stream);
            }
            catch (IOException)
            {
                if (sw.Elapsed >= timeoutLimit)
                {
                    throw new TimeoutException($"Failed to acquire store lock on '{lockPath}' within {timeoutLimit.TotalSeconds} seconds.");
                }

                Thread.Sleep(10);
            }
        }
    }

    private void CollectOrphanFiles()
    {
        try
        {
            var orphans = Directory.GetFiles(_storageDir, "*.tmp_*")
                .Concat(Directory.GetFiles(_storageDir, "*.tmp"))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var orphan in orphans)
            {
                if (!_discoveredOrphans.Contains(orphan, StringComparer.OrdinalIgnoreCase))
                {
                    _discoveredOrphans.Add(orphan);
                }
            }
        }
        catch
        {
            // Transient file system state
        }
    }

    public IReadOnlyList<string> CleanOrphanFiles()
    {
        lock (_syncLock)
        {
            using var storeLock = AcquireStoreLock();
            var cleaned = new List<string>();
            foreach (var file in _discoveredOrphans.ToArray())
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        cleaned.Add(file);
                    }
                }
                catch
                {
                    // Ignore individual cleanup errors
                }
            }
            _discoveredOrphans.RemoveAll(cleaned.Contains);
            return cleaned;
        }
    }

    public ValueTask<EngineeringJob> CreateJobAsync(
        string targetId,
        EngineeringActionKind action,
        string projectHash,
        string? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var jobId = $"job_{Guid.NewGuid():N}";
        var job = new EngineeringJob(
            jobId,
            targetId,
            action,
            JobState.Pending,
            projectHash,
            DateTimeOffset.UtcNow,
            ApprovalId: approvalId);

        lock (_syncLock)
        {
            using var storeLock = AcquireStoreLock(cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            SaveJobToDiskAtomic(job, expectedJobId: job.JobId);
            return ValueTask.FromResult(job);
        }
    }

    public ValueTask<EngineeringJob> TransitionAsync(
        string jobId,
        JobState targetState,
        string? error = null,
        string? log = null,
        CancellationToken cancellationToken = default)
    {
        ValidateJobId(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncLock)
        {
            using var storeLock = AcquireStoreLock(cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var current = LoadJobFromDisk(jobId) ?? throw new KeyNotFoundException($"Job '{jobId}' not found.");

            if (!IsValidTransition(current.State, targetState))
            {
                throw new InvalidOperationException($"Invalid job state transition from {current.State} to {targetState}.");
            }

            var now = DateTimeOffset.UtcNow;
            var updated = current with
            {
                State = targetState,
                StartedAt = (targetState == JobState.Running && current.StartedAt == null) ? now : current.StartedAt,
                CompletedAt = (targetState is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Quarantined) ? now : current.CompletedAt,
                Error = error ?? current.Error,
                Log = log != null ? (string.IsNullOrEmpty(current.Log) ? log : $"{current.Log}\n{log}") : current.Log
            };

            SaveJobToDiskAtomic(updated, expectedJobId: jobId);
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<EngineeringJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ValidateJobId(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncLock)
        {
            using var storeLock = AcquireStoreLock(cancellationToken: cancellationToken);
            return ValueTask.FromResult(LoadJobFromDisk(jobId));
        }
    }

    public ValueTask<IReadOnlyList<EngineeringJob>> RecoverFromCrashAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncLock)
        {
            using var storeLock = AcquireStoreLock(cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            CollectOrphanFiles();

            var recovered = new List<EngineeringJob>();
            var files = Directory.GetFiles(_storageDir, "job_*.json");

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var json = File.ReadAllText(file);
                    var job = JsonSerializer.Deserialize<EngineeringJob>(json);
                    if (job == null)
                    {
                        HandleCorruptedJobFile(file, "File contains null or empty JSON content.", recovered);
                        continue;
                    }

                    // Crash recovery logic:
                    // Any job found in 'Running' state upon restart/startup was interrupted by an unexpected crash or power loss.
                    // It must be placed into 'Quarantined' so engineers can inspect hardware state and prevent corrupted re-execution.
                    if (job.State == JobState.Running)
                    {
                        var quarantined = job with
                        {
                            State = JobState.Quarantined,
                            CompletedAt = DateTimeOffset.UtcNow,
                            Error = "Job interrupted mid-execution due to process crash/shutdown; quarantined for manual inspection."
                        };
                        SaveJobToDiskAtomic(quarantined, expectedJobId: job.JobId);
                        recovered.Add(quarantined);
                    }
                }
                catch (Exception ex)
                {
                    // Handle corruption on a per-file basis so other running jobs are still recovered and quarantined
                    HandleCorruptedJobFile(file, ex.Message, recovered);
                }
            }

            return ValueTask.FromResult<IReadOnlyList<EngineeringJob>>(recovered);
        }
    }

    private void HandleCorruptedJobFile(string file, string reason, List<EngineeringJob> recovered)
    {
        var fileName = Path.GetFileNameWithoutExtension(file);
        if (IsValidJobId(fileName))
        {
            try
            {
                var corruptedJob = new EngineeringJob(
                    JobId: fileName,
                    TargetId: "unknown",
                    Action: EngineeringActionKind.Compile,
                    State: JobState.Quarantined,
                    ProjectHash: "corrupted",
                    CreatedAt: DateTimeOffset.UtcNow,
                    CompletedAt: DateTimeOffset.UtcNow,
                    Error: $"Corrupt job file quarantined during crash recovery: {reason}");

                SaveJobToDiskAtomic(corruptedJob, expectedJobId: fileName);
                recovered.Add(corruptedJob);
            }
            catch
            {
                // Ignore fallback save failure to ensure remaining files in recovery continue
            }
        }
    }

    private EngineeringJob? LoadJobFromDisk(string jobId)
    {
        ValidateJobId(jobId);
        var path = GetJobFilePath(jobId);
        if (!File.Exists(path)) return null;

        var json = File.ReadAllText(path);
        var job = JsonSerializer.Deserialize<EngineeringJob>(json);
        if (job != null && !string.Equals(job.JobId, jobId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Persistent record JobId mismatch: file '{path}' contains JobId '{job.JobId}', expected '{jobId}'.");
        }
        return job;
    }

    private void SaveJobToDiskAtomic(EngineeringJob job, string? expectedJobId = null)
    {
        if (expectedJobId != null && !string.Equals(job.JobId, expectedJobId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Job ID mismatch: job object has '{job.JobId}' but caller specified '{expectedJobId}'.");
        }

        ValidateJobId(job.JobId);
        var path = GetJobFilePath(job.JobId);

        if (File.Exists(path))
        {
            try
            {
                var existingJson = File.ReadAllText(path);
                var existingJob = JsonSerializer.Deserialize<EngineeringJob>(existingJson);
                if (existingJob != null && !string.Equals(existingJob.JobId, job.JobId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Persistent record JobId mismatch: file '{path}' contains JobId '{existingJob.JobId}', expected '{job.JobId}'.");
                }
            }
            catch (JsonException)
            {
                // Existing file is corrupt; overwrite with valid atomic payload
            }
        }

        var tempPath = $"{path}.tmp_{Guid.NewGuid():N}";
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(job, JsonOptions);

        using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(jsonBytes, 0, jsonBytes.Length);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
