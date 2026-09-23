using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime.Monitoring;

public sealed class BoundedSampleBuffer
{
    private readonly object _lock = new();
    private readonly Queue<MonitoringSample> _samples;
    private readonly int _capacity;
    private int _totalAdded;

    public BoundedSampleBuffer(int capacity = 500)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Buffer capacity must be greater than zero.");
        }

        _capacity = capacity;
        _samples = new Queue<MonitoringSample>(Math.Min(capacity, 128));
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _samples.Count;
            }
        }
    }

    public int TotalAdded
    {
        get
        {
            lock (_lock)
            {
                return _totalAdded;
            }
        }
    }

    public MonitoringSample? Latest
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0)
                {
                    return null;
                }

                // In Queue, Peek is the oldest, but we want the newest.
                // We can maintain a _latest field or inspect the last element.
                return _latest;
            }
        }
    }

    private MonitoringSample? _latest;

    public void Add(MonitoringSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        lock (_lock)
        {
            if (_samples.Count >= _capacity)
            {
                _samples.Dequeue();
            }

            _samples.Enqueue(sample);
            _latest = sample;
            _totalAdded++;
        }
    }

    public IReadOnlyList<MonitoringSample> ToArray()
    {
        lock (_lock)
        {
            return _samples.ToArray();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _samples.Clear();
            _latest = null;
        }
    }
}
