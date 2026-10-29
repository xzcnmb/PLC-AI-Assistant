using System.Security.Cryptography;
using System.Text.Json;
using PlcMcp.Engineering.Workers.External;

namespace PlcMcp.Server.Governance;

/// <summary>
/// Host-side configuration for the .NET Framework 4.8 GX Works3 metadata probe worker.
/// </summary>
public sealed class GxWorks3ProbeConfig
{
    /// <summary>
    /// Absolute path to PlcMcp.GxWorks3.Worker.exe.
    /// </summary>
    public string ProbeExePath { get; set; } = string.Empty;

    /// <summary>
    /// Expected SHA256 of PlcMcp.GxWorks3.Worker.exe (hex).
    /// </summary>
    public string ProbeExeSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to target GXW3.exe.
    /// </summary>
    public string Gxw3ExePath { get; set; } = string.Empty;

    /// <summary>
    /// Expected exact version of GXW3.exe.
    /// </summary>
    public string ExpectedVersion { get; set; } = string.Empty;

    /// <summary>
    /// Pinned worker name for handshake validation.
    /// </summary>
    public string ExpectedWorkerName { get; set; } = "GxWorks3-Metadata-Worker";

    /// <summary>
    /// Pinned worker version for handshake validation.
    /// </summary>
    public string ExpectedWorkerVersion { get; set; } = "1.0";

    /// <summary>
    /// Pinned protocol version for handshake validation.
    /// </summary>
    public string ExpectedProtocolVersion { get; set; } = "1.0";

    /// <summary>
    /// Pinned vendor for handshake validation.
    /// </summary>
    public string ExpectedVendor { get; set; } = "Mitsubishi";

    /// <summary>
    /// Pinned bitness for handshake validation.
    /// </summary>
    public string ExpectedBitness { get; set; } = "32-bit";

    /// <summary>
    /// Timeout for probe operations in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Max output bytes budget.
    /// </summary>
    public long MaxOutputBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Validates that all required fields are present, absolute, and meet security constraints.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProbeExePath))
            throw new ArgumentException("ProbeExePath is required.");
        if (string.IsNullOrWhiteSpace(ProbeExeSha256))
            throw new ArgumentException("ProbeExeSha256 is required.");
        if (string.IsNullOrWhiteSpace(Gxw3ExePath))
            throw new ArgumentException("Gxw3ExePath is required.");
        if (string.IsNullOrWhiteSpace(ExpectedVersion))
            throw new ArgumentException("ExpectedVersion is required.");

        if (!Path.IsPathRooted(ProbeExePath))
            throw new ArgumentException($"ProbeExePath must be an absolute path: '{ProbeExePath}'.");
        if (!Path.IsPathRooted(Gxw3ExePath))
            throw new ArgumentException($"Gxw3ExePath must be an absolute path: '{Gxw3ExePath}'.");

        var normalizedProbe = Path.GetFullPath(ProbeExePath);
        var normalizedGxw3 = Path.GetFullPath(Gxw3ExePath);

        if (!File.Exists(normalizedProbe))
            throw new FileNotFoundException($"Probe executable not found: '{normalizedProbe}'.", normalizedProbe);

        // Verify SHA256 of probe executable
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(normalizedProbe);
        string actualHash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, ProbeExeSha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Probe executable SHA-256 mismatch. Expected: '{ProbeExeSha256}', Actual: '{actualHash}'.");
        }
    }
}

/// <summary>
/// Client that executes the GX Works3 standalone probe worker, runs handshake and doctor,
/// performs graceful shutdown, and returns structured raw evidence.
/// </summary>
public sealed class GxWorks3ProbeClient
{
    private readonly GxWorks3ProbeConfig _config;
    private readonly IProcessRunner _processRunner;

    public GxWorks3ProbeConfig Config => _config;

    public GxWorks3ProbeClient(GxWorks3ProbeConfig config, IProcessRunner? processRunner = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _processRunner = processRunner ?? new DefaultProcessRunner();
    }

    /// <summary>
    /// Invokes the external probe worker, runs handshake and raw doctor JSON-RPC calls,
    /// requests graceful shutdown, and returns the raw doctor report JsonElement.
    /// </summary>
    public async Task<JsonElement> RunDoctorAsync(CancellationToken cancellationToken = default)
    {
        _config.Validate();

        var probeFull = Path.GetFullPath(_config.ProbeExePath);
        var gxw3Full = Path.GetFullPath(_config.Gxw3ExePath);

        var securityPolicy = new ExternalWorkerSecurityPolicy
        {
            MaxOutputBytes = _config.MaxOutputBytes,
            RpcTimeout = TimeSpan.FromSeconds(_config.TimeoutSeconds),
            MaxProcessLifetime = TimeSpan.FromSeconds(_config.TimeoutSeconds + 10)
        };
        securityPolicy.AllowedExecutablePaths.Add(probeFull);

        var args = new[]
        {
            "--gx-exe", gxw3Full,
            "--expected-version", _config.ExpectedVersion
        };

        await using var client = new ExternalWorkerClient(
            executablePath: probeFull,
            arguments: args,
            securityPolicy: securityPolicy,
            processRunner: _processRunner,
            gracefulShutdown: true);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await client.StartAsync(linkedCts.Token).ConfigureAwait(false);

        // 1. Handshake
        var handshake = await client.HandshakeAsync(
            hostVersion: "1.0.0",
            supportedVendors: new[] { "Mitsubishi" },
            cancellationToken: default);

        if (!string.Equals(handshake.WorkerName, _config.ExpectedWorkerName, StringComparison.Ordinal))
            throw new InvalidOperationException($"WorkerName mismatch: expected '{_config.ExpectedWorkerName}', got '{handshake.WorkerName}'.");

        if (!string.Equals(handshake.WorkerVersion, _config.ExpectedWorkerVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"WorkerVersion mismatch: expected '{_config.ExpectedWorkerVersion}', got '{handshake.WorkerVersion}'.");

        if (!string.Equals(handshake.ProtocolVersion, _config.ExpectedProtocolVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"ProtocolVersion mismatch: expected '{_config.ExpectedProtocolVersion}', got '{handshake.ProtocolVersion}'.");

        if (!string.Equals(handshake.Vendor, _config.ExpectedVendor, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Vendor mismatch: expected '{_config.ExpectedVendor}', got '{handshake.Vendor}'.");

        if (!string.Equals(handshake.Bitness, _config.ExpectedBitness, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Bitness mismatch: expected '{_config.ExpectedBitness}', got '{handshake.Bitness}'.");

        // Validate capabilities contain only HostDiagnostics and DependencyMetadata
        var requiredCaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HostDiagnostics", "DependencyMetadata" };
        foreach (var cap in handshake.Capabilities)
        {
            if (!requiredCaps.Contains(cap))
            {
                throw new InvalidOperationException($"Unexpected capability '{cap}' advertised by probe worker.");
            }
        }

        // 2. Request raw doctor JsonElement
        var doctorResponse = await client.SendRequestAsync<JsonElement>("doctor", new { }, linkedCts.Token).ConfigureAwait(false);

        // Client disposal will trigger graceful shutdown {"method":"shutdown"} per gracefulShutdown: true.
        return doctorResponse;
    }
}
