using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Workers.External;

namespace PlcMcp.Engineering.Workers.AutoShop.NativeBridge;

/// <summary>
/// Read-only engineering worker for the AutoShop native bridge (in-process
/// plugin + communication-stack shim + named pipe).
///
/// Security posture mirrors the external worker protocol:
/// - dangerous operations (Download/RunStop/Force/HmiPublish and lookalikes)
///   are hard rejected through <see cref="ExternalWorkerSecurityPolicy"/>;
/// - only read-only jobs (InspectProject / read-only status) are accepted;
/// - every result is capped at <see cref="CapabilityStatus.Experimental"/>:
///   the native bridge self-reports, no PLC connection is verified, and the
///   interfaces are reverse-engineered without vendor endorsement;
/// - the worker never touches AutoShop UI, project files, the registry, the
///   service, or any PLC: it only talks to the local named pipe.
/// </summary>
public sealed class NativeBridgeWorker : IEngineeringWorker
{
    public PlcVendor Vendor => PlcVendor.Inovance;
    public string Name => NativeBridgeServices.BridgeName;

    private readonly NativeBridgeServices _services;

    public NativeBridgeWorker(NativeBridgeServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// True when the native bridge answered a handshake. Probing the pipe is a
    /// local, read-only operation; failures simply report the bridge as
    /// unavailable (never as a successful inspection).
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                NativeBridgeHandshakeReport report =
                    _services.HandshakeAsync().GetAwaiter().GetResult();
                return report.Answered && report.MatchesExpectation(out _);
            }
            catch
            {
                return false;
            }
        }
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
                Message: $"Native bridge supports read-only inspection only; '{job.JobType}' has no implementation.",
                Details: "The native bridge exposes no compile, export, diff, download, force, or run/stop path. " +
                         "AutoShop interfaces were obtained by reverse engineering without vendor endorsement and " +
                         "are version-locked to " + NativeBridgeServices.LockedVendorVersion + ".");
        }

        try
        {
            NativeBridgeHandshakeReport report =
                await _services.HandshakeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!report.Connected || !report.Answered)
            {
                return new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Unsupported,
                    Message: "AutoShop native bridge is not reachable.",
                    Details: $"{report.Message} {report.Disclaimer}");
            }

            if (!report.MatchesExpectation(out string mismatch))
            {
                return new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Unsupported,
                    Message: "AutoShop native bridge identity mismatch.",
                    Details: $"{mismatch} {report.Disclaimer}");
            }

            NativeBridgeStatusReport status = await _services.ReadStatusAsync(cancellationToken)
                .ConfigureAwait(false);

            string details =
                $"worker={report.WorkerName} version={report.WorkerVersion} bitness={report.Bitness} " +
                $"runtime={report.RuntimeEnvironment}; capabilities=[{string.Join(", ", report.Capabilities)}]; " +
                $"status={(status.Succeeded ? $"execModeCallResult={status.ExecModeCallResult}, gcmStateCallResult={status.GcmStateCallResult}" : status.Message)}. " +
                $"{report.Disclaimer}";

            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: true,
                Status: CapabilityStatus.Experimental,
                Message: "AutoShop native bridge answered the read-only handshake; no PLC connection is verified.",
                Details: details);
        }
        catch (Exception ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"AutoShop native bridge inspection error: {ex.Message}",
                Details: NativeBridgeProtocol.Disclaimer);
        }
    }
}
