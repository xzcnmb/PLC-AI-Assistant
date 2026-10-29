using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;

namespace PlcMcp.Engineering.Workers.AutoShop;

/// <summary>
/// Combined environment + ITP handshake inspection report for the AutoShop engineering bridge.
/// </summary>
public sealed record AutoShopInspectionReport(
    bool EnvironmentInstalled,
    AutoShopEnvironmentReport Environment,
    ItpHandshakeResult? Handshake,
    string Summary,
    IReadOnlyDictionary<string, string> Capabilities,
    string Disclaimer);

/// <summary>
/// Report of the controlled read-only shared-variable access attempt.
/// Variable values are only ever marked available when a real data packet is decoded;
/// with the ITP data-push format unverified, value access stays Unsupported.
/// </summary>
public sealed record AutoShopVariableReadReport(
    bool Connected,
    bool HandshakeAnswered,
    string? ResponseCode,
    string VariableReadCapability,
    string Reason,
    int PushPacketsObserved,
    long PushBytesObserved,
    string Disclaimer);

/// <summary>
/// Facade over the AutoShop environment detector and the read-only ITP client.
/// Owns the bridge configuration (endpoint, register path, timeout).
/// </summary>
public sealed class AutoShopBridgeServices
{
    public const string BridgeName = "AutoShop-ITP-Bridge";
    public const string LockedVendorVersion = "4.12.0.2";

    private readonly AutoShopBridgeConfig _config;
    private readonly AutoShopEnvironmentDetector _detector;

    public AutoShopBridgeServices(AutoShopBridgeConfig? config = null, AutoShopEnvironmentDetector? detector = null)
    {
        _config = config ?? new AutoShopBridgeConfig();
        _detector = detector ?? new AutoShopEnvironmentDetector();
    }

    public AutoShopBridgeConfig Config => _config;

    public AutoShopEnvironmentDetector Detector => _detector;

    /// <summary>True when the bridge is allowed to run: AutoShop installed on this host.</summary>
    public bool IsAvailable => _detector.Detect().Installed;

    public AutoShopInspectionReport Inspect()
    {
        AutoShopEnvironmentReport environment = _detector.Detect();
        ItpHandshakeResult? handshake = TryProbeHandshake();

        string summary = handshake is null
            ? $"AutoShop installed: {environment.Installed}; ITP endpoint {_config.Host}:{_config.Port} not listening; no handshake attempted."
            : $"AutoShop installed: {environment.Installed}; ITP register handshake answered: {handshake.Answered} (Response={handshake.ResponseCode ?? "none"}).";

        return new AutoShopInspectionReport(
            EnvironmentInstalled: environment.Installed,
            Environment: environment,
            Handshake: handshake,
            Summary: summary,
            Capabilities: BuildCapabilities(handshake),
            Disclaimer: environment.Disclaimer);
    }

    public async Task<AutoShopInspectionReport> InspectAsync(CancellationToken cancellationToken = default)
    {
        AutoShopEnvironmentReport environment = await Task.Run(_detector.Detect, cancellationToken).ConfigureAwait(false);
        ItpHandshakeResult? handshake = null;
        if (environment.LoopbackListening)
        {
            try
            {
                handshake = await ProbeHandshakeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                handshake = new ItpHandshakeResult(
                    Connected: false,
                    Answered: false,
                    ResponseCode: null,
                    Message: $"ITP handshake attempt failed: {ex.Message}",
                    Endpoint: $"{_config.Host}:{_config.Port}");
            }
        }

        string summary = handshake is null
            ? $"AutoShop installed: {environment.Installed}; ITP endpoint {_config.Host}:{_config.Port} not listening; no handshake attempted."
            : $"AutoShop installed: {environment.Installed}; ITP register handshake answered: {handshake.Answered} (Response={handshake.ResponseCode ?? "none"}).";

        return new AutoShopInspectionReport(
            EnvironmentInstalled: environment.Installed,
            Environment: environment,
            Handshake: handshake,
            Summary: summary,
            Capabilities: BuildCapabilities(handshake),
            Disclaimer: environment.Disclaimer);
    }

    /// <summary>
    /// Performs the read-only ITP register handshake against the configured endpoint.
    /// Connection failures are surfaced as an unanswered handshake result, never as success.
    /// </summary>
    public async Task<ItpHandshakeResult> ProbeHandshakeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = ItpClient.Create(_config.Host, _config.Port, _config.Timeout);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return await client.RegisterAsync(_config.RegisterPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ItpHandshakeResult(
                Connected: false,
                Answered: false,
                ResponseCode: null,
                Message: $"ITP handshake attempt failed: {ex.Message}",
                Endpoint: $"{_config.Host}:{_config.Port}");
        }
    }

    private ItpHandshakeResult? TryProbeHandshake()
    {
        try
        {
            return ProbeHandshakeAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            return new ItpHandshakeResult(false, false, null, "ITP handshake attempt failed.", $"{_config.Host}:{_config.Port}");
        }
    }

    /// <summary>
    /// Controlled read-only shared-variable access: register handshake plus a bounded observation
    /// window that only counts pushed packets (no decoding, no writes of any kind).
    /// </summary>
    public async Task<AutoShopVariableReadReport> ReadSharedVariablesAsync(
        TimeSpan? observationWindow = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan window = observationWindow ?? TimeSpan.FromSeconds(2);
        try
        {
            await using var client = ItpClient.Create(_config.Host, _config.Port, _config.Timeout);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            ItpHandshakeResult handshake = await client.RegisterAsync(_config.RegisterPath, cancellationToken).ConfigureAwait(false);
            return await ObserveAfterHandshakeAsync(client, handshake, window, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutoShopVariableReadReport(
                Connected: false,
                HandshakeAnswered: false,
                ResponseCode: null,
                VariableReadCapability: "Unsupported",
                Reason: $"ITP access attempt failed: {ex.Message}",
                PushPacketsObserved: 0,
                PushBytesObserved: 0,
                Disclaimer: "AutoShop interfaces used by this bridge were obtained by reverse engineering; there is no vendor endorsement. Version-locked to AutoShop 4.12.0.2. This tool is strictly read-only: no variable write, force, download, compile, or run/stop path exists.");
        }
    }

    private static async Task<AutoShopVariableReadReport> ObserveAfterHandshakeAsync(
        ItpClient client,
        ItpHandshakeResult handshake,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        int packets = 0;
        long bytes = 0;
        if (handshake.Answered)
        {
            using var observeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            observeCts.CancelAfter(window);
            try
            {
                while (!observeCts.IsCancellationRequested)
                {
                    ItpPacket packet = await client.ReadPacketAsync(observeCts.Token).ConfigureAwait(false);
                    packets++;
                    bytes += packet.TotalLength;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Observation window elapsed; that is the expected termination.
            }
            catch (Exception)
            {
                // Stream closed or malformed push packet; keep the handshake evidence.
            }
        }

        const string capability = "Unsupported";
        string reason = handshake.HasDataSource
            ? "ITP variable data-push packets (types 3..7) were observed but their binary layout is not reverse-engineered or verified; this bridge never decodes or fabricates variable values."
            : "No shared-variable publisher is registered for this path (service answered Response=3, 'Query Fail'). Open the publishing AutoShop project first; even then the binary push layout is unverified, so value decoding stays Unsupported.";

        return new AutoShopVariableReadReport(
            Connected: true,
            HandshakeAnswered: handshake.Answered,
            ResponseCode: handshake.ResponseCode,
            VariableReadCapability: capability,
            Reason: reason,
            PushPacketsObserved: packets,
            PushBytesObserved: bytes,
            Disclaimer: "AutoShop interfaces used by this bridge were obtained by reverse engineering; there is no vendor endorsement. Version-locked to AutoShop 4.12.0.2. This tool is strictly read-only: no variable write, force, download, compile, or run/stop path exists.");
    }

    private static Dictionary<string, string> BuildCapabilities(ItpHandshakeResult? handshake)
    {
        var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        capabilities["environment_inspection"] = "Experimental: local filesystem/registry/TCP evidence only; no vendor endorsement.";
        capabilities["itp_register_handshake"] = handshake?.Answered == true
            ? "Experimental: register handshake verified against the local InoVarSharedService endpoint."
            : "Unsupported: ITP endpoint unreachable or handshake not answered.";
        capabilities["shared_variable_read"] = "Unsupported: ITP variable data-push packet layout is not verified; no value decoding is implemented.";
        capabilities["compile"] = "Unsupported: the bridge provides no compile path.";
        capabilities["download"] = "Unsupported: physical download is forbidden in this bridge.";
        capabilities["force"] = "Unsupported: forcing is forbidden in this bridge.";
        capabilities["run_stop"] = "Unsupported: RUN/STOP control is forbidden in this bridge.";
        return capabilities;
    }
}

/// <summary>
/// Read-only engineering worker for the local AutoShop bridge.
///
/// Security posture mirrors the external worker protocol:
/// - dangerous operations (Download/RunStop/Force/HmiPublish and lookalikes) are hard rejected;
/// - only environment inspection jobs are accepted, and they never touch AutoShop UI, project
///   files, the registry, or the PLC;
/// - self-reported success is never promoted above Experimental.
/// </summary>
public sealed class AutoShopBridgeWorker : IEngineeringWorker
{
    public PlcVendor Vendor => PlcVendor.Inovance;
    public string Name => AutoShopBridgeServices.BridgeName;

    private readonly AutoShopBridgeServices _services;

    public AutoShopBridgeWorker(AutoShopBridgeServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public bool IsAvailable => _services.IsAvailable;

    public async Task<EngineeringExecutionResult> ExecuteAsync(EngineeringJobRequest job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (Workers.External.ExternalWorkerSecurityPolicy.IsHardRejectedOperation(job.JobType.ToString(), job.Options, out string rejectionReason))
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
                Message: $"AutoShop bridge supports read-only environment inspection only; '{job.JobType}' has no implementation.",
                Details: "The AutoShop bridge exposes no compile, export, diff, download, force, or run/stop path. AutoShop interfaces were obtained by reverse engineering without vendor endorsement and are version-locked to 4.12.0.2.");
        }

        try
        {
            AutoShopInspectionReport report = await _services.InspectAsync(cancellationToken).ConfigureAwait(false);
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: report.EnvironmentInstalled || report.Handshake?.Answered == true,
                Status: CapabilityStatus.Experimental,
                Message: report.Summary,
                Details: $"{report.Disclaimer} Capabilities: {string.Join("; ", report.Capabilities.Select(c => $"{c.Key}={c.Value}"))}");
        }
        catch (Exception ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"AutoShop bridge inspection error: {ex.Message}",
                Details: "AutoShop interfaces used by this bridge were obtained by reverse engineering; there is no vendor endorsement. Version-locked to AutoShop 4.12.0.2.");
        }
    }
}
