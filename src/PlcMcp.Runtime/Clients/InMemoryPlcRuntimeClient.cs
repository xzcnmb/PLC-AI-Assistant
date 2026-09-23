using System.Collections.Concurrent;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime.Clients;

public sealed class InMemoryPlcRuntimeClient : IPlcRuntimeClient
{
    private readonly ConcurrentDictionary<(string TargetId, string TagName), object?> _values = new();

    public InMemoryPlcRuntimeClient(IEnumerable<(string TargetId, TagDefinition Tag, object? InitialValue)> seed)
    {
        foreach (var item in seed)
        {
            _values[(item.TargetId, item.Tag.Name)] = item.InitialValue;
        }
    }

    public Task<ProbeResult> ProbeAsync(TargetProfile target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProbeResult(
            target.Id,
            Reachable: target.IsSimulation,
            IsSimulation: target.IsSimulation,
            State: target.IsSimulation ? "SIMULATED" : "UNKNOWN",
            Message: target.IsSimulation ? "In-memory simulator is ready." : "No runtime adapter is registered.",
            DateTimeOffset.UtcNow));
    }

    public Task<IReadOnlyList<TagValue>> ReadAsync(
        TargetProfile target,
        IReadOnlyList<TagDefinition> tags,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var result = tags.Select(tag =>
        {
            if (!tag.CanRead)
            {
                return new TagValue(tag.Name, null, tag.DataType, tag.Unit, QualityCode.Bad, now,
                    tag.NativeAddress, "Tag is not readable by policy.");
            }

            if (!_values.TryGetValue((target.Id, tag.Name), out var value))
            {
                return new TagValue(tag.Name, null, tag.DataType, tag.Unit, QualityCode.Uncertain, now,
                    tag.NativeAddress, "No simulator value has been seeded.");
            }

            return new TagValue(tag.Name, value, tag.DataType, tag.Unit,
                target.IsSimulation ? QualityCode.Simulated : QualityCode.Good, now, tag.NativeAddress);
        }).ToArray();

        return Task.FromResult<IReadOnlyList<TagValue>>(result);
    }

    public Task<TagValue> WriteAsync(
        TargetProfile target,
        TagDefinition tag,
        object value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!target.IsSimulation)
        {
            throw new InvalidOperationException("The in-memory client cannot write a physical target.");
        }

        _values[(target.Id, tag.Name)] = value;
        return Task.FromResult(new TagValue(tag.Name, value, tag.DataType, tag.Unit,
            QualityCode.Simulated, DateTimeOffset.UtcNow, tag.NativeAddress));
    }
}
