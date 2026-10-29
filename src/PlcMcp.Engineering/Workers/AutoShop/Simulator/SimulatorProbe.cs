using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Engineering.Workers.AutoShop.Simulator;

/// <summary>Options for <see cref="SimulatorProbe"/>. Host must be loopback (red line, enforced).</summary>
public sealed class SimulatorProbeOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int ModbusPort { get; init; } = 502;

    /// <summary>
    /// Modbus unit id. The vendor daemon answers unit-agnostically (verified 2026-10-24);
    /// 1 is used as the conventional default. Broadcast unit 0 is rejected.
    /// </summary>
    public byte UnitId { get; init; } = 1;

    public int TimeoutMs { get; init; } = 3000;

    /// <summary>AutoShop native protocol port (used only by the optional TestHost wrapper).</summary>
    public int NativePort { get; init; } = 12939;

    /// <summary>
    /// Explicit path to TestHost.exe (the read-only native-protocol harness built from
    /// tools/AutoShopNativeBridge). When null, the standard %LOCALAPPDATA% location is probed.
    /// </summary>
    public string? TestHostPath { get; init; }

    /// <summary>Target passed to TestHost via --target=ip:port. Loopback only.</summary>
    public string NativeTarget { get; init; } = "127.0.0.1:12939";

    public int NativeTimeoutMs { get; init; } = 20000;

    /// <summary>Holding-register addresses probed by default (D0->HR0, D100->HR100, R0->HR32768).</summary>
    public IReadOnlyList<ushort> DefaultHoldingAddresses { get; init; } = [0, 100, 32768];

    /// <summary>Coil addresses probed by default (M0->coil 1024).</summary>
    public IReadOnlyList<ushort> DefaultCoilAddresses { get; init; } = [1024];

    internal void Validate()
    {
        SimulatorLauncherOptions.EnsureLoopbackHost(Host, nameof(Host));
        SimulatorLauncherOptions.ValidatePort(ModbusPort, nameof(ModbusPort));
        SimulatorLauncherOptions.ValidatePort(NativePort, nameof(NativePort));
        if (UnitId is < 1 or > 255)
            throw new ArgumentException("UnitId must be between 1 and 255; broadcast unit 0 is not permitted.", nameof(UnitId));
        if (TimeoutMs is < 100 or > 30000)
            throw new ArgumentException("TimeoutMs must be between 100 and 30000 ms.", nameof(TimeoutMs));
        if (NativeTimeoutMs is < 500 or > 120000)
            throw new ArgumentException("NativeTimeoutMs must be between 500 and 120000 ms.", nameof(NativeTimeoutMs));
        int separator = NativeTarget.LastIndexOf(':');
        if (separator <= 0)
            throw new ArgumentException("NativeTarget must have the form host:port.", nameof(NativeTarget));
        SimulatorLauncherOptions.EnsureLoopbackHost(NativeTarget[..separator], nameof(NativeTarget));
    }
}

/// <summary>Modbus exception reported by the responder (function code | 0x80).</summary>
public sealed class ModbusProbeException(byte exceptionCode)
    : InvalidOperationException($"Modbus exception code {exceptionCode:X2}.")
{
    public byte ExceptionCode { get; } = exceptionCode;
}

/// <summary>
/// Read-only Modbus TCP probe for the local H5U simulator daemon plus an optional wrapper
/// around the read-only native-protocol harness (TestHost.exe --target).
///
/// Red lines encoded in code: every request is built by <see cref="BuildReadRequest"/>, which
/// only accepts function codes 1/2/3/4 and refuses every write function code; the probe host
/// and the native target are loopback-validated; nothing in this class can write to the
/// simulator. Latency is measured from request write to response completion.
/// </summary>
public sealed class SimulatorProbe
{
    private readonly SimulatorProbeOptions _options;
    private int _transaction;

    public SimulatorProbe(SimulatorProbeOptions? options = null)
    {
        _options = options ?? new SimulatorProbeOptions();
        _options.Validate();
    }

    public SimulatorProbeOptions Options => _options;

    // ---- frame construction / parsing (public for offline tests) ---------

    /// <summary>
    /// Builds a 12-byte read-only Modbus TCP request (MBAP header + PDU). Write function
    /// codes (5, 6, 15, 16, ...) are rejected so a write frame is structurally impossible.
    /// </summary>
    public static byte[] BuildReadRequest(ushort transaction, byte unit, byte function, ushort address, ushort count)
    {
        if (function is not (1 or 2 or 3 or 4))
            throw new ArgumentException(
                $"Function code {function} is not a read function. The simulator probe only ever generates FC1/FC2/FC3/FC4; writes are forbidden.");
        if (unit < 1)
            throw new ArgumentException("Unit id must be 1 to 255; broadcast unit 0 is not permitted.", nameof(unit));
        int maxCount = function is 1 or 2 ? 2000 : 125;
        if (count is < 1 || count > maxCount)
            throw new ArgumentException($"Read count must be between 1 and {maxCount} for FC{function}.", nameof(count));
        if (address + (long)count > 65536)
            throw new ArgumentException($"Address {address} + count {count} exceeds the 65536 Modbus address space.", nameof(address));

        var request = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(request, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // protocol id
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 6); // remaining length
        request[6] = unit;
        request[7] = function;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), count);
        return request;
    }

    /// <summary>Validates a Modbus read response and decodes registers (FC3/FC4) or coil bits (FC1/FC2, LSB first).</summary>
    public static SimulatorReadResponse ParseReadResponse(
        ReadOnlySpan<byte> frame, ushort expectedTransaction, byte expectedUnit, byte expectedFunction, ushort expectedCount)
    {
        if (expectedFunction is not (1 or 2 or 3 or 4))
            throw new ArgumentException("Only read function codes 1/2/3/4 are supported.", nameof(expectedFunction));
        if (frame.Length < 9)
            throw new InvalidDataException($"Modbus response frame too short ({frame.Length} bytes; at least 9 required).");
        ushort transaction = BinaryPrimitives.ReadUInt16BigEndian(frame);
        ushort protocol = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(2, 2));
        byte unit = frame[6];
        byte function = frame[7];

        if (transaction != expectedTransaction || protocol != 0)
            throw new InvalidDataException(
                $"Modbus response header mismatch: transaction {transaction} (expected {expectedTransaction}), protocol {protocol}.");
        if (unit != expectedUnit)
            throw new InvalidDataException($"Modbus response unit {unit} does not match request unit {expectedUnit}.");

        int expectedBytes = expectedFunction is 1 or 2 ? (expectedCount + 7) / 8 : expectedCount * 2;
        if (function == (expectedFunction | 0x80))
        {
            byte code = frame.Length > 8 ? frame[8] : (byte)0;
            throw new ModbusProbeException(code);
        }

        if (function != expectedFunction)
            throw new InvalidDataException($"Modbus response function {function:X2} does not match request {expectedFunction:X2}.");
        int byteCount = frame[8];
        if (byteCount != expectedBytes)
            throw new InvalidDataException($"Modbus byte count {byteCount} does not match expected {expectedBytes}.");
        if (frame.Length != 9 + expectedBytes)
            throw new InvalidDataException($"Modbus response length {frame.Length} does not match byte count {byteCount}.");

        var values = new ushort[expectedCount];
        if (expectedFunction is 3 or 4)
        {
            for (int i = 0; i < expectedCount; i++)
            {
                values[i] = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(9 + i * 2, 2));
            }
        }
        else
        {
            for (int i = 0; i < expectedCount; i++)
            {
                values[i] = (ushort)((frame[9 + i / 8] >> (i % 8)) & 1);
            }
        }

        return new SimulatorReadResponse(transaction, unit, function, values);
    }

    // ---- live probes ------------------------------------------------------

    /// <summary>Reads holding registers (FC3) at the given zero-based Modbus address (D->HR0, R->HR32768).</summary>
    public Task<SimulatorRegisterReading> ProbeHoldingRegistersAsync(ushort address, ushort count = 1,
        CancellationToken cancellationToken = default) =>
        ProbeReadAsync("holding-register", 3, address, count, cancellationToken);

    /// <summary>Reads coils (FC1) at the given zero-based Modbus address (M0->1024).</summary>
    public Task<SimulatorRegisterReading> ProbeCoilsAsync(ushort address, ushort count = 1,
        CancellationToken cancellationToken = default) =>
        ProbeReadAsync("coil", 1, address, count, cancellationToken);

    /// <summary>
    /// Runs the default read-only probe set (a few holding registers + coils, count 1 each)
    /// and the optional native probe. One failing reading does not abort the report.
    /// </summary>
    public async Task<SimulatorProbeReport> ProbeAsync(
        IReadOnlyList<ushort>? holdingAddresses = null,
        IReadOnlyList<ushort>? coilAddresses = null,
        CancellationToken cancellationToken = default)
    {
        var readings = new List<SimulatorRegisterReading>();
        foreach (ushort address in holdingAddresses ?? _options.DefaultHoldingAddresses)
        {
            readings.Add(await ProbeHoldingRegistersAsync(address, 1, cancellationToken).ConfigureAwait(false));
        }

        foreach (ushort address in coilAddresses ?? _options.DefaultCoilAddresses)
        {
            readings.Add(await ProbeCoilsAsync(address, 1, cancellationToken).ConfigureAwait(false));
        }

        NativeProbeResult native;
        try
        {
            native = await ProbeNativeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            native = new NativeProbeResult(false, false, null, null, false, string.Empty, 0, $"native probe failed: {ex.Message}");
        }

        bool ok = readings.Count > 0 && readings.All(r => r.Quality == QualityCode.Good);
        string details = $"{readings.Count} read-only Modbus probes on {_options.Host}:{_options.ModbusPort} " +
                         $"(good: {readings.Count(r => r.Quality == QualityCode.Good)}); native probe: " +
                         (native.Skipped ? $"SKIPPED ({native.SkipReason})" : native.Attempted ? $"attempted, online={native.Online}" : "not attempted") +
                         ". Zero write frames were generated.";
        return new SimulatorProbeReport(ok, readings, native, details, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Optional native-protocol probe: runs TestHost.exe --target=&lt;loopback:port&gt; (the
    /// read-only CommStackShim harness) and reports whether the vendor stack reports ONLINE.
    /// When the harness binary is missing the probe is skipped with an explicit reason.
    /// </summary>
    public async Task<NativeProbeResult> ProbeNativeAsync(CancellationToken cancellationToken = default)
    {
        string? testHost = ResolveTestHostPath();
        if (testHost is null)
        {
            return new NativeProbeResult(false, true,
                "TestHost.exe not found; build tools/AutoShopNativeBridge per docs/inovance-autoshop-native-bridge.md " +
                "(expected at %LOCALAPPDATA%\\AutoShopNativeBridge-run\\TestHost.exe). Native probe skipped.",
                null, false, string.Empty, 0, null);
        }

        var psi = new ProcessStartInfo
        {
            FileName = testHost,
            Arguments = $"--target={_options.NativeTarget}",
            WorkingDirectory = Path.GetDirectoryName(testHost) ?? ".",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        long started = Stopwatch.GetTimestamp();
        try
        {
            using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"Process.Start returned null for '{testHost}'.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.NativeTimeoutMs);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort cleanup of our own child */ }
                return new NativeProbeResult(true, false, null, null, false, string.Empty,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    $"TestHost did not exit within {_options.NativeTimeoutMs} ms and was terminated.");
            }

            string output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            string tail = output.Length > 4000 ? output[^4000..] : output;
            bool online = output.Contains("(ONLINE)", StringComparison.Ordinal) ||
                          output.Contains("PlcMcpComm_IsOnline() -> 1", StringComparison.Ordinal);
            return new NativeProbeResult(true, false, null, process.ExitCode, online, tail,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
        }
        catch (Exception ex)
        {
            return new NativeProbeResult(true, false, null, null, false, string.Empty,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
        }
    }

    private string? ResolveTestHostPath()
    {
        if (_options.TestHostPath is { Length: > 0 })
        {
            return File.Exists(_options.TestHostPath) ? _options.TestHostPath : null;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string candidate = Path.Combine(localAppData, "AutoShopNativeBridge-run", "TestHost.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private async Task<SimulatorRegisterReading> ProbeReadAsync(string kind, byte function, ushort address, ushort count,
        CancellationToken cancellationToken)
    {
        try
        {
            ushort transaction = (ushort)Interlocked.Increment(ref _transaction);
            byte[] request = BuildReadRequest(transaction, _options.UnitId, function, address, count);

            using var client = new TcpClient { NoDelay = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.TimeoutMs);
            await client.ConnectAsync(_options.Host, _options.ModbusPort, cts.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();

            long started = Stopwatch.GetTimestamp();
            await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);

            byte[] header = await ReadExactlyAsync(stream, 9, cts.Token).ConfigureAwait(false);
            int byteCount = header[8];
            if (byteCount is < 1 or > 260)
                throw new InvalidDataException($"Modbus byte count {byteCount} is outside the plausible range.");
            byte[] payload = await ReadExactlyAsync(stream, byteCount, cts.Token).ConfigureAwait(false);
            double latencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var frame = new byte[9 + byteCount];
            header.CopyTo(frame, 0);
            payload.CopyTo(frame, 9);
            SimulatorReadResponse response = ParseReadResponse(frame, transaction, _options.UnitId, function, count);
            return new SimulatorRegisterReading(kind, address, count, function, response.Values,
                Math.Round(latencyMs, 3), QualityCode.Good, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SimulatorRegisterReading(kind, address, count, function, [],
                _options.TimeoutMs, QualityCode.Bad,
                $"Simulator Modbus probe timed out after {_options.TimeoutMs} ms (connect + request + response).",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new SimulatorRegisterReading(kind, address, count, function, [],
                -1, QualityCode.Bad, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }
}
