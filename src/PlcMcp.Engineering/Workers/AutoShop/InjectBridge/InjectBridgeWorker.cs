using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Workers.External;

namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

/// <summary>
/// Engineering worker for the AutoShop inject bridge (injected in-process
/// bridge + named pipe).
///
/// Security posture mirrors the external worker protocol:
/// - dangerous operations (Download/RunStop/Force/HmiPublish and lookalikes)
///   are hard rejected through <see cref="ExternalWorkerSecurityPolicy"/> —
///   this happens BEFORE any pipe traffic and cannot be configured away;
/// - only read-only jobs (InspectProject) are accepted as job types; the
///   write surface is reachable exclusively through
///   <see cref="InjectBridgeServices.WriteReservationAsync"/> with an open C#
///   gate plus the bridge's own gate plus the loopback hard check;
/// - every result is capped at <see cref="CapabilityStatus.Experimental"/>:
///   reverse-engineered interfaces, no vendor endorsement, no verified PLC;
/// - the default capability matrix reports the write surface Unsupported with
///   the gate-open conditions verbatim.
/// </summary>
public sealed class InjectBridgeWorker : IEngineeringWorker
{
    public PlcVendor Vendor => PlcVendor.Inovance;
    public string Name => InjectBridgeServices.BridgeName;

    private readonly InjectBridgeServices _services;

    public InjectBridgeWorker(InjectBridgeServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// True when the bridge answered a handshake (local, read-only pipe probe;
    /// failures report the bridge as unavailable, never as success).
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                InjectBridgeHandshakeReport report =
                    _services.HandshakeAsync().GetAwaiter().GetResult();
                return report.Answered && report.MatchesExpectation(out _);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The capability matrix of this worker as a dictionary (all statements
    /// verbatim from the bridge; never upgraded).
    /// </summary>
    public IReadOnlyDictionary<string, string> DescribeCapabilities()
    {
        var matrix = _services.CapabilitiesAsync().GetAwaiter().GetResult();
        if (!matrix.Succeeded)
        {
            return new Dictionary<string, string>
            {
                ["inject_bridge"] = "Unsupported: bridge not reachable"
            };
        }
        return new Dictionary<string, string>
        {
            ["comm_stack_read_only"] = matrix.CommStackReadOnly ?? "unknown",
            ["variable_read"] = matrix.VariableRead ?? "unknown",
            ["element_write"] = matrix.ElementWrite ?? "unknown",
            ["force"] = matrix.Force ?? "unknown",
            ["download"] = matrix.Download ?? "unknown",
            ["compile"] = matrix.Compile ?? "unknown",
            ["run_stop"] = matrix.RunStop ?? "unknown"
        };
    }

    public async Task<EngineeringExecutionResult> ExecuteAsync(
        EngineeringJobRequest job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (ExternalWorkerSecurityPolicy.IsHardRejectedOperation(
                job.JobType.ToString(), job.Options, out string rejectionReason))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Operation refused by safety policy.",
                Details: rejectionReason);
        }

        if (job.JobType != EngineeringJobType.InspectProject)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Inject bridge supports read-only inspection only; '{job.JobType}' has no job implementation.",
                Details: "The write surface (element write / force / download) and compile are gated " +
                         "reservations, not job types: they require an open C# gate, the bridge-side gate, " +
                         "a loopback target and (for compile) the E3 entry-point deliverable. " +
                         InjectBridgeGateDefaultText);
        }

        try
        {
            InjectBridgeHandshakeReport report =
                await _services.HandshakeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!report.Connected || !report.Answered)
            {
                return new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Unsupported,
                    Message: "AutoShop inject bridge is not reachable.",
                    Details: $"{report.Message} {report.Disclaimer}");
            }

            if (!report.MatchesExpectation(out string mismatch))
            {
                return new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Unsupported,
                    Message: "AutoShop inject bridge identity mismatch.",
                    Details: $"{mismatch} {report.Disclaimer}");
            }

            string details =
                $"worker={report.WorkerName} version={report.WorkerVersion} bitness={report.Bitness} " +
                $"hostPid={report.HostPid} runtime={report.RuntimeEnvironment}; " +
                $"capabilities=[{string.Join(", ", report.Capabilities)}]. {report.Disclaimer}";

            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: true,
                Status: CapabilityStatus.Experimental,
                Message: "AutoShop inject bridge answered the read-only handshake; no PLC connection is verified.",
                Details: details);
        }
        catch (Exception ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"AutoShop inject bridge inspection error: {ex.Message}",
                Details: InjectBridgeProtocol.Disclaimer);
        }
    }

    /// <summary>Shared gate-conditions text (kept in one place for the tests).</summary>
    public const string InjectBridgeGateDefaultText =
        "Default gates: element write / force / download are closed (Unsupported) until an operator opens " +
        "the corresponding InjectBridgeGateOptions flag AND the bridge-side gate AND the loopback check pass; " +
        "compile stays Unsupported until the E3 entry point is configured and verified.";
}
