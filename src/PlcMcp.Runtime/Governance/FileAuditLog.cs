using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime.Governance;

public interface IAuditLog
{
    ValueTask<AuditRecord> AppendAsync(
        string eventType,
        string targetId,
        string operatorId,
        string action,
        object details,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AuditRecord>> ReadAllAsync(CancellationToken cancellationToken = default);

    ValueTask<AuditVerificationResult> VerifyChainAsync(CancellationToken cancellationToken = default);
}

public sealed record AuditVerificationResult(
    bool IsValid,
    int TotalRecords,
    long? TamperedSequenceNumber = null,
    string? ErrorMessage = null);

public sealed record TornTailRepairResult(
    bool Repaired,
    long DroppedBytes,
    string? DroppedContent,
    int RemainingRecords,
    string? ErrorMessage = null);

public class AuditLogCorruptedException : InvalidOperationException
{
    public long? SequenceNumber { get; }

    public AuditLogCorruptedException(string message, long? sequenceNumber = null, Exception? innerException = null)
        : base(message, innerException)
    {
        SequenceNumber = sequenceNumber;
    }
}

public sealed class FileAuditLog : IAuditLog
{
    private const string InitialPrevHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _filePath;
    private readonly object _syncLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public FileAuditLog(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public static string ComputeRecordHash(
        long sequenceNumber,
        DateTimeOffset timestamp,
        string eventType,
        string targetId,
        string operatorId,
        string action,
        string detailsJson,
        string prevHash)
    {
        var rawPayload = $"{sequenceNumber}|{timestamp:O}|{eventType}|{targetId}|{operatorId}|{action}|{detailsJson}|{prevHash}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private sealed record ParsedLineInfo(
        int LineIndex,
        long StartOffset,
        long EndOffset,
        string LineText,
        bool HasLineTerminator,
        AuditRecord? Record,
        string? Error);

    private static (List<ParsedLineInfo> Lines, string? StructuralError) ScanLines(Stream stream)
    {
        var lines = new List<ParsedLineInfo>();
        if (stream.Length == 0)
        {
            return (lines, null);
        }

        stream.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[stream.Length];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        int currentLineStart = 0;
        // Check for UTF-8 BOM (0xEF, 0xBB, 0xBF) at the very start of the file
        if (totalRead >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            currentLineStart = 3;
        }

        int lineIdx = 0;

        for (int i = currentLineStart; i < totalRead; i++)
        {
            if (buffer[i] == (byte)'\n')
            {
                int lineEndExclusive = i + 1;
                int contentEnd = i;
                if (contentEnd > currentLineStart && buffer[contentEnd - 1] == (byte)'\r')
                {
                    contentEnd--;
                }

                string lineText = Utf8NoBom.GetString(buffer, currentLineStart, contentEnd - currentLineStart);
                var (rec, err) = TryParseRecord(lineText);

                lines.Add(new ParsedLineInfo(
                    LineIndex: lineIdx++,
                    StartOffset: currentLineStart,
                    EndOffset: lineEndExclusive,
                    LineText: lineText,
                    HasLineTerminator: true,
                    Record: rec,
                    Error: err));

                currentLineStart = lineEndExclusive;
            }
        }

        if (currentLineStart < totalRead)
        {
            // Last line without newline terminator! (torn line / crash during write)
            string lineText = Utf8NoBom.GetString(buffer, currentLineStart, totalRead - currentLineStart);
            var (rec, err) = TryParseRecord(lineText);
            string lineError = err ?? "Line lacks terminating newline character (torn or incomplete write).";

            lines.Add(new ParsedLineInfo(
                LineIndex: lineIdx++,
                StartOffset: currentLineStart,
                EndOffset: totalRead,
                LineText: lineText,
                HasLineTerminator: false,
                Record: rec,
                Error: lineError));
        }

        return (lines, null);
    }

    private static (AuditRecord? Record, string? Error) TryParseRecord(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, "Encountered empty or whitespace-only line in audit log.");
        }

        try
        {
            var record = JsonSerializer.Deserialize<AuditRecord>(text);
            if (record == null)
            {
                return (null, "Deserialized null record.");
            }
            return (record, null);
        }
        catch (Exception ex)
        {
            return (null, $"Invalid JSON record: {ex.Message}");
        }
    }

    private static (bool IsValid, List<AuditRecord> Records, long? TamperedSeq, string? Error) ValidateChainFromLines(List<ParsedLineInfo> lines)
    {
        var records = new List<AuditRecord>();
        string expectedPrevHash = InitialPrevHash;
        long expectedSeq = 1;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (!line.HasLineTerminator)
            {
                return (false, records, expectedSeq,
                    $"Audit log terminated prematurely without newline at line {line.LineIndex + 1} (sequence {expectedSeq}): {line.Error ?? "Torn line"}");
            }

            if (line.Error != null || line.Record == null)
            {
                return (false, records, expectedSeq,
                    $"Audit log corrupted at line {line.LineIndex + 1} (expected sequence {expectedSeq}): {line.Error}");
            }

            var record = line.Record;

            if (record.SequenceNumber != expectedSeq)
            {
                return (false, records, record.SequenceNumber,
                    $"Sequence number gap or mismatch at line {line.LineIndex + 1}: expected {expectedSeq}, got {record.SequenceNumber}");
            }

            if (!string.Equals(record.PrevHash, expectedPrevHash, StringComparison.OrdinalIgnoreCase))
            {
                return (false, records, record.SequenceNumber,
                    $"PrevHash mismatch at record {record.SequenceNumber} (line {line.LineIndex + 1}): expected {expectedPrevHash}, got {record.PrevHash}");
            }

            var calculatedHash = ComputeRecordHash(
                record.SequenceNumber,
                record.Timestamp,
                record.EventType,
                record.TargetId,
                record.OperatorId,
                record.Action,
                record.DetailsJson,
                record.PrevHash);

            if (!string.Equals(record.RecordHash, calculatedHash, StringComparison.OrdinalIgnoreCase))
            {
                return (false, records, record.SequenceNumber,
                    $"RecordHash tampered or invalid at record {record.SequenceNumber} (line {line.LineIndex + 1}): expected {calculatedHash}, got {record.RecordHash}");
            }

            expectedPrevHash = record.RecordHash;
            expectedSeq++;
            records.Add(record);
        }

        return (true, records, null, null);
    }

    public ValueTask<AuditRecord> AppendAsync(
        string eventType,
        string targetId,
        string operatorId,
        string action,
        object details,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var detailsJson = details is string s ? s : JsonSerializer.Serialize(details, JsonOptions);
        var timestamp = DateTimeOffset.UtcNow;

        lock (_syncLock)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(
                _filePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            long nextSeq = 1;
            string prevHash = InitialPrevHash;

            if (stream.Length > 0)
            {
                var (lines, _) = ScanLines(stream);
                var (isValid, records, tamperedSeq, error) = ValidateChainFromLines(lines);
                if (!isValid)
                {
                    throw new AuditLogCorruptedException(
                        $"Cannot append to corrupted audit log (failing closed to preserve evidence). Sequence: {tamperedSeq}, Error: {error}",
                        tamperedSeq);
                }

                if (records.Count > 0)
                {
                    var last = records[^1];
                    nextSeq = last.SequenceNumber + 1;
                    prevHash = last.RecordHash;
                }
            }

            var recordHash = ComputeRecordHash(
                nextSeq,
                timestamp,
                eventType,
                targetId,
                operatorId,
                action,
                detailsJson,
                prevHash);

            var newRecord = new AuditRecord(
                nextSeq,
                timestamp,
                eventType,
                targetId,
                operatorId,
                action,
                detailsJson,
                prevHash,
                recordHash);

            stream.Seek(0, SeekOrigin.End);
            using var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 4096, leaveOpen: true);
            var lineToWrite = JsonSerializer.Serialize(newRecord, JsonOptions);
            writer.WriteLine(lineToWrite);
            writer.Flush();
            stream.Flush(true); // fsync to disk

            return ValueTask.FromResult(newRecord);
        }
    }

    public ValueTask<IReadOnlyList<AuditRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncLock)
        {
            if (!File.Exists(_filePath))
            {
                return ValueTask.FromResult<IReadOnlyList<AuditRecord>>(Array.Empty<AuditRecord>());
            }

            using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            var (lines, _) = ScanLines(stream);
            var (isValid, records, tamperedSeq, error) = ValidateChainFromLines(lines);

            if (!isValid)
            {
                throw new AuditLogCorruptedException(
                    $"Audit log is corrupted: {error}",
                    tamperedSeq);
            }

            return ValueTask.FromResult<IReadOnlyList<AuditRecord>>(records);
        }
    }

    public ValueTask<AuditVerificationResult> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncLock)
        {
            if (!File.Exists(_filePath))
            {
                return ValueTask.FromResult(new AuditVerificationResult(true, 0));
            }

            using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            if (stream.Length == 0)
            {
                return ValueTask.FromResult(new AuditVerificationResult(true, 0));
            }

            var (lines, _) = ScanLines(stream);
            var (isValid, records, tamperedSeq, error) = ValidateChainFromLines(lines);

            if (!isValid)
            {
                return ValueTask.FromResult(new AuditVerificationResult(
                    false,
                    records.Count,
                    tamperedSeq,
                    error));
            }

            return ValueTask.FromResult(new AuditVerificationResult(true, records.Count));
        }
    }

    /// <summary>
    /// Explicitly repairs a torn or corrupted tail if and only if all preceding records up to the torn tail are valid.
    /// Does NOT automatically repair corrupted records in the middle of the log or when the log is non-recoverable.
    /// Preserves evidence by returning information on dropped bytes.
    /// </summary>
    public TornTailRepairResult RepairTornTail()
    {
        lock (_syncLock)
        {
            if (!File.Exists(_filePath))
            {
                return new TornTailRepairResult(false, 0, null, 0, "Audit log file does not exist.");
            }

            using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            if (stream.Length == 0)
            {
                return new TornTailRepairResult(false, 0, null, 0, "Audit log file is empty.");
            }

            var (lines, _) = ScanLines(stream);
            if (lines.Count == 0)
            {
                return new TornTailRepairResult(false, 0, null, 0, "No lines found.");
            }

            // Check if the entire log is already valid
            var (isAlreadyValid, validRecords, _, _) = ValidateChainFromLines(lines);
            if (isAlreadyValid)
            {
                return new TornTailRepairResult(false, 0, null, validRecords.Count, "Audit log is already valid. No torn tail to repair.");
            }

            // Check if all lines except the last one form a valid chain
            var prefixLines = lines.Take(lines.Count - 1).ToList();
            var (isPrefixValid, prefixRecords, prefixTamperedSeq, prefixError) = ValidateChainFromLines(prefixLines);

            if (!isPrefixValid && prefixLines.Count > 0)
            {
                return new TornTailRepairResult(
                    false,
                    0,
                    null,
                    prefixRecords.Count,
                    $"Cannot repair torn tail: Corruption detected earlier in the log (sequence {prefixTamperedSeq}): {prefixError}");
            }

            var tornLine = lines[^1];
            long truncateOffset = tornLine.StartOffset;
            long droppedBytesCount = stream.Length - truncateOffset;
            string droppedContent = tornLine.LineText;

            stream.SetLength(truncateOffset);
            stream.Flush(true);

            return new TornTailRepairResult(
                true,
                droppedBytesCount,
                droppedContent,
                prefixRecords.Count);
        }
    }
}

