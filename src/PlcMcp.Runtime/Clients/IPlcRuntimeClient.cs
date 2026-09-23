using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime.Clients;

public interface IPlcRuntimeClient
{
    Task<ProbeResult> ProbeAsync(TargetProfile target, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TagValue>> ReadAsync(
        TargetProfile target,
        IReadOnlyList<TagDefinition> tags,
        CancellationToken cancellationToken = default);

    Task<TagValue> WriteAsync(
        TargetProfile target,
        TagDefinition tag,
        object value,
        CancellationToken cancellationToken = default);
}
