using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PlcMcp.Contracts.Models;
using PlcMcp.Runtime.Clients;

namespace PlcMcp.Runtime.Monitoring;

public sealed class MonitoringService : IMonitoringService
{
    public const int DefaultMaxActiveSessionsPerTarget = 2;
    public const int DefaultMaxActiveSessionsGlobal = 32;
    public static readonly TimeSpan DefaultTerminalRetentionPeriod = TimeSpan.FromMinutes(5);

    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TagValue>>> _readDelegate;
    private readonly Func<string, IReadOnlyList<TagDefinition>>? _tagResolver;
    private readonly MonitoringOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly PerTargetThrottler _throttler;
    private readonly ConcurrentDictionary<string, MonitoringSession> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _serviceCts = new();
    private readonly object _sessionLock = new();
    private readonly int _maxActiveSessionsPerTarget;
    private readonly int _maxActiveSessionsGlobal;
    private readonly TimeSpan _terminalRetentionPeriod;
    private bool _disposed;

    public MonitoringService(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TagValue>>> readDelegate,
        Func<string, IReadOnlyList<TagDefinition>>? tagResolver = null,
        MonitoringOptions? options = null,
        TimeProvider? timeProvider = null,
        int maxActiveSessionsPerTarget = DefaultMaxActiveSessionsPerTarget,
        int maxActiveSessionsGlobal = DefaultMaxActiveSessionsGlobal,
        TimeSpan? terminalRetentionPeriod = null)
    {
        _readDelegate = readDelegate ?? throw new ArgumentNullException(nameof(readDelegate));
        _tagResolver = tagResolver;
        _options = options ?? new MonitoringOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _throttler = new PerTargetThrottler(_options.PerTargetMinInterval, _timeProvider);
        _maxActiveSessionsPerTarget = maxActiveSessionsPerTarget > 0 ? maxActiveSessionsPerTarget : DefaultMaxActiveSessionsPerTarget;
        _maxActiveSessionsGlobal = maxActiveSessionsGlobal > 0 ? maxActiveSessionsGlobal : DefaultMaxActiveSessionsGlobal;
        _terminalRetentionPeriod = terminalRetentionPeriod ?? DefaultTerminalRetentionPeriod;
    }

    public MonitoringService(
        PlcRuntimeService runtimeService,
        MonitoringOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(
            (targetId, tags, ct) => (runtimeService ?? throw new ArgumentNullException(nameof(runtimeService))).ReadAsync(targetId, tags, ct),
            targetId => runtimeService.ListTags(targetId),
            options,
            timeProvider)
    {
    }

    public MonitoringService(
        IPlcRuntimeClient client,
        IEnumerable<TargetProfile> targets,
        IReadOnlyDictionary<string, IReadOnlyList<TagDefinition>> manifests,
        MonitoringOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(
            CreateClientReadDelegate(client, targets, manifests),
            targetId => manifests.TryGetValue(targetId, out var tags) ? tags : Array.Empty<TagDefinition>(),
            options,
            timeProvider)
    {
    }

    public MonitoringOptions Options => _options;

    public async Task<MonitoringWindowResult> SampleWindowAsync(
        MonitoringRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(_options);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _serviceCts.Token);
        var token = linkedCts.Token;

        var canonicalTags = ResolveCanonicalTags(request.TargetId, request.Tags);
        var interval = request.Interval ?? _options.DefaultInterval;
        var duration = request.Duration ?? _options.DefaultDuration;
        var maxSamples = request.MaxSamples ?? _options.DefaultMaxSamples;

        var samples = new List<MonitoringSample>(Math.Min(maxSamples, _options.WindowBufferSize));
        var changedSummary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastValues = new Dictionary<string, TagValue>(StringComparer.OrdinalIgnoreCase);
        var lastChangedTimes = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        var startedAt = _timeProvider.GetUtcNow();
        var sampleIndex = 0;
        var status = MonitoringStatus.Active;
        string? errorMessage = null;

        try
        {
            while (!token.IsCancellationRequested &&
                   sampleIndex < maxSamples &&
                   (_timeProvider.GetUtcNow() - startedAt) < duration)
            {
                var sampleTime = _timeProvider.GetUtcNow();
                IReadOnlyList<TagValue> rawValues;

                using (await _throttler.AcquireTargetLeaseAsync(request.TargetId, token).ConfigureAwait(false))
                {
                    rawValues = await _readDelegate(request.TargetId, request.Tags, token).ConfigureAwait(false);
                }

                var sample = ProcessSample(sampleTime, rawValues, sampleIndex, lastValues, lastChangedTimes);
                samples.Add(sample);
                foreach (var tag in sample.ChangedTags)
                {
                    changedSummary.Add(tag);
                }

                sampleIndex++;

                if (sampleIndex >= maxSamples || (_timeProvider.GetUtcNow() - startedAt) >= duration)
                {
                    break;
                }

                await _timeProvider.DelayAsync(interval, token).ConfigureAwait(false);
            }

            status = MonitoringStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            status = MonitoringStatus.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            status = MonitoringStatus.Faulted;
            errorMessage = ex.Message;
            throw;
        }

        var completedAt = _timeProvider.GetUtcNow();
        return new MonitoringWindowResult(
            TargetId: request.TargetId,
            RequestedTags: request.Tags,
            CanonicalTags: canonicalTags,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            Duration: completedAt - startedAt,
            SampleCount: samples.Count,
            Samples: samples,
            ChangedTagsSummary: changedSummary.OrderBy(x => x).ToArray(),
            Status: status,
            SnapshotGuarantee: SnapshotGuarantee.None,
            ErrorMessage: errorMessage);
    }

    public async IAsyncEnumerable<MonitoringSample> StreamSamplesAsync(
        MonitoringRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(_options);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _serviceCts.Token);
        var token = linkedCts.Token;

        var interval = request.Interval ?? _options.DefaultInterval;
        var duration = request.Duration ?? _options.DefaultDuration;
        var maxSamples = request.MaxSamples ?? _options.DefaultMaxSamples;

        var lastValues = new Dictionary<string, TagValue>(StringComparer.OrdinalIgnoreCase);
        var lastChangedTimes = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        var startedAt = _timeProvider.GetUtcNow();
        var sampleIndex = 0;

        while (!token.IsCancellationRequested &&
               sampleIndex < maxSamples &&
               (_timeProvider.GetUtcNow() - startedAt) < duration)
        {
            var sampleTime = _timeProvider.GetUtcNow();
            IReadOnlyList<TagValue> rawValues;

            using (await _throttler.AcquireTargetLeaseAsync(request.TargetId, token).ConfigureAwait(false))
            {
                rawValues = await _readDelegate(request.TargetId, request.Tags, token).ConfigureAwait(false);
            }

            var sample = ProcessSample(sampleTime, rawValues, sampleIndex, lastValues, lastChangedTimes);
            yield return sample;

            sampleIndex++;

            if (sampleIndex >= maxSamples || (_timeProvider.GetUtcNow() - startedAt) >= duration)
            {
                yield break;
            }

            await _timeProvider.DelayAsync(interval, token).ConfigureAwait(false);
        }
    }

    public IMonitoringSession StartSession(MonitoringRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(_options);

        var sessionId = $"mon-{Guid.NewGuid():N}";
        var canonicalTags = ResolveCanonicalTags(request.TargetId, request.Tags);

        MonitoringSession session;

        lock (_sessionLock)
        {
            CleanupExpiredTerminalSessionsLocked();

            var activeGlobal = _sessions.Values.Count(s => s.Status == MonitoringStatus.Active);
            if (activeGlobal >= _maxActiveSessionsGlobal)
            {
                throw new InvalidOperationException(
                    $"Global active monitoring session limit reached ({_maxActiveSessionsGlobal}).");
            }

            var activeTarget = _sessions.Values.Count(s =>
                s.Status == MonitoringStatus.Active &&
                string.Equals(s.Info.TargetId, request.TargetId, StringComparison.OrdinalIgnoreCase));
            if (activeTarget >= _maxActiveSessionsPerTarget)
            {
                throw new InvalidOperationException(
                    $"Active monitoring session limit for target '{request.TargetId}' reached ({_maxActiveSessionsPerTarget}).");
            }

            session = new MonitoringSession(
                sessionId: sessionId,
                request: request,
                canonicalTags: canonicalTags,
                options: _options,
                readDelegate: _readDelegate,
                throttler: _throttler,
                timeProvider: _timeProvider,
                parentToken: _serviceCts.Token);

            _sessions[sessionId] = session;
        }

        return session;
    }

    public IMonitoringSession? GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_sessionLock)
        {
            CleanupExpiredTerminalSessionsLocked();
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }
    }

    public IReadOnlyList<MonitoringSessionInfo> ListSessions()
    {
        lock (_sessionLock)
        {
            CleanupExpiredTerminalSessionsLocked();
            return _sessions.Values.Select(s => s.Info).ToArray();
        }
    }

    public bool StopSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Stop();
            return true;
        }

        return false;
    }

    public int CleanupTerminalSessions()
    {
        lock (_sessionLock)
        {
            return CleanupExpiredTerminalSessionsLocked();
        }
    }

    private int CleanupExpiredTerminalSessionsLocked()
    {
        var now = _timeProvider.GetUtcNow();
        var removedCount = 0;

        foreach (var pair in _sessions)
        {
            var session = pair.Value;
            if (session.Status != MonitoringStatus.Active)
            {
                var completedAt = session.Info.CompletedAt ?? now;
                if ((now - completedAt) >= _terminalRetentionPeriod)
                {
                    if (_sessions.TryRemove(pair.Key, out var removedSession))
                    {
                        removedSession.Dispose();
                        removedCount++;
                    }
                }
            }
        }

        return removedCount;
    }

    public IReadOnlyList<string> ResolveCanonicalTags(string targetId, IReadOnlyList<string> requestedTags)
    {
        if (_tagResolver is null)
        {
            return requestedTags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var manifest = _tagResolver(targetId);
        if (manifest is null || manifest.Count == 0)
        {
            return requestedTags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in manifest)
        {
            foreach (var alias in tag.AllNames)
            {
                lookup.TryAdd(alias, tag.Name);
            }
        }

        var canonical = new List<string>();
        foreach (var req in requestedTags)
        {
            if (lookup.TryGetValue(req, out var canonicalName))
            {
                canonical.Add(canonicalName);
            }
            else
            {
                canonical.Add(req);
            }
        }

        return canonical.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static MonitoringSample ProcessSample(
        DateTimeOffset sampleTime,
        IReadOnlyList<TagValue> rawValues,
        int sampleIndex,
        Dictionary<string, TagValue> lastValues,
        Dictionary<string, DateTimeOffset> lastChangedTimes)
    {
        var monitoredValues = new List<MonitoredTagValue>(rawValues.Count);
        var changedTags = new List<string>();

        foreach (var tag in rawValues)
        {
            var isFirst = !lastValues.TryGetValue(tag.Name, out var previous);
            bool hasChanged;

            if (isFirst || previous is null)
            {
                hasChanged = false;
                lastChangedTimes[tag.Name] = sampleTime;
            }
            else
            {
                hasChanged = !MonitoringSession.ValuesEqual(previous.Value, tag.Value) || previous.Quality != tag.Quality;
                if (hasChanged)
                {
                    lastChangedTimes[tag.Name] = sampleTime;
                    changedTags.Add(tag.Name);
                }
            }

            var staleness = sampleTime - lastChangedTimes[tag.Name];
            if (staleness < TimeSpan.Zero)
            {
                staleness = TimeSpan.Zero;
            }

            lastValues[tag.Name] = tag;

            monitoredValues.Add(new MonitoredTagValue(
                Name: tag.Name,
                Value: tag.Value,
                DataType: tag.DataType,
                Unit: tag.Unit,
                Quality: tag.Quality,
                Timestamp: tag.Timestamp != default ? tag.Timestamp : sampleTime,
                Staleness: staleness,
                HasChanged: hasChanged,
                NativeAddress: tag.NativeAddress,
                Error: tag.Error));
        }

        return new MonitoringSample(
            SampleIndex: sampleIndex,
            Timestamp: sampleTime,
            Values: monitoredValues,
            ChangedTags: changedTags,
            SnapshotGuarantee: SnapshotGuarantee.None);
    }

    private static Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TagValue>>> CreateClientReadDelegate(
        IPlcRuntimeClient client,
        IEnumerable<TargetProfile> targets,
        IReadOnlyDictionary<string, IReadOnlyList<TagDefinition>> manifests)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(manifests);

        var targetMap = targets.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        return async (targetId, names, ct) =>
        {
            if (!targetMap.TryGetValue(targetId, out var target))
            {
                throw new KeyNotFoundException($"Unknown target '{targetId}'.");
            }

            if (!manifests.TryGetValue(targetId, out var manifest))
            {
                throw new KeyNotFoundException($"No tag manifest is registered for '{targetId}'.");
            }

            var lookup = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in manifest)
            {
                foreach (var name in tag.AllNames)
                {
                    lookup.TryAdd(name, tag);
                }
            }

            var tagsToRead = new List<TagDefinition>();
            foreach (var name in names)
            {
                if (lookup.TryGetValue(name, out var td))
                {
                    tagsToRead.Add(td);
                }
                else
                {
                    throw new KeyNotFoundException($"Unknown tag '{name}' for target '{targetId}'.");
                }
            }

            var distinctTags = tagsToRead.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return await client.ReadAsync(target, distinctTags, ct).ConfigureAwait(false);
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _serviceCts.Cancel();
        }
        catch
        {
            // Ignore
        }

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();

        _throttler.Dispose();

        try
        {
            _serviceCts.Dispose();
        }
        catch
        {
            // Ignore
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _serviceCts.Cancel();
        }
        catch
        {
            // Ignore
        }

        // Best-effort wait for all session completions before destroying CTS
        var activeSessions = _sessions.Values.ToArray();
        foreach (var session in activeSessions)
        {
            session.Stop();
        }

        if (activeSessions.Length > 0)
        {
            try
            {
                var completionTasks = activeSessions.Select(s => s.Completion);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Task.WhenAll(completionTasks).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best effort wait; ignore timeout or cancellation during shutdown
            }
        }

        foreach (var session in activeSessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        _sessions.Clear();

        _throttler.Dispose();

        try
        {
            _serviceCts.Dispose();
        }
        catch
        {
            // Ignore
        }
    }
}
