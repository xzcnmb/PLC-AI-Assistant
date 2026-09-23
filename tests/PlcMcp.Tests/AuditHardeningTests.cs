using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Runtime.Governance;

namespace PlcMcp.Tests;

public class AuditHardeningTests
{
    [Fact]
    public async Task AppendAsync_VerifyRestart_CanReopenFileAndContinueChain()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audit_restart_{Guid.NewGuid():N}.jsonl");
        try
        {
            // Instance 1 writes records 1 and 2
            var log1 = new FileAuditLog(tempFile);
            var r1 = await log1.AppendAsync("event1", "target-1", "user-1", "act1", new { a = 1 });
            var r2 = await log1.AppendAsync("event2", "target-1", "user-1", "act2", new { a = 2 });

            Assert.Equal(1, r1.SequenceNumber);
            Assert.Equal(2, r2.SequenceNumber);

            // Instance 2 simulates system restart / process reboot opening existing file
            var log2 = new FileAuditLog(tempFile);
            var r3 = await log2.AppendAsync("event3", "target-1", "user-1", "act3", new { a = 3 });

            Assert.Equal(3, r3.SequenceNumber);
            Assert.Equal(r2.RecordHash, r3.PrevHash);

            var verification = await log2.VerifyChainAsync();
            Assert.True(verification.IsValid);
            Assert.Equal(3, verification.TotalRecords);

            var records = await log2.ReadAllAsync();
            Assert.Equal(3, records.Count);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AppendAsync_WhenTailIsTornWithoutNewline_FailsClosedWithAuditLogCorruptedException()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audit_torn_tail_{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new FileAuditLog(tempFile);
            await log.AppendAsync("event1", "target-1", "user-1", "act1", new { a = 1 });

            // Simulate power loss / torn write mid-line at the end of the file (no trailing newline)
            var partialRecordBytes = Encoding.UTF8.GetBytes("{\"SequenceNumber\":2,\"Timestamp\":\"2026-09-23T12:00:00Z\",\"Event");
            await using (var fs = new FileStream(tempFile, FileMode.Append, FileAccess.Write))
            {
                await fs.WriteAsync(partialRecordBytes);
                await fs.FlushAsync();
            }

            // Must fail closed: cannot append to corrupted file
            var ex = await Assert.ThrowsAsync<AuditLogCorruptedException>(async () =>
            {
                await log.AppendAsync("event3", "target-1", "user-1", "act3", new { a = 3 });
            });
            Assert.Contains("Cannot append to corrupted audit log", ex.Message);
            Assert.Equal(2, ex.SequenceNumber);

            // ReadAllAsync must also fail closed without throwing a raw JsonException
            var readEx = await Assert.ThrowsAsync<AuditLogCorruptedException>(async () =>
            {
                await log.ReadAllAsync();
            });
            Assert.Contains("Audit log is corrupted", readEx.Message);

            // VerifyChainAsync should report failure with details
            var verify = await log.VerifyChainAsync();
            Assert.False(verify.IsValid);
            Assert.Equal(1, verify.TotalRecords);
            Assert.Equal(2, verify.TamperedSequenceNumber);
            Assert.Contains("terminated prematurely without newline", verify.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AppendAsync_WhenMiddleRecordIsCorrupted_FailsClosedAndPreservesEvidence()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audit_bad_middle_{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new FileAuditLog(tempFile);
            await log.AppendAsync("event1", "target-1", "user-1", "act1", new { a = 1 });
            await log.AppendAsync("event2", "target-1", "user-1", "act2", new { a = 2 });
            await log.AppendAsync("event3", "target-1", "user-1", "act3", new { a = 3 });

            // Corrupt line 2 in place (bad json content, but keep newline)
            var lines = await File.ReadAllLinesAsync(tempFile);
            var originalLine2 = lines[1];
            lines[1] = "{\"CorruptedJsonData\":true"; // invalid JSON line
            await File.WriteAllLinesAsync(tempFile, lines);

            // AppendAsync must fail closed
            var ex = await Assert.ThrowsAsync<AuditLogCorruptedException>(async () =>
            {
                await log.AppendAsync("event4", "target-1", "user-1", "act4", new { a = 4 });
            });
            Assert.Contains("Cannot append to corrupted audit log", ex.Message);
            Assert.Equal(2, ex.SequenceNumber);

            // ReadAllAsync must fail closed with AuditLogCorruptedException
            var readEx = await Assert.ThrowsAsync<AuditLogCorruptedException>(async () =>
            {
                await log.ReadAllAsync();
            });
            Assert.Contains("Audit log is corrupted", readEx.Message);
            Assert.Equal(2, readEx.SequenceNumber);

            // Verification must identify the error at line 2 / sequence 2
            var verify = await log.VerifyChainAsync();
            Assert.False(verify.IsValid);
            Assert.Equal(1, verify.TotalRecords);
            Assert.Equal(2, verify.TamperedSequenceNumber);

            // Ensure file still contains evidence and was not silently truncated
            var currentLines = await File.ReadAllLinesAsync(tempFile);
            Assert.Equal(3, currentLines.Length);
            Assert.Equal("{\"CorruptedJsonData\":true", currentLines[1]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyChainAsync_DetectsBlankOrEmptyLinesInMiddle()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audit_blank_lines_{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new FileAuditLog(tempFile);
            await log.AppendAsync("event1", "target-1", "user-1", "act1", new { a = 1 });
            await log.AppendAsync("event2", "target-1", "user-1", "act2", new { a = 2 });

            // Insert an empty line between record 1 and 2
            var lines = await File.ReadAllLinesAsync(tempFile);
            var tampered = new[] { lines[0], "   ", lines[1] };
            await File.WriteAllLinesAsync(tempFile, tampered);

            var verify = await log.VerifyChainAsync();
            Assert.False(verify.IsValid);
            Assert.Equal(1, verify.TotalRecords);
            Assert.Equal(2, verify.TamperedSequenceNumber);
            Assert.Contains("empty or whitespace-only", verify.ErrorMessage);

            // AppendAsync should reject appending to this
            await Assert.ThrowsAsync<AuditLogCorruptedException>(async () =>
            {
                await log.AppendAsync("event3", "target-1", "user-1", "act3", new { a = 3 });
            });
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RepairTornTail_WhenOnlyTailIsTorn_RepairsAndAllowsSubsequentAppends()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audit_repair_tail_{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new FileAuditLog(tempFile);
            var r1 = await log.AppendAsync("event1", "target-1", "user-1", "act1", new { a = 1 });
            var r2 = await log.AppendAsync("event2", "target-1", "user-1", "act2", new { a = 2 });

            // Simulate torn tail at the end
            var tornBytes = Encoding.UTF8.GetBytes("{\"SequenceNumber\":3,\"Action\":\"Incomplete");
            await using (var fs = new FileStream(tempFile, FileMode.Append, FileAccess.Write))
            {
                await fs.WriteAsync(tornBytes);
                await fs.FlushAsync();
            }

            // Before repair: append fails closed
            await Assert.ThrowsAsync<AuditLogCorruptedException>(async () =>
            {
                await log.AppendAsync("event_x", "target-1", "user-1", "act_x", new { a = 99 });
            });

            // Perform explicit repair
            var repairResult = log.RepairTornTail();
            Assert.True(repairResult.Repaired);
            Assert.Equal(tornBytes.Length, repairResult.DroppedBytes);
            Assert.Equal("{\"SequenceNumber\":3,\"Action\":\"Incomplete", repairResult.DroppedContent);
            Assert.Equal(2, repairResult.RemainingRecords);

            // After repair: VerifyChainAsync is valid
            var verify = await log.VerifyChainAsync();
            Assert.True(verify.IsValid);
            Assert.Equal(2, verify.TotalRecords);

            // Subsequent AppendAsync succeeds and continues from sequence 3
            var r3 = await log.AppendAsync("event3", "target-1", "user-1", "act3", new { a = 3 });
            Assert.Equal(3, r3.SequenceNumber);
            Assert.Equal(r2.RecordHash, r3.PrevHash);

            var verifyAfter = await log.VerifyChainAsync();
            Assert.True(verifyAfter.IsValid);
            Assert.Equal(3, verifyAfter.TotalRecords);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RepairTornTail_WhenCorruptionIsInMiddle_RefusesToRepairToPreventSilentDataLoss()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audit_repair_middle_{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new FileAuditLog(tempFile);
            await log.AppendAsync("event1", "target-1", "user-1", "act1", new { a = 1 });
            await log.AppendAsync("event2", "target-1", "user-1", "act2", new { a = 2 });
            await log.AppendAsync("event3", "target-1", "user-1", "act3", new { a = 3 });

            // Tamper line 2 in middle
            var lines = await File.ReadAllLinesAsync(tempFile);
            lines[1] = "{\"BadRecord\":true}";
            await File.WriteAllLinesAsync(tempFile, lines);

            // Append torn tail at end as well
            await File.AppendAllTextAsync(tempFile, "{\"PartialTail");

            // RepairTornTail must refuse to repair because middle is corrupted
            var repairResult = log.RepairTornTail();
            Assert.False(repairResult.Repaired);
            Assert.Contains("Corruption detected earlier in the log", repairResult.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ConcurrentAppends_MutualExclusion_EnsuresSequentialIntegrityWithoutDataLoss()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audit_concurrent_{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new FileAuditLog(tempFile);
            const int concurrency = 20;

            var tasks = Enumerable.Range(1, concurrency).Select(i => Task.Run(async () =>
            {
                await log.AppendAsync("concurrent_event", $"target-{i}", $"user-{i}", "test_action", new { workerId = i });
            })).ToArray();

            await Task.WhenAll(tasks);

            var verifyResult = await log.VerifyChainAsync();
            Assert.True(verifyResult.IsValid, verifyResult.ErrorMessage);
            Assert.Equal(concurrency, verifyResult.TotalRecords);

            var allRecords = await log.ReadAllAsync();
            Assert.Equal(concurrency, allRecords.Count);

            // Check sequences are strictly 1..N with no duplicates or gaps
            var seqs = allRecords.Select(r => r.SequenceNumber).OrderBy(x => x).ToList();
            for (int i = 0; i < concurrency; i++)
            {
                Assert.Equal(i + 1, seqs[i]);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
