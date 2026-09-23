using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime.Policy;

public sealed class SafetyPolicy
{
    public SafetyPolicy(string id, IEnumerable<string>? writableTags = null, bool allowSimulationWrites = true)
    {
        Id = id;
        _writableTags = new HashSet<string>(writableTags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        AllowSimulationWrites = allowSimulationWrites;
    }

    private readonly HashSet<string> _writableTags;

    public string Id { get; }
    public bool AllowSimulationWrites { get; }

    public bool CanWrite(TargetProfile target, TagDefinition tag, object value, out string reason)
    {
        if (!tag.CanWrite)
        {
            reason = "Tag is not writable by its manifest.";
            return false;
        }

        if (tag.SafetyClass is SafetyClass.Actuator or SafetyClass.Safety)
        {
            reason = "Direct actuator and safety writes are disabled in the MVP policy.";
            return false;
        }

        if (!target.IsSimulation)
        {
            reason = "Physical writes are disabled in the starter build; only simulation targets may be written.";
            return false;
        }

        if (!AllowSimulationWrites)
        {
            reason = "Simulation writes are disabled by the active policy.";
            return false;
        }

        if (_writableTags.Count > 0 && !_writableTags.Contains(tag.Name))
        {
            reason = "Tag is not present in the policy write allowlist.";
            return false;
        }

        if (!ValueConverter.TryConvertAndValidate(value, tag, out _, out var validationError))
        {
            reason = validationError;
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

public sealed class PlanStore
{
    private readonly Dictionary<string, OperationPlan> _plans = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public void Add(OperationPlan plan)
    {
        lock (_sync)
        {
            foreach (var expired in _plans.Where(x => x.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray())
                _plans.Remove(expired);
            if (_plans.Count >= 1024) throw new InvalidOperationException("Pending plan limit reached.");
            _plans[plan.PlanId] = plan with { Changes = Array.AsReadOnly(plan.Changes.ToArray()) };
        }
    }

    public bool TryTake(string planId, out OperationPlan? plan)
    {
        lock (_sync)
        {
            if (!_plans.TryGetValue(planId, out plan))
            {
                return false;
            }

            _plans.Remove(planId);
            return true;
        }
    }
}

public static class StateHasher
{
    public static string Compute(IEnumerable<TagValue> values)
    {
        var canonical = values
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new { x.Name, x.Value, x.DataType, x.Quality })
            .ToArray();
        var json = JsonSerializer.Serialize(canonical);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public interface IAuditSink
{
    ValueTask AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed class InMemoryAuditSink : IAuditSink
{
    private readonly List<AuditEntry> _entries = [];
    private readonly object _sync = new();

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToArray();
            }
        }
    }

    public ValueTask AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _entries.Add(entry);
        }

        return ValueTask.CompletedTask;
    }
}
