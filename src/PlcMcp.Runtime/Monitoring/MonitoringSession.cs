using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime.Monitoring;

public sealed class MonitoringSession : IMonitoringSession
{
    private readonly string _sessionId;
    private readonly MonitoringRequest _request;
    private readonly IReadOnlyList<string> _canonicalTags;
    private readonly MonitoringOptions _options;
    private readonly BoundedSampleBuffer _buffer;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TagValue>>> _readDelegate;
    private readonly PerTargetThrottler _throttler;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Dictionary<string, TagValue> _lastValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastChangedTimes = new(StringComparer.OrdinalIgnoreCase);

    private MonitoringStatus _status = MonitoringStatus.Active;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset? _completedAt;
    private string? _errorMessage;
    private int _sampleIndex;
    private bool _disposed;

    public MonitoringSession(
        string sessionId,
        MonitoringRequest request,
        IReadOnlyList<string> canonicalTags,
        MonitoringOptions options,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TagValue>>> readDelegate,
        PerTargetThrottler throttler,
        TimeProvider? timeProvider = null,
        CancellationToken parentToken = default)
    {
        _sessionId = sessionId;
        _request = request;
        _canonicalTags = canonicalTags;
        _options = options;
        _readDelegate = readDelegate;
        _throttler = throttler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _buffer = new BoundedSampleBuffer(options.WindowBufferSize);

        _cts = parentToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parentToken)
            : new CancellationTokenSource();

        _startedAt = _timeProvider.GetUtcNow();

        // Start bounded execution
        _ = Task.Run(RunLoopAsync);
    }

    public string SessionId => _sessionId;

    public MonitoringStatus Status => _status;

    public Task Completion => _completionTcs.Task;

    public MonitoringSessionInfo Info => new(
        SessionId: _sessionId,
        TargetId: _request.TargetId,
        RequestedTags: _request.Tags,
        CanonicalTags: _canonicalTags,
        Interval: _request.Interval ?? _options.DefaultInterval,
        Duration: _request.Duration ?? _options.DefaultDuration,
        MaxSamples: _request.MaxSamples ?? _options.DefaultMaxSamples,
        StartedAt: _startedAt,
        CompletedAt: _completedAt,
        Status: _status,
        SampleCount: _buffer.TotalAdded,
        SnapshotGuarantee: SnapshotGuarantee.None,
        ErrorMessage: _errorMessage);

    public IReadOnlyList<MonitoringSample> GetBufferedSamples() => _buffer.ToArray();

    public MonitoringSample? GetLatestSample() => _buffer.Latest;

    public void Stop()
    {
        if (_status == MonitoringStatus.Active)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
        }
    }

    private async Task RunLoopAsync()
    {
        var interval = _request.Interval ?? _options.DefaultInterval;
        var duration = _request.Duration ?? _options.DefaultDuration;
        var maxSamples = _request.MaxSamples ?? _options.DefaultMaxSamples;

        try
        {
            while (!_cts.IsCancellationRequested &&
                   _sampleIndex < maxSamples &&
                   (_timeProvider.GetUtcNow() - _startedAt) < duration)
            {
                var sampleTime = _timeProvider.GetUtcNow();
                IReadOnlyList<TagValue> rawValues;

                using (await _throttler.AcquireTargetLeaseAsync(_request.TargetId, _cts.Token).ConfigureAwait(false))
                {
                    rawValues = await _readDelegate(_request.TargetId, _request.Tags, _cts.Token).ConfigureAwait(false);
                }

                var sample = ProcessSample(sampleTime, rawValues, _sampleIndex);
                _buffer.Add(sample);
                _sampleIndex++;

                if (_sampleIndex >= maxSamples || (_timeProvider.GetUtcNow() - _startedAt) >= duration)
                {
                    break;
                }

                await _timeProvider.DelayAsync(interval, _cts.Token).ConfigureAwait(false);
            }

            if (_cts.IsCancellationRequested && _status == MonitoringStatus.Active)
            {
                _status = MonitoringStatus.Cancelled;
            }
            else if (_status == MonitoringStatus.Active)
            {
                _status = MonitoringStatus.Completed;
            }
        }
        catch (OperationCanceledException)
        {
            _status = MonitoringStatus.Cancelled;
        }
        catch (Exception ex)
        {
            _status = MonitoringStatus.Faulted;
            _errorMessage = ex.Message;
        }
        finally
        {
            _completedAt = _timeProvider.GetUtcNow();
            _completionTcs.TrySetResult();
        }
    }

    private MonitoringSample ProcessSample(DateTimeOffset sampleTime, IReadOnlyList<TagValue> rawValues, int sampleIndex)
    {
        var monitoredValues = new List<MonitoredTagValue>(rawValues.Count);
        var changedTags = new List<string>();

        foreach (var tag in rawValues)
        {
            var isFirst = !_lastValues.TryGetValue(tag.Name, out var previous);
            bool hasChanged;

            if (isFirst || previous is null)
            {
                hasChanged = false;
                _lastChangedTimes[tag.Name] = sampleTime;
            }
            else
            {
                hasChanged = !ValuesEqual(previous.Value, tag.Value) || previous.Quality != tag.Quality;
                if (hasChanged)
                {
                    _lastChangedTimes[tag.Name] = sampleTime;
                    changedTags.Add(tag.Name);
                }
            }

            var staleness = sampleTime - _lastChangedTimes[tag.Name];
            if (staleness < TimeSpan.Zero)
            {
                staleness = TimeSpan.Zero;
            }

            _lastValues[tag.Name] = tag;

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

    public static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (Equals(a, b)) return true;

        if (IsNumeric(a) && IsNumeric(b))
        {
            try
            {
                var da = Convert.ToDouble(a);
                var db = Convert.ToDouble(b);
                return Math.Abs(da - db) < 0.000001;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsNumeric(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        try
        {
            _cts.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }
    }
}
