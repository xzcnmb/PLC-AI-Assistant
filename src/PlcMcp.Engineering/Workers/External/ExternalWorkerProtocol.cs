using System.Text.Json;
using System.Text.Json.Serialization;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Engineering.Workers.External;

/// <summary>
/// JSON-RPC 2.0 Request representation for External Engineering Workers.
/// </summary>
public sealed record ExternalRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params = null)
{
    public static ExternalRpcRequest Create(string id, string method, object? parameters = null)
    {
        JsonElement? elem = null;
        if (parameters != null)
        {
            var json = JsonSerializer.Serialize(parameters);
            elem = JsonDocument.Parse(json).RootElement.Clone();
        }
        return new ExternalRpcRequest("2.0", id, method, elem);
    }
}

/// <summary>
/// JSON-RPC 2.0 Error details.
/// </summary>
public sealed record ExternalRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data = null);

/// <summary>
/// JSON-RPC 2.0 Response representation.
/// </summary>
public sealed record ExternalRpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("result")] JsonElement? Result = null,
    [property: JsonPropertyName("error")] ExternalRpcError? Error = null);

/// <summary>
/// Handshake request sent by host client to external worker.
/// </summary>
public sealed record WorkerHandshakeRequest(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("hostVersion")] string HostVersion,
    [property: JsonPropertyName("supportedVendors")] IReadOnlyList<string> SupportedVendors);

/// <summary>
/// Handshake response returned by external worker.
/// </summary>
public sealed record WorkerHandshakeResponse(
    [property: JsonPropertyName("workerName")] string WorkerName,
    [property: JsonPropertyName("workerVersion")] string WorkerVersion,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("bitness")] string Bitness,
    [property: JsonPropertyName("runtimeEnvironment")] string RuntimeEnvironment,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("capabilityEvidence")] IReadOnlyDictionary<string, string>? CapabilityEvidence = null)
{
    public bool MatchesExpectation(string? expectedName, string? expectedVersion, string? expectedProtocol, out string mismatchReason)
    {
        if (!string.IsNullOrWhiteSpace(expectedName) && !string.Equals(WorkerName, expectedName, StringComparison.Ordinal))
        {
            mismatchReason = $"WorkerName mismatch: expected '{expectedName}', actual '{WorkerName}'.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(expectedVersion) && !string.Equals(WorkerVersion, expectedVersion, StringComparison.Ordinal))
        {
            mismatchReason = $"WorkerVersion mismatch: expected '{expectedVersion}', actual '{WorkerVersion}'.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(expectedProtocol) && !string.Equals(ProtocolVersion, expectedProtocol, StringComparison.Ordinal))
        {
            mismatchReason = $"ProtocolVersion mismatch: expected '{expectedProtocol}', actual '{ProtocolVersion}'.";
            return false;
        }
        mismatchReason = string.Empty;
        return true;
    }
}

/// <summary>
/// Worker doctor diagnosis response.
/// </summary>
public sealed record WorkerDoctorReport(
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("installed")] bool Installed,
    [property: JsonPropertyName("toolchainPath")] string? ToolchainPath,
    [property: JsonPropertyName("toolchainVersion")] string? ToolchainVersion,
    [property: JsonPropertyName("bitness")] string? Bitness,
    [property: JsonPropertyName("details")] string Details,
    [property: JsonPropertyName("checks")] IReadOnlyList<WorkerDoctorCheckItem>? Checks = null);

public sealed record WorkerDoctorCheckItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Task submit request payload.
/// </summary>
public sealed record WorkerSubmitJobRequest(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("options")] IReadOnlyDictionary<string, string>? Options = null);

/// <summary>
/// Task status query response payload.
/// </summary>
public sealed record WorkerJobStatusResponse(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("state")] string State, // "running", "completed", "failed", "cancelled"
    [property: JsonPropertyName("progress")] double Progress,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("exitCode")] int? ExitCode = null);

/// <summary>
/// Artifacts descriptor returned by worker.
/// </summary>
public sealed record WorkerArtifactDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("artifactType")] string ArtifactType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record WorkerArtifactsResponse(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<WorkerArtifactDescriptor> Artifacts);

/// <summary>
/// Trust classification for execution results from external workers.
/// External workers self-reporting completion can never be unconditionally promoted to Supported.
/// </summary>
public enum WorkerTrustLevel
{
    Untrusted,
    Experimental,
    Verified
}

/// <summary>
/// Identity pinning expectation for external workers.
/// </summary>
public sealed record WorkerIdentityExpectation(
    [property: JsonPropertyName("expectedWorkerName")] string? ExpectedWorkerName,
    [property: JsonPropertyName("expectedWorkerVersion")] string? ExpectedWorkerVersion,
    [property: JsonPropertyName("expectedProtocolVersion")] string? ExpectedProtocolVersion = "1.0");

