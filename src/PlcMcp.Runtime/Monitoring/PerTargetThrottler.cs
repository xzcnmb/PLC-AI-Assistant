using System.Collections.Concurrent;

namespace PlcMcp.Runtime.Monitoring;

public sealed class PerTargetThrottler : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAccessTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _minCooldown;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public PerTargetThrottler(TimeSpan minCooldown, TimeProvider? timeProvider = null)
    {
        _minCooldown = minCooldown < TimeSpan.Zero ? TimeSpan.Zero : minCooldown;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IDisposable> AcquireTargetLeaseAsync(string targetId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var gate = _gates.GetOrAdd(targetId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_lastAccessTimes.TryGetValue(targetId, out var lastTime))
            {
                var elapsed = _timeProvider.GetUtcNow() - lastTime;
                if (elapsed < _minCooldown)
                {
                    var delay = _minCooldown - elapsed;
                    await _timeProvider.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            return new TargetLease(this, targetId, gate);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private void Release(string targetId, SemaphoreSlim gate)
    {
        try
        {
            _lastAccessTimes[targetId] = _timeProvider.GetUtcNow();
        }
        finally
        {
            if (!_disposed)
            {
                gate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var gate in _gates.Values)
        {
            try
            {
                gate.Dispose();
            }
            catch
            {
                // Suppress disposal exceptions on shutdown
            }
        }

        _gates.Clear();
        _lastAccessTimes.Clear();
    }

    private sealed class TargetLease : IDisposable
    {
        private readonly PerTargetThrottler _throttler;
        private readonly string _targetId;
        private readonly SemaphoreSlim _gate;
        private int _released;

        public TargetLease(PerTargetThrottler throttler, string targetId, SemaphoreSlim gate)
        {
            _throttler = throttler;
            _targetId = targetId;
            _gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _throttler.Release(_targetId, _gate);
            }
        }
    }
}
