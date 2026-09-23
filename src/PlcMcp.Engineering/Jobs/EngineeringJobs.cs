using System.Text.Json.Serialization;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Models;

namespace PlcMcp.Engineering.Jobs;

public enum EngineeringJobType
{
    InspectProject,
    ExportPou,
    ValidatePou,
    CompileProject,
    DiffProjects
}

public sealed record EngineeringJobRequest(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("jobType")] EngineeringJobType JobType,
    [property: JsonPropertyName("vendor")] PlcVendor Vendor,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("options")] IReadOnlyDictionary<string, string>? Options = null,
    [property: JsonPropertyName("workingCopySnapshot")] ProjectSnapshot? WorkingCopySnapshot = null);

public sealed record EngineeringExecutionResult(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("status")] CapabilityStatus Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("parsedProject")] ParsedProject? ParsedProject = null,
    [property: JsonPropertyName("diff")] ProjectDiff? Diff = null,
    [property: JsonPropertyName("staticCheck")] StaticCheckResult? StaticCheck = null,
    [property: JsonPropertyName("exportedOutputs")] IReadOnlyDictionary<string, string>? ExportedOutputs = null,
    [property: JsonPropertyName("details")] string? Details = null);

public interface IEngineeringWorker
{
    PlcVendor Vendor { get; }
    string Name { get; }
    bool IsAvailable { get; }
    Task<EngineeringExecutionResult> ExecuteAsync(EngineeringJobRequest job, CancellationToken cancellationToken = default);
}

public sealed class UnsupportedEngineeringWorker : IEngineeringWorker
{
    public PlcVendor Vendor { get; }
    public string Name { get; }
    public bool IsAvailable => false;
    private readonly string _reason;

    public UnsupportedEngineeringWorker(PlcVendor vendor, string name, string? reason = null)
    {
        Vendor = vendor;
        Name = name;
        _reason = reason ?? $"No vendor engineering backend worker is installed or enabled for '{name}' ({vendor}).";
    }

    public Task<EngineeringExecutionResult> ExecuteAsync(EngineeringJobRequest job, CancellationToken cancellationToken = default)
    {
        var result = new EngineeringExecutionResult(
            JobId: job.JobId,
            Success: false,
            Status: CapabilityStatus.Unsupported,
            Message: _reason,
            Details: "Install or configure the corresponding vendor worker/API backend to execute this engineering job.");
        return Task.FromResult(result);
    }
}
