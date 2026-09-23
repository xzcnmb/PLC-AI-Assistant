using System.Text.Json;
using System.Text.Json.Serialization;
using PlcMcp.Adapters.Protocols;
using PlcMcp.Contracts.Models;
using PlcMcp.Runtime;
using PlcMcp.Runtime.Clients;
using PlcMcp.Runtime.Policy;

namespace PlcMcp.Adapters;

public sealed record ConfiguredTarget(string Id, PlcVendor Vendor, string Family, EndpointProfile Endpoint,
    IReadOnlyList<TagDefinition> Tags, string? Model = null, string? Firmware = null);
public sealed record PlcConfiguration(int SchemaVersion, IReadOnlyList<ConfiguredTarget> Targets,
    [property: JsonPropertyName("$schema")] string? Schema = null);

public static class ConfiguredPlcComposition
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static PlcRuntimeHost Load(string path)
    {
        var file = new FileInfo(path);
        if (file.Length > 1_048_576) throw new InvalidDataException("Configuration exceeds 1 MiB.");
        var configuration = JsonSerializer.Deserialize<PlcConfiguration>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Configuration cannot be null.");
        return Create(configuration);
    }

    public static PlcRuntimeHost Create(PlcConfiguration configuration)
    {
        if (configuration.SchemaVersion != 1 || configuration.Targets is null || configuration.Targets.Count is < 1 or > 32)
            throw new ArgumentException("Configuration schemaVersion must be 1 with 1 to 32 explicit targets.");
        IPlcProtocolAdapter[] adapters = [new S7ReadAdapter(), new FinsTcpAdapter(), new SlmpAdapter(), new ModbusTcpAdapter()];
        var profiles = new List<TargetProfile>();
        var manifests = new Dictionary<string, IReadOnlyList<TagDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configuration.Targets)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length > 100 || !Enum.IsDefined(entry.Vendor) || string.IsNullOrWhiteSpace(entry.Family) ||
                entry.Endpoint is null || entry.Tags is null || entry.Tags.Count is < 1 or > 1000)
                throw new ArgumentException("Each target requires an id, vendor, family, endpoint and 1 to 1000 tags.");
            var ep = entry.Endpoint;
            if (string.IsNullOrWhiteSpace(ep.Host) || ep.Host.Contains('/') || ep.Host.Contains('*') || ep.Port is < 1 or > 65535 || ep.TimeoutMs is < 100 or > 30000)
                throw new ArgumentException($"Invalid host, port or timeout for '{entry.Id}'.");
            if (ep.Transport == TransportKind.S7Comm && (ep.Rack is null or < 0 or > 7 || ep.Slot is null or < 0 or > 31 ||
                entry.Family.ToUpperInvariant() is not ("S7-200 SMART" or "S7-200" or "S7-300" or "S7-400" or "S7-1200" or "S7-1500")))
                throw new ArgumentException("S7 requires a supported family, rack (0..7) and slot (0..31).");
            if (ep.Transport == TransportKind.ModbusTcp && ep.Unit is not null && ep.Unit is < 1 or > 255)
                throw new ArgumentException("Modbus unit must be 1..255.");
            if (ep.Transport == TransportKind.Fins && ep.Unit is not null && ep.Unit is < 0 or > 255)
                throw new ArgumentException("FINS unit must be 0..255.");
            if (ep.Transport == TransportKind.Slmp && ep.Unit is not (null or 0))
                throw new ArgumentException("SLMP only supports local station 0.");
            var adapter = adapters.SingleOrDefault(a => a.Transports.Contains(ep.Transport))
                ?? throw new NotSupportedException($"Transport '{ep.Transport}' has no installed read adapter. OPC UA/CIP remain planned capabilities.");
            if (adapter.Vendor != PlcVendor.Generic && adapter.Vendor != entry.Vendor)
                throw new ArgumentException("Target vendor does not match selected protocol adapter.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in entry.Tags)
            {
                if (tag is null || string.IsNullOrWhiteSpace(tag.Name) || string.IsNullOrWhiteSpace(tag.NativeAddress) || !Enum.IsDefined(tag.DataType) ||
                    tag.ByteOrder is not null && !Enum.IsDefined(tag.ByteOrder.Value) || tag.Minimum is double min && !double.IsFinite(min) ||
                    tag.Maximum is double max && !double.IsFinite(max) || tag.Minimum > tag.Maximum)
                    throw new ArgumentException("Invalid tag definition.");
                foreach (var name in tag.AllNames)
                    if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) throw new ArgumentException("Tag names and aliases must be unique within a target.");
                if (tag.CanWrite) throw new ArgumentException("Physical configurations must set canWrite=false.");
                switch (ep.Transport)
                {
                    case TransportKind.S7Comm: S7ReadAdapter.ParseAddress(tag); break;
                    case TransportKind.ModbusTcp: ModbusTcpAdapter.BuildRead(1, 1, tag); break;
                    case TransportKind.Fins: FinsTcpAdapter.BuildRead(1, 2, 0, 1, tag); break;
                    case TransportKind.Slmp: SlmpAdapter.BuildRead(tag); break;
                }
            }
            if (!manifests.TryAdd(entry.Id, entry.Tags.ToArray())) throw new ArgumentException("Target ids must be unique.");
            profiles.Add(new(entry.Id, entry.Vendor, entry.Family, entry.Model, entry.Firmware, ep,
                [ep.Transport], null, new(adapter.Capabilities), "physical-readonly", false));
        }
        var audit = new InMemoryAuditSink();
        return new(new PlcRuntimeService(profiles, manifests, new ProtocolRuntimeClient(adapters),
            new SafetyPolicy("physical-readonly", allowSimulationWrites: false), new PlanStore(), audit), audit);
    }
}

public sealed class ProtocolRuntimeClient(IEnumerable<IPlcProtocolAdapter> adapters) : IPlcRuntimeClient
{
    private readonly IPlcProtocolAdapter[] _adapters = adapters.ToArray();
    private IPlcProtocolAdapter For(TargetProfile target) => _adapters.Single(a => a.Transports.Contains(target.Endpoint.Transport));
    public Task<ProbeResult> ProbeAsync(TargetProfile target, CancellationToken cancellationToken = default) => For(target).ProbeAsync(target, cancellationToken);
    public Task<IReadOnlyList<TagValue>> ReadAsync(TargetProfile target, IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken = default) => For(target).ReadAsync(target, tags, cancellationToken);
    public Task<TagValue> WriteAsync(TargetProfile target, TagDefinition tag, object value, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Physical runtime has no write implementation.");
}
