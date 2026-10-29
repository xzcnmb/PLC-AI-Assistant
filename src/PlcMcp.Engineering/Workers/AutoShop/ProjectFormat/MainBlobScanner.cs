using System.Buffers.Binary;
using System.Text;

namespace PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;

/// <summary>Kinds of segments found in a scanned Main program blob.</summary>
public enum MainBlobSegmentKind
{
    /// <summary>Prologue bytes before the cycle phase-locks (observed blob[0..21)).</summary>
    Prologue,

    /// <summary>
    /// Regular interleaved region: payload bytes at even positions, fixed-cycle bytes at odd
    /// positions (observed across the vast majority of every measured blob).
    /// </summary>
    Interleaved,

    /// <summary>
    /// Cycle-suspended region: both parities carry payload bytes (the obfuscation cycle is
    /// suspended; observed as short 1-13 byte runs at 17/56 spots on the measured samples).
    /// </summary>
    CycleSuspendedPayload,
}

/// <summary>One contiguous segment of a scanned Main blob (offset into the blob, length, kind).</summary>
public sealed class MainBlobSegment
{
    public required int Offset { get; init; }
    public required int Length { get; init; }
    public required MainBlobSegmentKind Kind { get; init; }
}

/// <summary>
/// Scan report for the opaque Main program blob (<see cref="HcppRecord.MainBlob"/>).
/// All fields describe MEASURED structure (see docs/inovance-autoshop-program-blob.md):
/// <list type="bullet">
/// <item>The blob interleaves a fixed obfuscation cycle with the payload: every other byte
/// (odd offsets) belongs to an 11-character cycle (<see cref="MainBlobScanner.KnownCycleChars"/>,
/// period 22 in blob space), phase-locked from blob offset 21 on the measured samples.</item>
/// <item>The de-interleaved payload is high-entropy (~7.98-8.00 bits/byte, statistically
/// indistinguishable from random) - encrypted with AutoShop's CAESAlg (EnDecrypt.dll);
/// its contents are NOT recoverable and this scanner never attempts decryption.</item>
/// <item>The first 180 bytes were byte-identical across two independent projects
/// (project-independent boilerplate: fixed header ciphertext).</item>
/// </list>
/// </summary>
public sealed class MainBlobScanReport
{
    public required int BlobLength { get; init; }

    /// <summary>Cycle length in characters (11 on measured samples; derived, not assumed).</summary>
    public required int CyclePeriod { get; init; }

    /// <summary>Bytes between consecutive cycle slots (2 on measured samples: odd positions).</summary>
    public required int CycleStride { get; init; }

    /// <summary>The derived cycle, ASCII-ified ('.' for non-printable bytes).</summary>
    public required string CycleChars { get; init; }

    /// <summary>First blob offset where the cycle phase-locks (21 on measured samples).</summary>
    public required int CycleStartOffset { get; init; }

    /// <summary>Fraction of cycle-slot bytes matching the derived cycle (0.9927 / 0.9984 measured).</summary>
    public required double CyclePurity { get; init; }

    /// <summary>Count of payload bytes that intruded into cycle slots (69 / 210 measured).</summary>
    public required int DeviationCount { get; init; }

    /// <summary>Number of contiguous cycle-suspended runs (17 / 56 measured).</summary>
    public required int SuspendedRunCount { get; init; }

    public required IReadOnlyList<MainBlobSegment> Segments { get; init; }

    /// <summary>Length of the de-interleaved payload (even-position bytes + intrusion bytes).</summary>
    public required int PayloadLength { get; init; }

    /// <summary>Shannon entropy of the de-interleaved payload in bits/byte (7.981 / 7.999 measured).</summary>
    public required double PayloadEntropy { get; init; }

    public required double PayloadPrintableFraction { get; init; }

    /// <summary>
    /// True when the payload entropy/printability profile matches encrypted-or-compressed
    /// material (entropy >= 7.5 bits/byte and printable fraction <= 0.5). This scanner does
    /// not decrypt; "encrypted" here is a measured statistical profile, not a decryption claim.
    /// </summary>
    public required bool PayloadLooksEncrypted { get; init; }

    /// <summary>Detected signature in the payload stream, or null (measured samples: none).</summary>
    public string? PayloadMagic { get; init; }

    /// <summary>True when the scan matches the structure measured on real AutoShop V4.12/V5.x blobs.</summary>
    public required bool MatchesKnownStructure { get; init; }

    /// <summary>Human-readable findings (facts only, no decryption claims).</summary>
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Segment scanner for the opaque Main program blob of an AutoShop .hcpp container
/// (record 0, <see cref="HcppRecord.MainBlob"/>).
/// </summary>
/// <remarks>
/// <para>
/// Measured structure (2026-10-28, Template A1.hcpp 18,944 B blob and 连续式立包机.hcpp
/// 263,612 B blob; full evidence in docs/inovance-autoshop-program-blob.md):
/// the blob is <c>[payload][cycle][payload][cycle]...</c> where the cycle bytes at odd
/// positions repeat an 11-character constant "SlotaecSfwr" phase-locked from blob offset 21
/// (purity 99.27% / 99.84%), interrupted by 17 / 56 short suspended runs where both parities
/// carry payload bytes. The de-interleaved payload (9,541 / 132,016 bytes) has entropy
/// 7.981 / 7.999 bits/byte - statistically random, consistent with AutoShop's CAESAlg
/// encryption (EnDecrypt.dll). Its contents are NOT recoverable here.
/// </para>
/// <para>
/// This scanner is a RECON tool: it verifies the structure, locates the suspended runs, and
/// extracts the raw payload for diagnostics. It does NOT decrypt and CANNOT synthesize a new
/// program blob. Generator code must keep reusing an existing blob (e.g. parsed from a
/// template), as documented in docs/inovance-autoshop-project-format.md.
/// </para>
/// </remarks>
public static class MainBlobScanner
{
    /// <summary>Fixed 11-character obfuscation cycle measured in every examined blob's odd positions.</summary>
    public const string KnownCycleChars = "SlotaecSfwr";

    /// <summary>Blob offset where the cycle phase-locks on measured samples (blob[21] == 'S').</summary>
    public const int KnownCycleStartOffset = 21;

    /// <summary>Purity above which the derived cycle is considered the known structure.</summary>
    public const double KnownStructurePurityThreshold = 0.95;

    /// <summary>Entropy (bits/byte) above which the payload profile counts as encrypted-or-compressed.</summary>
    public const double EncryptedEntropyThreshold = 7.5;

    /// <summary>Minimum blob size the scanner will analyze.</summary>
    public const int MinimumBlobLength = 64;

    public static bool TryScan(ReadOnlySpan<byte> blob, out MainBlobScanReport? report, out string? error)
    {
        report = null;
        if (blob.Length < MinimumBlobLength)
        {
            error = $"Main blob too small to scan: {blob.Length} bytes (minimum {MinimumBlobLength}).";
            return false;
        }
        report = Scan(blob);
        error = null;
        return true;
    }

    /// <summary>Scans the blob and returns the segment map. Throws on trivially small input.</summary>
    public static MainBlobScanReport Scan(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < MinimumBlobLength)
            throw new HcppFormatException($"Main blob too small to scan: {blob.Length} bytes.");
        return Scan(blob.ToArray(), cycleStride: 2);
    }

    /// <summary>
    /// Scans with an explicit cycle stride (bytes between cycle slots). Measured samples use 2
    /// (odd positions); the parameter exists for diagnostics on future variants.
    /// </summary>
    public static MainBlobScanReport Scan(byte[] blob, int cycleStride)
    {
        if (cycleStride < 2)
            throw new HcppFormatException($"Cycle stride must be >= 2, got {cycleStride}.");

        // Split the blob into the cycle-slot stream and the payload-slot stream.
        var cycleSlots = new List<byte>();
        var payloadSlots = new List<byte>();
        for (int i = 0; i < blob.Length; i++)
        {
            if (i % cycleStride == cycleStride - 1) cycleSlots.Add(blob[i]);
            else payloadSlots.Add(blob[i]);
        }

        // Derive the dominant cycle of the cycle-slot stream by majority vote per residue,
        // for candidate periods 2..16; keep the period with the best purity (ties -> smaller).
        int bestPeriod = 2;
        byte[] bestCycleRaw = Array.Empty<byte>();
        double bestPurity = -1;
        for (int period = 2; period <= 16; period++)
        {
            var (cycle, purity) = DeriveCycle(cycleSlots, period);
            if (purity > bestPurity + 1e-9)
            {
                bestPurity = purity;
                bestPeriod = period;
                bestCycleRaw = cycle;
            }
        }
        // Normalize the rotation: the majority vote anchors the cycle at slot 0, so a correct
        // derivation can still be a rotation of the known constant. Rotate it canonically.
        byte[] bestCycle = NormalizeRotation(bestCycleRaw, bestPeriod);

        // Locate deviations (payload bytes intruding into cycle slots) and phase-lock offset.
        // NOTE: deviations must use the RAW slot-0-anchored cycle - the normalized rotation
        // would compare the wrong phase against the stream.
        var deviations = new List<int>();
        int cycleStart = -1;
        for (int i = 0; i < cycleSlots.Count; i++)
        {
            if (cycleSlots[i] != bestCycleRaw[i % bestPeriod])
                deviations.Add(i);
        }
        // First index from which the cycle matches for the next 22 slots (a stable lock).
        for (int i = 0; i < cycleSlots.Count; i++)
        {
            int lookahead = Math.Min(22, cycleSlots.Count - i);
            bool stable = true;
            for (int j = i; j < i + lookahead; j++)
            {
                if (cycleSlots[j] != bestCycleRaw[j % bestPeriod]) { stable = false; break; }
            }
            if (stable && lookahead == 22) { cycleStart = i; break; }
        }

        // Contiguous suspended runs in cycle-slot indices.
        var runs = new List<(int Start, int End)>();
        if (deviations.Count > 0)
        {
            int s = deviations[0], prev = deviations[0];
            foreach (var d in deviations.Skip(1))
            {
                if (d == prev + 1) { prev = d; continue; }
                runs.Add((s, prev));
                s = d; prev = d;
            }
            runs.Add((s, prev));
        }

        // De-interleave the payload: payload-slot bytes in order + intrusion bytes in order.
        var payload = new List<byte>(payloadSlots.Count + deviations.Count);
        int devIdx = 0;
        // Reconstruct in blob order: payload byte(s) before each cycle slot.
        for (int i = 0; i < blob.Length; i++)
        {
            bool isCycleSlot = i % cycleStride == cycleStride - 1;
            if (!isCycleSlot) { payload.Add(blob[i]); continue; }
            int slotIdx = i / cycleStride;
            if (devIdx < deviations.Count && deviations[devIdx] == slotIdx)
            {
                payload.Add(cycleSlots[slotIdx]);
                devIdx++;
            }
        }

        var payloadArr = payload.ToArray();
        double entropy = ShannonEntropy(payloadArr);
        double printable = PrintableFraction(payloadArr);
        bool looksEncrypted = entropy >= EncryptedEntropyThreshold && printable <= 0.5;
        string? magic = DetectMagic(payloadArr);

        // Segments: prologue, suspended runs, interleaved gaps between them.
        var segments = new List<MainBlobSegment>();
        int cycleStartBlob = cycleStart < 0 ? 0 : cycleStart * cycleStride + (cycleStride - 1);
        if (cycleStartBlob > 0)
            segments.Add(new MainBlobSegment { Offset = 0, Length = cycleStartBlob, Kind = MainBlobSegmentKind.Prologue });
        int cursor = cycleStartBlob;
        foreach (var (s, e) in runs)
        {
            int runStartBlob = s * cycleStride + (cycleStride - 1);
            int runEndBlob = e * cycleStride + (cycleStride - 1);
            if (runStartBlob > cursor)
                segments.Add(new MainBlobSegment { Offset = cursor, Length = runStartBlob - cursor, Kind = MainBlobSegmentKind.Interleaved });
            int len = Math.Min(runEndBlob + 1, blob.Length) - runStartBlob;
            segments.Add(new MainBlobSegment { Offset = runStartBlob, Length = len, Kind = MainBlobSegmentKind.CycleSuspendedPayload });
            cursor = runStartBlob + len;
        }
        if (cursor < blob.Length)
            segments.Add(new MainBlobSegment { Offset = cursor, Length = blob.Length - cursor, Kind = MainBlobSegmentKind.Interleaved });

        // After rotation normalization, a correct derivation of the known cycle IS the known
        // constant; non-rotatable cycles (unknown period) compare literally.
        string normalizedChars = Ascii(bestCycle);
        bool matchesKnown = bestPeriod == KnownCycleChars.Length
            && cycleStride == 2
            && bestPurity >= KnownStructurePurityThreshold
            && normalizedChars == KnownCycleChars;

        var findings = new List<string>
        {
            $"Cycle: period {bestPeriod}, stride {cycleStride}, purity {bestPurity:P2}, chars \"{normalizedChars}\".",
            $"Payload: {payloadArr.Length} bytes, entropy {entropy:F3} bits/byte, printable {printable:P2}.",
            looksEncrypted
                ? "Payload profile matches encrypted-or-compressed material (measured statistically); contents NOT recoverable by this tool."
                : "Payload profile does not match the encrypted-or-compressed profile of measured AutoShop blobs.",
            $"Cycle-suspended runs: {runs.Count} ({deviations.Count} intrusion bytes).",
            matchesKnown
                ? $"Structure matches the known AutoShop obfuscation cycle \"{KnownCycleChars}\" (measured on Template A1 + 连续式立包机 blobs)."
                : "Structure does NOT match the known AutoShop obfuscation cycle.",
        };

        return new MainBlobScanReport
        {
            BlobLength = blob.Length,
            CyclePeriod = bestPeriod,
            CycleStride = cycleStride,
            CycleChars = Ascii(bestCycle),
            CycleStartOffset = cycleStartBlob,
            CyclePurity = bestPurity,
            DeviationCount = deviations.Count,
            SuspendedRunCount = runs.Count,
            Segments = segments,
            PayloadLength = payloadArr.Length,
            PayloadEntropy = entropy,
            PayloadPrintableFraction = printable,
            PayloadLooksEncrypted = looksEncrypted,
            PayloadMagic = magic,
            MatchesKnownStructure = matchesKnown,
            Findings = findings,
        };
    }

    /// <summary>
    /// Extracts the raw de-interleaved payload (even-position bytes + intrusion bytes, in blob
    /// order). Measured samples: Template A1 9,541 B, 连续式立包机 132,016 B. The payload is
    /// encrypted; the result is for diagnostics only.
    /// </summary>
    public static byte[] ExtractPayload(ReadOnlySpan<byte> blob) => ExtractPayload(blob.ToArray(), 2);

    /// <summary>Extracts the payload with an explicit cycle stride.</summary>
    public static byte[] ExtractPayload(byte[] blob, int cycleStride)
    {
        if (cycleStride < 2)
            throw new HcppFormatException($"Cycle stride must be >= 2, got {cycleStride}.");
        var cycleSlots = new List<byte>();
        for (int i = cycleStride - 1; i < blob.Length; i += cycleStride)
            cycleSlots.Add(blob[i]);

        var (cycle, _) = DeriveCycle(cycleSlots, 11);
        var payload = new List<byte>(blob.Length / cycleStride + 16);
        int slotIdx = 0;
        for (int i = 0; i < blob.Length; i++)
        {
            bool isCycleSlot = i % cycleStride == cycleStride - 1;
            if (!isCycleSlot) { payload.Add(blob[i]); continue; }
            if (cycleSlots[slotIdx] != cycle[slotIdx % 11])
                payload.Add(cycleSlots[slotIdx]);
            slotIdx++;
        }
        return payload.ToArray();
    }

    /// <summary>
    /// Majority-vote cycle for a candidate period: for each residue class, the most frequent
    /// byte. Returns the cycle bytes and the fraction of stream bytes matching them.
    /// </summary>
    private static (byte[] Cycle, double Purity) DeriveCycle(List<byte> stream, int period)
    {
        var counts = new Dictionary<int, Dictionary<byte, int>>();
        for (int i = 0; i < stream.Count; i++)
        {
            int r = i % period;
            if (!counts.TryGetValue(r, out var bucket))
                counts[r] = bucket = new Dictionary<byte, int>();
            bucket[stream[i]] = bucket.TryGetValue(stream[i], out var n) ? n + 1 : 1;
        }
        var cycle = new byte[period];
        for (int r = 0; r < period; r++)
        {
            byte best = 0; int bestN = -1;
            if (counts.TryGetValue(r, out var bucket))
            {
                foreach (var (b, n) in bucket)
                {
                    if (n > bestN) { bestN = n; best = b; }
                }
            }
            cycle[r] = best;
        }
        int match = 0;
        for (int i = 0; i < stream.Count; i++)
        {
            if (stream[i] == cycle[i % period]) match++;
        }
        double purity = stream.Count == 0 ? 0 : (double)match / stream.Count;
        return (cycle, purity);
    }

    /// <summary>
    /// Rotates a derived cycle into the canonical orientation of the known constant
    /// ("SlotaecSfwr", 'S' first) when it is a rotation of it; otherwise returns it unchanged.
    /// The majority vote anchors the cycle at slot 0, so a structurally correct derivation of
    /// the known cycle can still come out rotated.
    /// </summary>
    private static byte[] NormalizeRotation(byte[] cycle, int period)
    {
        if (period != KnownCycleChars.Length)
            return cycle;
        var known = Encoding.ASCII.GetBytes(KnownCycleChars);
        for (int shift = 0; shift < period; shift++)
        {
            bool match = true;
            for (int i = 0; i < period; i++)
            {
                if (cycle[(shift + i) % period] != known[i]) { match = false; break; }
            }
            if (match)
            {
                var rotated = new byte[period];
                for (int i = 0; i < period; i++)
                    rotated[i] = cycle[(shift + i) % period];
                return rotated;
            }
        }
        return cycle;
    }

    private static double ShannonEntropy(byte[] data)
    {
        if (data.Length == 0) return 0;
        Span<int> hist = stackalloc int[256];
        foreach (var b in data) hist[b]++;
        double e = 0;
        foreach (var n in hist)
        {
            if (n == 0) continue;
            double p = (double)n / data.Length;
            e -= p * Math.Log2(p);
        }
        return e;
    }

    private static double PrintableFraction(byte[] data)
    {
        if (data.Length == 0) return 0;
        int n = 0;
        foreach (var b in data)
        {
            if (b is >= 0x20 and <= 0x7E or 9 or 10 or 13) n++;
        }
        return (double)n / data.Length;
    }

    private static string? DetectMagic(byte[] data)
    {
        if (data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == HcppHeader.MagicLittleEndian)
            return "PPCH";
        if (data.Length >= 4 && data[0] == 0x7F && data[1] == 0x45 && data[2] == 0x4C && data[3] == 0x46)
            return "ELF";
        if (data.Length >= 2 && data[0] == 0x78 && data[1] is 0x01 or 0x5E or 0x9C or 0xDA)
            return "zlib";
        if (data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B)
            return "gzip";
        return null;
    }

    private static string Ascii(byte[] bytes) =>
        string.Create(bytes.Length, bytes, static (span, src) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = src[i] >= 0x20 && src[i] <= 0x7E ? (char)src[i] : '.';
        });
}
