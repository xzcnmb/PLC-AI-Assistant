namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

/// <summary>
/// Handshake report of the inject bridge. Self-reported worker data is never
/// promoted above Experimental.
/// </summary>
public sealed record InjectBridgeHandshakeReport(
    bool Connected,
    bool Answered,
    string? WorkerName,
    string? WorkerVersion,
    string? ProtocolVersion,
    string? Bitness,
    string? RuntimeEnvironment,
    int? HostPid,
    IReadOnlyList<string> Capabilities,
    string Message,
    string Disclaimer)
{
    public bool MatchesExpectation(out string mismatchReason)
    {
        mismatchReason = string.Empty;
        if (!Answered)
        {
            mismatchReason = "Inject bridge did not answer the handshake.";
            return false;
        }
        if (!string.Equals(WorkerName, InjectBridgeProtocol.WorkerName, StringComparison.Ordinal))
        {
            mismatchReason = $"WorkerName mismatch: expected '{InjectBridgeProtocol.WorkerName}', actual '{WorkerName}'.";
            return false;
        }
        if (!string.Equals(ProtocolVersion, InjectBridgeProtocol.ProtocolVersion, StringComparison.Ordinal))
        {
            mismatchReason = $"ProtocolVersion mismatch: expected '{InjectBridgeProtocol.ProtocolVersion}', actual '{ProtocolVersion}'.";
            return false;
        }
        return true;
    }
}

/// <summary>
/// Capability matrix of the bridge (self-reported, gate-aware). Strings are
/// the verbatim bridge statements; never paraphrased into a stronger claim.
/// </summary>
public sealed record InjectBridgeCapabilityMatrix(
    bool Succeeded,
    string? CommStackReadOnly,
    string? VariableRead,
    string? ElementWrite,
    string? Force,
    string? Download,
    string? Compile,
    string? RunStop,
    string Message,
    string Disclaimer);

/// <summary>
/// Result of a loopback set_target request through the bridge.
/// </summary>
public sealed record InjectBridgeSetTargetReport(
    bool Succeeded,
    bool? Stored,
    string? Ip,
    int? Port,
    bool? LoopbackOnly,
    string Message,
    string Disclaimer);

/// <summary>
/// Read-only PLC status evidence (call-return codes only; requires a loopback
/// target to have been set first).
/// </summary>
public sealed record InjectBridgeStatusReport(
    bool Succeeded,
    int? ExecMode,
    long? ExecModeCallResult,
    int? GcmState,
    long? GcmStateCallResult,
    string? Evidence,
    string Message,
    string Disclaimer);

/// <summary>
/// Read-only element read evidence (G1 value-path upgrade). The bridge fills
/// the vendor CSoftAddrMap with the real primitives, calls ReadElemValue per
/// element and polls BOTH value slots: the CString at tagSoftAddr+0x0C — the
/// slot the vendor write-back parser assigns when the backend answers (see
/// docs/inovance-autoshop-monitor-values.md §2) — and the +0x20 double
/// mirror. <see cref="Delivered"/> is the number of elements whose +0x0C
/// CString actually changed: Delivered &gt; 0 proves an end-to-end answered
/// read (real values); Delivered == 0 keeps the honest CALL-LEVEL verdict.
/// <see cref="ValueSlots"/>, <see cref="Variant"/> and
/// <see cref="WatchRc"/> carry the per-element delivery mirror and the
/// variant receipts. Null on pre-G1 bridges (legacy wording applies).
/// </summary>
public sealed record InjectBridgeElementReadReport(
    bool Succeeded,
    int? Count,
    string? Values,
    string? Note,
    string Message,
    string Disclaimer,
    int? Delivered = null,
    string? ValueSlots = null,
    string? Variant = null,
    long? WatchRc = null,
    long? OnlineRc = null);

/// <summary>
/// G1 read_elements variant options (all optional; null keeps the proven
/// baseline: mode 0 / level 0 / 6 s settle, private map, NULL window, no
/// extra paths). Settle is capped at 20 s on the bridge side.
/// </summary>
public sealed record InjectBridgeReadVariant(
    int? Mode = null,
    int? Level = null,
    int? SettleMs = null,
    bool UseMainFrameWnd = false,
    bool ReportActiveMap = false,
    bool TryWatchPath = false,
    bool TryOnlineProbe = false);

/// <summary>
/// Write-surface report. The delivered bridge RESOLVES the vendor export but
/// never invokes it: <see cref="Invoked"/> is false with an explicit reason,
/// and <see cref="HonestUnsupported"/> reports surfaces that answered
/// Unsupported (gate closed / not wired) so callers cannot mistake a
/// reservation for an executed action.
/// </summary>
public sealed record InjectBridgeWriteReport(
    bool Succeeded,
    string RequestMethod,
    bool GateOpen,
    bool? Resolved,
    bool? Invoked,
    string? Export,
    string? ExportRva,
    string? Reason,
    string Message,
    string Disclaimer,
    bool HonestUnsupported = false);

/// <summary>Compile report (E3 slot; Unsupported until the entry point lands).</summary>
public sealed record InjectBridgeCompileReport(
    bool Succeeded,
    bool EntryConfigured,
    string Message,
    string Disclaimer,
    bool HonestUnsupported = true);

/// <summary>
/// Facade over the inject bridge pipe client. Owns the configuration (pipe
/// name, server, timeout, gates) and maps responses onto the report models.
/// Every method is failure-safe: transport problems produce a report, not an
/// exception.
/// </summary>
public sealed class InjectBridgeServices
{
    public const string BridgeName = "AutoShop-Inject-Bridge";
    public const string LockedVendorVersion = InjectBridgeProtocol.LockedVendorVersion;

    private readonly InjectBridgeClientOptions _options;

    public InjectBridgeServices(InjectBridgeClientOptions? options = null)
    {
        options?.Gate.Validate();
        _options = options ?? new InjectBridgeClientOptions();
    }

    public InjectBridgeClientOptions Options => _options;

    /// <summary>Connects and performs the handshake.</summary>
    public async Task<InjectBridgeHandshakeReport> HandshakeAsync(
        string clientName = "PlcMcp",
        CancellationToken cancellationToken = default)
    {
        var client = new InjectBridgePipeClient(_options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            InjectBridgeCallResult call = await client.CallAsync(
                InjectBridgeProtocol.MethodHandshake,
                new InjectBridgeRequestParams(Client: clientName),
                cancellationToken).ConfigureAwait(false);
            if (!call.Succeeded || call.Response?.Result == null)
            {
                return NotAnswered(client.Endpoint, call.ErrorMessage ?? "no result");
            }

            InjectBridgeResult result = call.Response.Result;
            return new InjectBridgeHandshakeReport(
                Connected: true,
                Answered: true,
                WorkerName: result.WorkerName,
                WorkerVersion: result.WorkerVersion,
                ProtocolVersion: result.ProtocolVersion,
                Bitness: result.Bitness,
                RuntimeEnvironment: result.RuntimeEnvironment,
                HostPid: result.Pid,
                Capabilities: result.CapabilityList ?? Array.Empty<string>(),
                Message: "Handshake answered.",
                Disclaimer: InjectBridgeProtocol.Disclaimer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotAnswered(client.Endpoint, ex.Message);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Capability matrix (bridge-side gates reflected verbatim).</summary>
    public async Task<InjectBridgeCapabilityMatrix> CapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        var client = new InjectBridgePipeClient(_options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            InjectBridgeCallResult call = await client.CallAsync(
                InjectBridgeProtocol.MethodCapabilities, null, cancellationToken)
                .ConfigureAwait(false);
            if (!call.Succeeded || call.Response?.Result == null)
            {
                return new InjectBridgeCapabilityMatrix(
                    false, null, null, null, null, null, null, null,
                    $"Capabilities unavailable ({client.Endpoint}): {call.ErrorMessage}",
                    InjectBridgeProtocol.Disclaimer);
            }

            InjectBridgeResult result = call.Response.Result;
            return new InjectBridgeCapabilityMatrix(
                true,
                result.CommStackReadOnly,
                result.VariableRead,
                result.ElementWrite,
                result.Force,
                result.Download,
                result.Compile,
                result.RunStop,
                "Capability matrix answered (bridge statements verbatim).",
                InjectBridgeProtocol.Disclaimer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InjectBridgeCapabilityMatrix(
                false, null, null, null, null, null, null, null,
                $"Capabilities unavailable: {ex.Message}", InjectBridgeProtocol.Disclaimer);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stores a LOOPBACK-ONLY connection target in the bridge. Non-loopback
    /// addresses are refused locally (never sent).
    /// </summary>
    public async Task<InjectBridgeSetTargetReport> SetTargetAsync(
        string ip,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (!InjectBridgeGateOptions.IsLoopbackAddress(ip))
        {
            return new InjectBridgeSetTargetReport(
                false, null, ip, port, null,
                $"Non-loopback ip '{ip}' refused locally; the inject bridge only configures 127.0.0.0/8.",
                InjectBridgeProtocol.Disclaimer);
        }
        if (port is < 1 or > 65535)
        {
            return new InjectBridgeSetTargetReport(
                false, null, ip, port, null, "Port must be between 1 and 65535.",
                InjectBridgeProtocol.Disclaimer);
        }

        var client = new InjectBridgePipeClient(_options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            InjectBridgeCallResult call = await client.CallAsync(
                InjectBridgeProtocol.MethodSetTarget,
                new InjectBridgeRequestParams(Ip: ip, Port: port),
                cancellationToken).ConfigureAwait(false);
            if (!call.Succeeded || call.Response?.Result == null)
            {
                return new InjectBridgeSetTargetReport(
                    false, null, ip, port, null,
                    $"set_target failed: {call.ErrorMessage}", InjectBridgeProtocol.Disclaimer);
            }

            InjectBridgeResult result = call.Response.Result;
            return new InjectBridgeSetTargetReport(
                true, result.Stored, result.Ip ?? ip, result.Port ?? port, result.LoopbackOnly,
                "Loopback target stored in the bridge.", InjectBridgeProtocol.Disclaimer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InjectBridgeSetTargetReport(
                false, null, ip, port, null, $"set_target failed: {ex.Message}",
                InjectBridgeProtocol.Disclaimer);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Read-only status (requires a loopback target set first).</summary>
    public async Task<InjectBridgeStatusReport> ReadStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var client = new InjectBridgePipeClient(_options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            InjectBridgeCallResult call = await client.CallAsync(
                InjectBridgeProtocol.MethodReadStatus, null, cancellationToken)
                .ConfigureAwait(false);
            if (!call.Succeeded || call.Response?.Result == null)
            {
                return new InjectBridgeStatusReport(
                    false, null, null, null, null, null,
                    $"read_status failed: {call.ErrorMessage}", InjectBridgeProtocol.Disclaimer);
            }

            InjectBridgeResult result = call.Response.Result;
            return new InjectBridgeStatusReport(
                true,
                result.ExecMode,
                result.ExecModeCallResult,
                result.GcmState,
                result.GcmStateCallResult,
                result.Evidence,
                "read_status answered (call-return evidence only).",
                InjectBridgeProtocol.Disclaimer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InjectBridgeStatusReport(
                false, null, null, null, null, null, $"read_status failed: {ex.Message}",
                InjectBridgeProtocol.Disclaimer);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Read-only element read (baseline: proven mode/level 0/0, private map,
    /// NULL window, 6 s settle). See <see cref="ReadElementsAsync(string,InjectBridgeReadVariant,CancellationToken)"/>
    /// for the G1 variant matrix.
    /// </summary>
    public Task<InjectBridgeElementReadReport> ReadElementsAsync(
        string addresses,
        CancellationToken cancellationToken = default) =>
        ReadElementsAsync(addresses, variant: null, cancellationToken);

    /// <summary>
    /// Read-only element read with G1 variants (docs/inovance-autoshop-
    /// monitor-values.md §3/§4): enum values, settle window, the main-frame
    /// CWnd*-shim delivery experiment, the active-map report and the
    /// ReadWatchElemValue word-surface attempt. The reported Delivery field
    /// distinguishes answered reads (Delivered &gt; 0: real values) from
    /// receipt-only calls (Delivered == 0: call-level evidence, never PLC
    /// data). Transport/bridge failures stay failures.
    /// </summary>
    public async Task<InjectBridgeElementReadReport> ReadElementsAsync(
        string addresses,
        InjectBridgeReadVariant? variant,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(addresses))
        {
            return new InjectBridgeElementReadReport(
                false, null, null, null, "Address list must not be empty.",
                InjectBridgeProtocol.Disclaimer);
        }

        var parameters = new InjectBridgeRequestParams(
            Addresses: addresses,
            Mode: variant?.Mode,
            Level: variant?.Level,
            SettleMs: variant?.SettleMs,
            Wnd: variant != null && variant.UseMainFrameWnd ? 1 : null,
            ActiveMap: variant != null && variant.ReportActiveMap ? 1 : null,
            Watch: variant != null && variant.TryWatchPath ? 1 : null,
            Online: variant != null && variant.TryOnlineProbe ? 1 : null);

        var client = new InjectBridgePipeClient(_options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            InjectBridgeCallResult call = await client.CallAsync(
                InjectBridgeProtocol.MethodReadElements,
                parameters,
                cancellationToken).ConfigureAwait(false);
            if (!call.Succeeded || call.Response?.Result == null)
            {
                return new InjectBridgeElementReadReport(
                    false, null, null, null,
                    $"read_elements failed: {call.ErrorMessage}", InjectBridgeProtocol.Disclaimer);
            }

            InjectBridgeResult result = call.Response.Result;
            string message = BuildReadMessage(result);
            return new InjectBridgeElementReadReport(
                true, result.Count, result.Values, result.Note,
                message,
                InjectBridgeProtocol.Disclaimer,
                Delivered: result.Delivered,
                ValueSlots: result.ValueSlots,
                Variant: result.Variant,
                WatchRc: result.WatchRc,
                OnlineRc: result.OnlineRc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InjectBridgeElementReadReport(
                false, null, null, null, $"read_elements failed: {ex.Message}",
                InjectBridgeProtocol.Disclaimer);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string BuildReadMessage(InjectBridgeResult result)
    {
        if (result.Delivered is > 0)
        {
            return "read_elements answered with DELIVERED values (the vendor " +
                   $"write-back assigned {result.Delivered} value slot(s) at tagSoftAddr+0x0C; " +
                   "daemon-answered read, values below are PLC data as read by the vendor stack).";
        }
        if (!string.IsNullOrWhiteSpace(result.Values))
        {
            return "read_elements answered (in-process vendor read: per-element map fill via " +
                   "SetAt/tagCtor5, dual-slot poll of tag+0x0C (delivery slot) and tag+0x20); " +
                   "no value slot changed (Delivered=0), so these remain CALL-LEVEL evidence.";
        }
        return "read_elements answered (no values decoded).";
    }

    /// <summary>
    /// Write-surface reservation. When the C# gate is closed this returns a
    /// local refusal WITHOUT touching the pipe; when open, the bridge performs
    /// its own gate + loopback checks, resolves the export and answers
    /// resolved=true/invoked=false until the vendor signature is verified.
    /// </summary>
    public async Task<InjectBridgeWriteReport> WriteReservationAsync(
        string method,
        string addresses,
        string? target = null,
        CancellationToken cancellationToken = default)
    {
        var gate = _options.Gate;
        bool gateOpen = method switch
        {
            InjectBridgeProtocol.MethodWriteElements => gate.AllowElementWrite,
            InjectBridgeProtocol.MethodForce => gate.AllowForce,
            InjectBridgeProtocol.MethodDownload => gate.AllowDownload,
            _ => false
        };

        if (!InjectBridgeProtocol.GatedMethods.Contains(method) ||
            method == InjectBridgeProtocol.MethodCompile)
        {
            return Unsupported(method, $"Unknown write-surface method '{method}'.");
        }

        if (!InjectBridgeGateOptions.IsLoopbackAddress(target) && target != null)
        {
            return Unsupported(method,
                $"Non-loopback target '{target}' refused locally; writes are only possible to 127.0.0.0/8.");
        }

        if (!gateOpen)
        {
            return Unsupported(method,
                $"{method} is refused by the C# gate (default closed). {gate.DescribeGateConditions()}");
        }

        var client = new InjectBridgePipeClient(_options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            InjectBridgeCallResult call = await client.CallAsync(
                method,
                new InjectBridgeRequestParams(Addresses: addresses, Target: target),
                cancellationToken).ConfigureAwait(false);
            if (!call.Succeeded)
            {
                bool honestUnsupported =
                    call.ErrorMessage!.Contains($"[{InjectBridgeProtocol.ErrGateClosed}]") ||
                    call.ErrorMessage.Contains($"[{InjectBridgeProtocol.ErrUnsupported}]");
                return new InjectBridgeWriteReport(
                    false, method, true, null, null, null, null, call.ErrorMessage,
                    honestUnsupported
                        ? $"{method} answered Unsupported by the bridge (honest refusal, nothing executed). {call.ErrorMessage}"
                        : $"{method} failed: {call.ErrorMessage}",
                    InjectBridgeProtocol.Disclaimer,
                    honestUnsupported);
            }

            InjectBridgeResult result = call.Response!.Result!;
            return new InjectBridgeWriteReport(
                true, method, true,
                result.Resolved, result.Invoked, result.Export, result.ExportRva, result.Reason,
                $"{method}: vendor export resolved but NOT invoked (reservation; no value was written).",
                InjectBridgeProtocol.Disclaimer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InjectBridgeWriteReport(
                false, method, true, null, null, null, null, ex.Message,
                $"{method} failed: {ex.Message}", InjectBridgeProtocol.Disclaimer);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        InjectBridgeWriteReport Unsupported(string m, string message) =>
            new(false, m, false, null, null, null, null, message,
                $"{m}: Unsupported (honest refusal, nothing executed). {message}",
                InjectBridgeProtocol.Disclaimer, HonestUnsupported: true);
    }

    /// <summary>
    /// Compile through the bridge: the F1-verified compile-all chain is OPEN
    /// by default (bridge DLL answers with a submit receipt); a bridge that
    /// answers Unsupported (disabled surface, old DLL, project-gate refusal)
    /// surfaces as an honest failure. The C# hint only documents the
    /// intended configuration.
    /// </summary>
    public async Task<InjectBridgeCompileReport> CompileAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.Gate.CompileEntryHint))
        {
            return new InjectBridgeCompileReport(
                false, EntryConfigured: false,
                "compile: Unsupported. The E3 entry-point slot is empty (no verified compile address " +
                "delivered by the open/compile reverse engineering). " + _options.Gate.DescribeGateConditions(),
                InjectBridgeProtocol.Disclaimer);
        }

        var client = new InjectBridgePipeClient(_options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            InjectBridgeCallResult call = await client.CallAsync(
                InjectBridgeProtocol.MethodCompile, null, cancellationToken).ConfigureAwait(false);
            // The delivered bridge answers with a submit receipt when the compile
            // gate is open (F1-verified chain; BridgePipeServer.cpp HandleCompile)
            // and with a structured Unsupported/gate error when the surface is
            // disabled or a precondition failed. Only a result answer may surface
            // as Succeeded — never a timeout or an error.
            if (call.Succeeded && call.Response?.Result is { } result)
            {
                return new InjectBridgeCompileReport(
                    true, EntryConfigured: true,
                    $"compile accepted by the bridge: {result.Compile ?? result.Note ?? result.Reason ?? "no detail"}",
                    InjectBridgeProtocol.Disclaimer, HonestUnsupported: false);
            }
            return new InjectBridgeCompileReport(
                false, EntryConfigured: true,
                $"compile: Unsupported by the bridge ({call.ErrorMessage ?? "no answer"})",
                InjectBridgeProtocol.Disclaimer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InjectBridgeCompileReport(
                false, true, $"compile attempt failed: {ex.Message}",
                InjectBridgeProtocol.Disclaimer);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static InjectBridgeHandshakeReport NotAnswered(string endpoint, string message) =>
        new(false, false, null, null, null, null, null, null, Array.Empty<string>(),
            $"Handshake not answered ({endpoint}): {message}", InjectBridgeProtocol.Disclaimer);
}
