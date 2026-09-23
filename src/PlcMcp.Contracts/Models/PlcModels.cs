using System.Text.Json.Serialization;

namespace PlcMcp.Contracts.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlcVendor
{
    Siemens,
    Omron,
    Mitsubishi,
    Inovance,
    Generic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlcDataType
{
    Bool,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Real,
    Double,
    String
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransportKind
{
    S7Comm,
    Fins,
    Slmp,
    ModbusTcp,
    OpcUa,
    EtherNetIp,
    Simulation
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapabilityStatus
{
    Supported,
    Unsupported,
    Experimental
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityCode
{
    Good,
    Bad,
    Uncertain,
    Simulated
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SafetyClass
{
    ReadOnly,
    Parameter,
    Handshake,
    Actuator,
    Safety
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationKind
{
    WriteTag,
    PulseTag,
    CompileProject,
    DownloadProject,
    SetRunMode,
    ForceIo
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlcByteOrder
{
    BigEndian,
    LittleEndian,
    WordSwap,
    ByteSwap
}

public sealed record EndpointProfile(
    string Host,
    int Port,
    TransportKind Transport,
    int? Rack = null,
    int? Slot = null,
    int? Unit = null,
    int TimeoutMs = 3000);

public sealed record CapabilityDescriptor(
    string Name,
    CapabilityStatus Status,
    string Notes);

public sealed record CapabilitySet(IReadOnlyList<CapabilityDescriptor> Items)
{
    public CapabilityStatus GetStatus(string name) =>
        Items.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Status
        ?? CapabilityStatus.Unsupported;

    public bool Supports(string name) => GetStatus(name) == CapabilityStatus.Supported;
}

public sealed record TargetProfile(
    string Id,
    PlcVendor Vendor,
    string Family,
    string? Model,
    string? Firmware,
    EndpointProfile Endpoint,
    IReadOnlyList<TransportKind> RuntimeProtocols,
    string? EngineeringBackend,
    CapabilitySet Capabilities,
    string PolicyId,
    bool IsSimulation = false);

public sealed record TagDefinition(
    string Name,
    string NativeAddress,
    PlcDataType DataType,
    string? Unit = null,
    double? Minimum = null,
    double? Maximum = null,
    bool CanRead = true,
    bool CanWrite = false,
    SafetyClass SafetyClass = SafetyClass.ReadOnly,
    IReadOnlyList<string>? Aliases = null,
    string? Description = null,
    PlcByteOrder? ByteOrder = null)
{
    public IReadOnlyList<string> AllNames =>
        new[] { Name }.Concat(Aliases ?? Array.Empty<string>()).ToArray();
}

public sealed record TagValue(
    string Name,
    object? Value,
    PlcDataType DataType,
    string? Unit,
    QualityCode Quality,
    DateTimeOffset Timestamp,
    string? NativeAddress = null,
    string? Error = null);

public sealed record PlannedChange(
    string Tag,
    object? CurrentValue,
    object? NewValue,
    PlcDataType DataType,
    string? Unit,
    SafetyClass SafetyClass,
    double? Minimum,
    double? Maximum);

public sealed record OperationPlan(
    string PlanId,
    string TargetId,
    OperationKind Operation,
    IReadOnlyList<PlannedChange> Changes,
    string StateHash,
    string ApprovalToken,
    DateTimeOffset ExpiresAt,
    bool RequiresHumanApproval,
    string RiskSummary);

public sealed record ApplyWriteResult(
    string PlanId,
    bool Applied,
    IReadOnlyList<TagValue> Values,
    string? Error,
    DateTimeOffset Timestamp);

public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Action,
    string TargetId,
    string? PlanId,
    string? Tag,
    object? OldValue,
    object? NewValue,
    bool Succeeded,
    string? Error);

public sealed record ProbeResult(
    string TargetId,
    bool Reachable,
    bool IsSimulation,
    string State,
    string Message,
    DateTimeOffset Timestamp);

public sealed record ProtocolDescriptor(
    string Name,
    PlcVendor Vendor,
    int DefaultPort,
    string AddressModel,
    bool SupportsRead,
    bool SupportsWrite,
    string Notes);
