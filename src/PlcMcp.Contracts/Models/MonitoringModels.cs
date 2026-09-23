using System.Text.Json.Serialization;

namespace PlcMcp.Contracts.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotGuarantee
{
    None
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MonitoringStatus
{
    Active,
    Completed,
    Cancelled,
    Faulted
}

public sealed record MonitoringOptions
{
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan DefaultInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan DefaultDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxSamplesLimit { get; init; } = 500;
    public int DefaultMaxSamples { get; init; } = 100;
    public TimeSpan PerTargetMinInterval { get; init; } = TimeSpan.FromMilliseconds(20);
    public int WindowBufferSize { get; init; } = 500;
}

public sealed record MonitoringRequest
{
    public required string TargetId { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public TimeSpan? Interval { get; init; }
    public TimeSpan? Duration { get; init; }
    public int? MaxSamples { get; init; }

    public void Validate(MonitoringOptions options)
    {
        if (string.IsNullOrWhiteSpace(TargetId))
        {
            throw new ArgumentException("TargetId must be specified.", nameof(TargetId));
        }

        if (Tags == null || Tags.Count == 0)
        {
            throw new ArgumentException("At least one tag must be specified.", nameof(Tags));
        }

        if (Tags.Count > 128)
        {
            throw new ArgumentException("Cannot monitor more than 128 tags in a single request.", nameof(Tags));
        }

        if (Tags.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Tag names cannot be null or whitespace.", nameof(Tags));
        }

        var interval = Interval ?? options.DefaultInterval;
        if (interval < options.MinInterval || interval > options.MaxInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Interval),
                $"Interval must be between {options.MinInterval.TotalMilliseconds}ms and {options.MaxInterval.TotalMilliseconds}ms.");
        }

        var duration = Duration ?? options.DefaultDuration;
        if (duration <= TimeSpan.Zero || duration > options.MaxDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Duration),
                $"Duration must be positive and at most {options.MaxDuration.TotalMinutes} minutes.");
        }

        var maxSamples = MaxSamples ?? options.DefaultMaxSamples;
        if (maxSamples <= 0 || maxSamples > options.MaxSamplesLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSamples),
                $"MaxSamples must be between 1 and {options.MaxSamplesLimit}.");
        }
    }
}

public sealed record MonitoredTagValue(
    string Name,
    object? Value,
    PlcDataType DataType,
    string? Unit,
    QualityCode Quality,
    DateTimeOffset Timestamp,
    TimeSpan Staleness,
    bool HasChanged,
    string? NativeAddress = null,
    string? Error = null);

public sealed record MonitoringSample(
    int SampleIndex,
    DateTimeOffset Timestamp,
    IReadOnlyList<MonitoredTagValue> Values,
    IReadOnlyList<string> ChangedTags,
    SnapshotGuarantee SnapshotGuarantee = SnapshotGuarantee.None)
{
    public SnapshotGuarantee SnapshotGuarantee { get; init; } = SnapshotGuarantee;
}

public sealed record MonitoringWindowResult(
    string TargetId,
    IReadOnlyList<string> RequestedTags,
    IReadOnlyList<string> CanonicalTags,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan Duration,
    int SampleCount,
    IReadOnlyList<MonitoringSample> Samples,
    IReadOnlyList<string> ChangedTagsSummary,
    MonitoringStatus Status,
    SnapshotGuarantee SnapshotGuarantee = SnapshotGuarantee.None,
    string? ErrorMessage = null);

public sealed record MonitoringSessionInfo(
    string SessionId,
    string TargetId,
    IReadOnlyList<string> RequestedTags,
    IReadOnlyList<string> CanonicalTags,
    TimeSpan Interval,
    TimeSpan Duration,
    int MaxSamples,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    MonitoringStatus Status,
    int SampleCount,
    SnapshotGuarantee SnapshotGuarantee = SnapshotGuarantee.None,
    string? ErrorMessage = null);
