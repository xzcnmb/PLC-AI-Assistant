namespace PlcMcp.Engineering.Workers.AutoShop.NativeBridge;

/// <summary>
/// Handshake report of the native bridge (pipe + plugin + comm-stack shim).
/// Self-reported worker data is never promoted above Experimental.
/// </summary>
public sealed record NativeBridgeHandshakeReport(
    bool Connected,
    bool Answered,
    string? WorkerName,
    string? WorkerVersion,
    string? ProtocolVersion,
    string? Bitness,
    string? RuntimeEnvironment,
    IReadOnlyList<string> Capabilities,
    string Message,
    string Disclaimer)
{
    public bool MatchesExpectation(out string mismatchReason)
    {
        mismatchReason = string.Empty;
        if (!Answered)
        {
            mismatchReason = "Native bridge did not answer the handshake.";
            return false;
        }
        if (!string.Equals(WorkerName, NativeBridgeProtocol.WorkerName, StringComparison.Ordinal))
        {
            mismatchReason = $"WorkerName mismatch: expected '{NativeBridgeProtocol.WorkerName}', actual '{WorkerName}'.";
            return false;
        }
        if (!string.Equals(ProtocolVersion, NativeBridgeProtocol.ProtocolVersion, StringComparison.Ordinal))
        {
            mismatchReason = $"ProtocolVersion mismatch: expected '{NativeBridgeProtocol.ProtocolVersion}', actual '{ProtocolVersion}'.";
            return false;
        }
        return true;
    }
}

/// <summary>
/// Result of a read-only PLC status request through the bridge.
/// Values are call-return evidence only: without a verified PLC connection the
/// numeric fields are the vendor stub/zero results and must not be presented
/// as real PLC state.
/// </summary>
public sealed record NativeBridgeStatusReport(
    bool Succeeded,
    int? ExecMode,
    long? ExecModeCallResult,
    int? GcmState,
    long? GcmStateCallResult,
    string? Evidence,
    string Message,
    string Disclaimer);

/// <summary>
/// Result of a read-only element read request through the bridge. The bridge
/// only reports call-return evidence and the raw value blob; decoding of
/// vendor element values is not implemented (layout unverified), so
/// <see cref="Values"/> stays empty unless the native side reports otherwise.
/// </summary>
public sealed record NativeBridgeElementReadReport(
    bool Succeeded,
    int? Count,
    string? Values,
    string Message,
    string Disclaimer);

/// <summary>
/// Facade over the native bridge pipe client. Owns the configuration
/// (pipe name, server, timeout) and maps responses onto the report models.
/// </summary>
public sealed class NativeBridgeServices
{
    public const string BridgeName = "AutoShop-Native-Bridge";
    public const string LockedVendorVersion = NativeBridgeProtocol.LockedVendorVersion;

    private readonly NativeBridgeClientOptions _options;

    public NativeBridgeServices(NativeBridgeClientOptions? options = null)
    {
        _options = options ?? new NativeBridgeClientOptions();
    }

    public NativeBridgeClientOptions Options => _options;

    /// <summary>
    /// Connects and performs the handshake. Transport failures produce a
    /// report with Connected=false instead of throwing.
    /// </summary>
    public async Task<NativeBridgeHandshakeReport> HandshakeAsync(
        string clientName = "PlcMcp",
        CancellationToken cancellationToken = default)
    {
        var client = new NativeBridgePipeClient(_options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            NativeBridgeCallResult call = await client.CallAsync(
                NativeBridgeProtocol.MethodHandshake,
                new NativeBridgeRequestParams(Client: clientName),
                cancellationToken).ConfigureAwait(false);
            if (!call.Succeeded || call.Response?.Result == null)
            {
                return NotAnswered(client.Endpoint, call.ErrorMessage ?? "no result");
            }

            NativeBridgeResult result = call.Response.Result;
            return new NativeBridgeHandshakeReport(
                Connected: true,
                Answered: true,
                WorkerName: result.WorkerName,
                WorkerVersion: result.WorkerVersion,
                ProtocolVersion: result.ProtocolVersion,
                Bitness: result.Bitness,
                RuntimeEnvironment: result.RuntimeEnvironment,
                Capabilities: result.CapabilityList ?? Array.Empty<string>(),
                Message: "Handshake answered.",
                Disclaimer: NativeBridgeProtocol.Disclaimer);
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

    /// <summary>
    /// Read-only status request: connect, handshake, read_status, shutdown.
    /// Each step is failure-safe; nothing is written to the PLC.
    /// </summary>
    public async Task<NativeBridgeStatusReport> ReadStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var client = new NativeBridgePipeClient(_options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            NativeBridgeCallResult handshake = await client.CallAsync(
                NativeBridgeProtocol.MethodHandshake,
                new NativeBridgeRequestParams(Client: "PlcMcp"),
                cancellationToken).ConfigureAwait(false);
            if (!handshake.Succeeded)
            {
                return StatusFailed(handshake.ErrorMessage ?? "handshake failed");
            }

            NativeBridgeCallResult call = await client.CallAsync(
                NativeBridgeProtocol.MethodReadStatus, null, cancellationToken).ConfigureAwait(false);
            if (!call.Succeeded || call.Response?.Result == null)
            {
                return StatusFailed(call.ErrorMessage ?? "read_status returned no result");
            }

            NativeBridgeResult result = call.Response.Result;
            return new NativeBridgeStatusReport(
                Succeeded: true,
                ExecMode: result.ExecMode,
                ExecModeCallResult: result.ExecModeCallResult,
                GcmState: result.GcmState,
                GcmStateCallResult: result.GcmStateCallResult,
                Evidence: result.Evidence,
                Message: "read_status answered.",
                Disclaimer: NativeBridgeProtocol.Disclaimer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StatusFailed(ex.Message);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Read-only element read request: connect, handshake, read_elements.
    /// Addresses are a comma-separated vendor soft-element list (e.g. "D100,D101").
    /// </summary>
    public async Task<NativeBridgeElementReadReport> ReadElementsAsync(
        string addresses,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(addresses))
        {
            return new NativeBridgeElementReadReport(
                false, null, null, "Address list must not be empty.", NativeBridgeProtocol.Disclaimer);
        }

        var client = new NativeBridgePipeClient(_options);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            NativeBridgeCallResult handshake = await client.CallAsync(
                NativeBridgeProtocol.MethodHandshake,
                new NativeBridgeRequestParams(Client: "PlcMcp"),
                cancellationToken).ConfigureAwait(false);
            if (!handshake.Succeeded)
            {
                return ReadFailed(handshake.ErrorMessage ?? "handshake failed");
            }

            NativeBridgeCallResult call = await client.CallAsync(
                NativeBridgeProtocol.MethodReadElements,
                new NativeBridgeRequestParams(Addresses: addresses),
                cancellationToken).ConfigureAwait(false);
            if (!call.Succeeded || call.Response?.Result == null)
            {
                return ReadFailed(call.ErrorMessage ?? "read_elements returned no result");
            }

            NativeBridgeResult result = call.Response.Result;
            return new NativeBridgeElementReadReport(
                Succeeded: true,
                Count: result.Count,
                Values: result.Values,
                Message: "read_elements answered.",
                Disclaimer: NativeBridgeProtocol.Disclaimer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReadFailed(ex.Message);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static NativeBridgeHandshakeReport NotAnswered(string endpoint, string message) =>
        new(false, false, null, null, null, null, null, Array.Empty<string>(),
            $"Handshake not answered ({endpoint}): {message}", NativeBridgeProtocol.Disclaimer);

    private static NativeBridgeStatusReport StatusFailed(string message) =>
        new(false, null, null, null, null, null,
            $"Native bridge status read failed: {message}", NativeBridgeProtocol.Disclaimer);

    private static NativeBridgeElementReadReport ReadFailed(string message) =>
        new(false, null, null,
            $"Native bridge element read failed: {message}", NativeBridgeProtocol.Disclaimer);
}
