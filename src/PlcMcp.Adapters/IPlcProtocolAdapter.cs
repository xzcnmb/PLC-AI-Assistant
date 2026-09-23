using PlcMcp.Contracts.Models;

namespace PlcMcp.Adapters;

public interface IPlcProtocolAdapter
{
    string AdapterId { get; }
    PlcVendor Vendor { get; }
    IReadOnlyList<TransportKind> Transports { get; }
    IReadOnlyList<CapabilityDescriptor> Capabilities { get; }
    Task<ProbeResult> ProbeAsync(TargetProfile target, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TagValue>> ReadAsync(TargetProfile target, IReadOnlyList<TagDefinition> tags,
        CancellationToken cancellationToken = default);
    bool CanWrite(TargetProfile target) => false;
    Task<TagValue> WriteAsync(TargetProfile target, TagDefinition tag, object value,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Adapter '{AdapterId}' only supports reads.");
}
