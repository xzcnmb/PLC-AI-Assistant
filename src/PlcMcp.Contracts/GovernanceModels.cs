using System.Text.Json.Serialization;

namespace PlcMcp.Contracts.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Quarantined
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AtomicityScope
{
    SingleTag,
    BatchAtomic,
    BestEffortNonAtomic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RollbackSupportKind
{
    Unsupported,
    AutomaticSnapshot,
    ManualCompensatingSteps
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EngineeringActionKind
{
    Compile,
    DownloadProgram,
    UploadProgram,
    SetRunMode,
    RestoreBackup,
    FlashFirmware
}

public sealed record CapabilityReport(
    string TargetId,
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CapabilityDescriptor> Capabilities,
    IReadOnlyList<TransportKind> SupportedTransports,
    AtomicityScope WriteAtomicity,
    RollbackSupportKind RollbackSupport,
    bool RequiresApprovalForPhysical,
    string? Notes = null);

public sealed record RollbackAction(
    string Tag,
    object? ExpectedValue,
    PlcDataType DataType,
    string? Reason = null);

public sealed record RollbackPlan(
    string PlanId,
    string TargetId,
    RollbackSupportKind Kind,
    IReadOnlyList<RollbackAction> Actions,
    string BaselineStateHash,
    DateTimeOffset CreatedAt);

public sealed record ApprovalBinding(
    string ApprovalId,
    string TargetId,
    string ActionKind,
    string ProjectHash,
    string ApprovedBy,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Signature,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record EngineeringJob(
    string JobId,
    string TargetId,
    EngineeringActionKind Action,
    JobState State,
    string ProjectHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? ApprovalId = null,
    string? Error = null,
    string? Log = null,
    int RetryCount = 0);

public sealed record EngineeringJobResult(
    string JobId,
    string TargetId,
    JobState FinalState,
    bool Success,
    string? Error,
    DateTimeOffset CompletedAt,
    IReadOnlyDictionary<string, object?>? OutputDetails = null);

public sealed record AuditRecord(
    long SequenceNumber,
    DateTimeOffset Timestamp,
    string EventType,
    string TargetId,
    string OperatorId,
    string Action,
    string DetailsJson,
    string PrevHash,
    string RecordHash);
