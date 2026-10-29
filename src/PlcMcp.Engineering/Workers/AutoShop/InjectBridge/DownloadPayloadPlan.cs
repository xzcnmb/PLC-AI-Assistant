// G2 download-closed-loop: the byte-level "编译产物 → 可下载载荷" conversion plan.
//
// T1 static finding (docs/inovance-autoshop-download-closedloop.md §2): the
// AutoShop download dialog does NOT create a special wire payload. It walks a
// fixed list of project output files and transfers each of them VERBATIM over
// the 12939 file-transfer chain (serverId 4, session type 2 → chunks type 5 →
// finish 0x0F) via IH530Communicate::DownloadFile. The file order was read off
// the vendor call sites in AutoShop.exe (0xE63F00–0xE7B400 function family,
// wide-name pushes at 0xe63f71/0xe64245/0xe64437/0xe64666/0xe648f3/0xe64bb2):
//   project.hcpp, userprog.foid, stprog.o, stuserprog.o, symbol.sym, varsinfo.foid
// (EIPIns.foid appears in a further variant; net.down/net.tmp/net.up are
// project-package members for the "以太网配置" download, not program-transfer frames).
//
// So the minimal transformation is: take the repackaged project.hcpp (PPCH
// container produced by the compile pipeline, E3 §5.4) and send it under the
// remote name "project\project.hcpp". Everything else is validation.
//
// Pure functions only — no sockets, no filesystem. Loopback discipline lives in
// the transport callers (DownloadProtocolClient hard-checks 127.0.0.0/8).
// Reverse-engineered, no vendor endorsement, version-locked to AutoShop 4.12.0.2.

using PlcMcp.Engineering.Workers.AutoShop.DownloadProtocol;

namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

/// <summary>One file of the download transaction: remote name (daemon-relative) + exact bytes.</summary>
/// <param name="RemoteName">Daemon-relative file name, e.g. "project\\project.hcpp" or "stprog.o".</param>
/// <param name="Bytes">Verbatim content to transfer (the payload IS the file).</param>
public readonly record struct DownloadPayloadFile(string RemoteName, ReadOnlyMemory<byte> Bytes);

/// <summary>
/// Builds the ordered per-file download plan for the program-download step of
/// the AutoShop download workflow, mirroring the vendor call-site order.
/// </summary>
public static class DownloadPayloadPlan
{
    /// <summary>Remote name of the repackaged project container (the minimal payload).</summary>
    public const string ProjectHcppRemoteName = @"project\project.hcpp";

    /// <summary>Vendor call-site file order for the program-download step (T1 evidence).</summary>
    public static IReadOnlyList<string> VendorFileOrder { get; } = new[]
    {
        ProjectHcppRemoteName,
        "userprog.foid",
        "stprog.o",
        "stuserprog.o",
        "symbol.sym",
        "varsinfo.foid",
    };

    /// <summary>
    /// Minimal plan: the repackaged .hcpp only. The daemon backs up the previous
    /// file (project_backup.hcpp) and truncates on session open, so callers get
    /// rollback semantics for free (D1 §四).
    /// </summary>
    /// <exception cref="ArgumentException">The bytes do not start with the PPCH magic.</exception>
    public static IReadOnlyList<DownloadPayloadFile> ForRepackedProject(ReadOnlyMemory<byte> hcppBytes)
    {
        EnsureHcppContainer(hcppBytes);
        return new[] { new DownloadPayloadFile(ProjectHcppRemoteName, hcppBytes) };
    }

    /// <summary>
    /// Full plan: every supplied artifact, ordered by the vendor call-site order;
    /// unknown extras are appended after them in sorted order for determinism.
    /// </summary>
    /// <param name="repackedHcpp">The PPCH container (required — the daemon load path reads it).</param>
    /// <param name="artifacts">Base file names (e.g. "stprog.o") to their exact bytes.</param>
    /// <exception cref="KeyNotFoundException">A vendor-ordered file was not supplied and is not optional.</exception>
    public static IReadOnlyList<DownloadPayloadFile> ForFullProject(
        ReadOnlyMemory<byte> repackedHcpp,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        EnsureHcppContainer(repackedHcpp);

        var plan = new List<DownloadPayloadFile> { new(ProjectHcppRemoteName, repackedHcpp) };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProjectHcppRemoteName };
        foreach (var name in VendorFileOrder)
        {
            if (used.Contains(name))
            {
                continue;
            }
            // The compile stage does not always emit every member (e.g. stuserprog.o
            // absent for LD-only projects); members are optional, order is not.
            if (artifacts.TryGetValue(name, out var bytes))
            {
                plan.Add(new DownloadPayloadFile(name, bytes));
                used.Add(name);
            }
        }
        foreach (var extra in artifacts.Keys.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            plan.Add(new DownloadPayloadFile(extra, artifacts[extra]));
        }
        return plan;
    }

    /// <summary>
    /// PPCH container check: the repackaged .hcpp produced by the compile
    /// pipeline starts with the magic "PPCH" (P1 container format). A raw
    /// .compile/.foid blob is NOT a valid single-file payload.
    /// </summary>
    public static void EnsureHcppContainer(ReadOnlyMemory<byte> hcppBytes)
    {
        var span = hcppBytes.Span;
        if (span.Length < 4 || span[0] != (byte)'P' || span[1] != (byte)'P' ||
            span[2] != (byte)'C' || span[3] != (byte)'H')
        {
            throw new ArgumentException(
                "download payload is not a PPCH container: pass the repackaged project.hcpp " +
                "(compile output container), not a raw .compile/.foid blob", nameof(hcppBytes));
        }
    }

    /// <summary>
    /// Builds the file-transfer session command frame for one plan entry — the
    /// exact frame the daemon truncates/backs-up on (D1 §四). Exposed so the
    /// byte layout is unit-testable without a daemon.
    /// </summary>
    public static byte[] BuildSessionFrame(DownloadPayloadFile file, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, DownloadFrame.MaxPayloadLength);
        var command = new byte[0x12 + file.RemoteName.Length + 1];
        command[0] = DownloadFrame.FilePayloadTypes.DownloadSession;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(0x0A), chunkSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(0x0E), file.Bytes.Length);
        System.Text.Encoding.ASCII.GetBytes(file.RemoteName).CopyTo(command.AsSpan(0x12));
        return DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.FileTransfer, command);
    }
}
