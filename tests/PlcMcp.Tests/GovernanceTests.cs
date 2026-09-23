using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Runtime.Governance;

namespace PlcMcp.Tests;

public class GovernanceTests
{
    [Fact]
    public void CapabilityReport_ContractProperties_CanBeSerializedAndDeserialized()
    {
        var report = new CapabilityReport(
            TargetId: "s7-1500-cell-1",
            SchemaVersion: "1.0.0",
            GeneratedAt: DateTimeOffset.UtcNow,
            Capabilities:
            [
                new CapabilityDescriptor("read_tag", CapabilityStatus.Supported, "Standard cyclic read"),
                new CapabilityDescriptor("download_program", CapabilityStatus.Supported, "Engineering port download")
            ],
            SupportedTransports: [TransportKind.S7Comm, TransportKind.OpcUa],
            WriteAtomicity: AtomicityScope.BatchAtomic,
            RollbackSupport: RollbackSupportKind.AutomaticSnapshot,
            RequiresApprovalForPhysical: true,
            Notes: "Verified under production cell governor."
        );

        var json = JsonSerializer.Serialize(report);
        var deserialized = JsonSerializer.Deserialize<CapabilityReport>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("s7-1500-cell-1", deserialized.TargetId);
        Assert.Equal(AtomicityScope.BatchAtomic, deserialized.WriteAtomicity);
        Assert.Equal(RollbackSupportKind.AutomaticSnapshot, deserialized.RollbackSupport);
        Assert.True(deserialized.RequiresApprovalForPhysical);
        Assert.Equal(2, deserialized.Capabilities.Count);
    }

    [Fact]
    public async Task AuditLog_AppendAndVerify_ValidHashChainSucceeds()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audit_test_{Guid.NewGuid():N}.jsonl");
        try
        {
            var auditLog = new FileAuditLog(tempFile);

            var r1 = await auditLog.AppendAsync("auth", "target-1", "user-alice", "login", new { ip = "192.168.0.10" });
            var r2 = await auditLog.AppendAsync("job_start", "target-1", "user-alice", "download", new { hash = "abc123" });
            var r3 = await auditLog.AppendAsync("job_end", "target-1", "user-alice", "download", new { status = "succeeded" });

            Assert.Equal(1, r1.SequenceNumber);
            Assert.Equal(2, r2.SequenceNumber);
            Assert.Equal(3, r3.SequenceNumber);
            Assert.Equal("0000000000000000000000000000000000000000000000000000000000000000", r1.PrevHash);
            Assert.Equal(r1.RecordHash, r2.PrevHash);
            Assert.Equal(r2.RecordHash, r3.PrevHash);

            var verifyResult = await auditLog.VerifyChainAsync();
            Assert.True(verifyResult.IsValid);
            Assert.Equal(3, verifyResult.TotalRecords);
            Assert.Null(verifyResult.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AuditLog_TamperingDetection_DetectsContentModificationAndReportsExplicitly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audit_test_{Guid.NewGuid():N}.jsonl");
        try
        {
            var auditLog = new FileAuditLog(tempFile);

            await auditLog.AppendAsync("step1", "target-1", "user-1", "action1", new { v = 1 });
            await auditLog.AppendAsync("step2", "target-1", "user-1", "action2", new { v = 2 });
            await auditLog.AppendAsync("step3", "target-1", "user-1", "action3", new { v = 3 });

            // Tamper line 2 in file
            var lines = await File.ReadAllLinesAsync(tempFile);
            var tamperedRecord = JsonSerializer.Deserialize<AuditRecord>(lines[1])!;
            var modified = tamperedRecord with { OperatorId = "malicious-hacker" };
            lines[1] = JsonSerializer.Serialize(modified);
            await File.WriteAllLinesAsync(tempFile, lines);

            // Verify tampering detection
            var verifyResult = await auditLog.VerifyChainAsync();
            Assert.False(verifyResult.IsValid);
            Assert.Equal(2, verifyResult.TamperedSequenceNumber);
            Assert.Contains("RecordHash tampered or invalid", verifyResult.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AuditLog_TamperingDetection_DetectsDeletionOrTruncation()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audit_test_{Guid.NewGuid():N}.jsonl");
        try
        {
            var auditLog = new FileAuditLog(tempFile);

            await auditLog.AppendAsync("step1", "target-1", "user-1", "action1", new { v = 1 });
            await auditLog.AppendAsync("step2", "target-1", "user-1", "action2", new { v = 2 });
            await auditLog.AppendAsync("step3", "target-1", "user-1", "action3", new { v = 3 });

            // Delete middle line (sequence 2)
            var lines = await File.ReadAllLinesAsync(tempFile);
            var tamperedLines = new[] { lines[0], lines[2] };
            await File.WriteAllLinesAsync(tempFile, tamperedLines);

            var verifyResult = await auditLog.VerifyChainAsync();
            Assert.False(verifyResult.IsValid);
            Assert.Equal(3, verifyResult.TamperedSequenceNumber);
            Assert.Contains("Sequence number gap or mismatch", verifyResult.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task JobStateMachine_LifecycleTransitions_WorkAsExpected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jobs_{Guid.NewGuid():N}");
        try
        {
            var sm = new FileJobStateMachine(tempDir);

            var job = await sm.CreateJobAsync("plc-line-1", EngineeringActionKind.DownloadProgram, "proj_hash_abc", "appr_123");
            Assert.Equal(JobState.Pending, job.State);
            Assert.Null(job.StartedAt);
            Assert.Null(job.CompletedAt);

            // Pending -> Running
            var running = await sm.TransitionAsync(job.JobId, JobState.Running, log: "Starting compile & transfer");
            Assert.Equal(JobState.Running, running.State);
            Assert.NotNull(running.StartedAt);
            Assert.Null(running.CompletedAt);

            // Running -> Succeeded
            var completed = await sm.TransitionAsync(job.JobId, JobState.Succeeded, log: "Download completed successfully");
            Assert.Equal(JobState.Succeeded, completed.State);
            Assert.NotNull(completed.CompletedAt);
            Assert.Contains("Download completed successfully", completed.Log);

            // Succeeded is terminal; cannot transition to Running or Failed
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await sm.TransitionAsync(job.JobId, JobState.Running);
            });
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task JobStateMachine_CrashRecovery_QuarantinesRunningJobsOnRestart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jobs_crash_{Guid.NewGuid():N}");
        try
        {
            var sm1 = new FileJobStateMachine(tempDir);

            // Job 1 finished before crash
            var j1 = await sm1.CreateJobAsync("plc-1", EngineeringActionKind.DownloadProgram, "hash1");
            await sm1.TransitionAsync(j1.JobId, JobState.Running);
            await sm1.TransitionAsync(j1.JobId, JobState.Succeeded);

            // Job 2 was pending
            var j2 = await sm1.CreateJobAsync("plc-1", EngineeringActionKind.DownloadProgram, "hash2");

            // Job 3 was running mid-flight when crash happened
            var j3 = await sm1.CreateJobAsync("plc-1", EngineeringActionKind.DownloadProgram, "hash3");
            await sm1.TransitionAsync(j3.JobId, JobState.Running, log: "Writing block 4/10");

            // Simulate process crash and reboot by instantiating new sm2 on same storage
            var sm2 = new FileJobStateMachine(tempDir);
            var recovered = await sm2.RecoverFromCrashAsync();

            Assert.Single(recovered);
            Assert.Equal(j3.JobId, recovered[0].JobId);
            Assert.Equal(JobState.Quarantined, recovered[0].State);
            Assert.Contains("interrupted mid-execution", recovered[0].Error);

            // Reload jobs to ensure persisted state
            var reloadedJ1 = await sm2.GetJobAsync(j1.JobId);
            var reloadedJ2 = await sm2.GetJobAsync(j2.JobId);
            var reloadedJ3 = await sm2.GetJobAsync(j3.JobId);

            Assert.Equal(JobState.Succeeded, reloadedJ1!.State);
            Assert.Equal(JobState.Pending, reloadedJ2!.State);
            Assert.Equal(JobState.Quarantined, reloadedJ3!.State);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TargetLease_ExclusiveCrossHandleAcquisition_BlocksSecondLease()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"leases_{Guid.NewGuid():N}");
        try
        {
            var leaseService1 = new FileTargetLeaseService(tempDir);
            var leaseService2 = new FileTargetLeaseService(tempDir);

            var targetId = "plc-critical-cell";

            var lease1 = await leaseService1.TryAcquireAsync(targetId, TimeSpan.FromMilliseconds(100));
            Assert.NotNull(lease1);
            Assert.True(lease1.IsActive);

            // Second lease attempt on same target must fail while lease1 is held
            var lease2 = await leaseService2.TryAcquireAsync(targetId, TimeSpan.FromMilliseconds(100));
            Assert.Null(lease2);

            // Releasing lease 1 allows lease 2 to acquire
            await lease1.DisposeAsync();
            Assert.False(lease1.IsActive);

            var lease3 = await leaseService2.TryAcquireAsync(targetId, TimeSpan.FromMilliseconds(500));
            Assert.NotNull(lease3);
            Assert.True(lease3.IsActive);

            await lease3.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApprovalBinding_SelfMintedToken_IsRejected()
    {
        var key = Encoding.UTF8.GetBytes("super-secret-hmac-key-for-test-32b");
        var validator = new HmacExternalSignatureValidator("TestCorpIdP", key);
        var verifier = new ApprovalBindingVerifier(validator, allowHmacForIntegrityOnly: true);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-001",
            TargetId: "plc-main",
            ActionKind: "DownloadProgram",
            ProjectHash: "sha256_proj_abc",
            ApprovedBy: "self-minted", // Disallowed self-minted identity
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(5),
            Signature: "any-sig",
            Metadata: new Dictionary<string, string> { ["source"] = "test" }
        );

        var result = verifier.Verify(binding, "plc-main", "DownloadProgram", "sha256_proj_abc", now);

        Assert.False(result.IsValid);
        Assert.Contains("Self-minted or local machine token", result.ErrorReason);
    }

    [Fact]
    public void ApprovalBinding_NoExternalIdPConfigured_FailsClosed()
    {
        // No validator passed to constructor
        var verifier = new ApprovalBindingVerifier(null);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-002",
            TargetId: "plc-main",
            ActionKind: "DownloadProgram",
            ProjectHash: "sha256_proj_abc",
            ApprovedBy: "alice@corporate-idp.example.com",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(5),
            Signature: "some-signature",
            Metadata: new Dictionary<string, string> { ["source"] = "test" }
        );

        var result = verifier.Verify(binding, "plc-main", "DownloadProgram", "sha256_proj_abc", now);

        Assert.False(result.IsValid);
        Assert.False(result.ExternalIdentityProviderConfigured);
        Assert.Contains("No external identity provider/signature validator configured; failing closed", result.ErrorReason);
    }

    [Fact]
    public void ApprovalBinding_TargetActionOrHashMismatch_FailsClosed()
    {
        var key = Encoding.UTF8.GetBytes("super-secret-hmac-key-for-test-32b");
        var validator = new HmacExternalSignatureValidator("TestCorpIdP", key);
        var verifier = new ApprovalBindingVerifier(validator, allowHmacForIntegrityOnly: true);

        var now = DateTimeOffset.UtcNow;
        var baseBinding = new ApprovalBinding(
            ApprovalId: "appr-003",
            TargetId: "plc-target-A",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash_project_v1",
            ApprovedBy: "alice@corporate-idp.example.com",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "",
            Metadata: new Dictionary<string, string> { ["source"] = "test-corp-idp" }
        );
        var signature = HmacExternalSignatureValidator.Sign(baseBinding, key);
        var binding = baseBinding with { Signature = signature };

        // 1. Correct binding passes
        var okResult = verifier.Verify(binding, "plc-target-A", "DownloadProgram", "hash_project_v1", now);
        Assert.True(okResult.IsValid);
        Assert.True(okResult.IsIntegrityOnly);
        Assert.False(okResult.IsPhysicalApprovalValid);

        // 2. Target mismatch
        var targetMismatch = verifier.Verify(binding, "plc-target-B", "DownloadProgram", "hash_project_v1", now);
        Assert.False(targetMismatch.IsValid);
        Assert.Contains("Approval target mismatch", targetMismatch.ErrorReason);

        // 3. Action mismatch
        var actionMismatch = verifier.Verify(binding, "plc-target-A", "FlashFirmware", "hash_project_v1", now);
        Assert.False(actionMismatch.IsValid);
        Assert.Contains("Approval action mismatch", actionMismatch.ErrorReason);

        // 4. Project hash mismatch
        var hashMismatch = verifier.Verify(binding, "plc-target-A", "DownloadProgram", "hash_project_v2_tampered", now);
        Assert.False(hashMismatch.IsValid);
        Assert.Contains("Approval project hash mismatch", hashMismatch.ErrorReason);

        // 5. Expired token
        var expiredResult = verifier.Verify(binding, "plc-target-A", "DownloadProgram", "hash_project_v1", now.AddMinutes(15));
        Assert.False(expiredResult.IsValid);
        Assert.Contains("Approval token has expired", expiredResult.ErrorReason);

        // 6. Bad signature
        var tamperedSigBinding = binding with { Signature = "deadbeef" };
        var badSigResult = verifier.Verify(tamperedSigBinding, "plc-target-A", "DownloadProgram", "hash_project_v1", now);
        Assert.False(badSigResult.IsValid);
        Assert.Contains("Cryptographic signature verification failed", badSigResult.ErrorReason);
    }

    [Fact]
    public void ApprovalVerifier_HmacWithoutIntegrityFlag_FailsClosed()
    {
        var key = Encoding.UTF8.GetBytes("super-secret-hmac-key-for-test-32b");
        var validator = new HmacExternalSignatureValidator("TestCorpIdP", key);
        // allowHmacForIntegrityOnly is false by default
        var verifier = new ApprovalBindingVerifier(validator, allowHmacForIntegrityOnly: false);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-hmac-strict-01",
            TargetId: "plc-target-A",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash_project_v1",
            ApprovedBy: "alice@corporate-idp.example.com",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "",
            Metadata: new Dictionary<string, string> { ["source"] = "test" }
        );
        var signature = HmacExternalSignatureValidator.Sign(binding, key);
        var signedBinding = binding with { Signature = signature };

        var result = verifier.Verify(signedBinding, "plc-target-A", "DownloadProgram", "hash_project_v1", now);
        Assert.False(result.IsValid);
        Assert.Contains("HMAC signature cannot serve as human approval", result.ErrorReason);
    }

    [Fact]
    public void ApprovalVerifier_Hmac_AlwaysRejectedForHumanApproval()
    {
        var key = Encoding.UTF8.GetBytes("super-secret-hmac-key-for-test-32b");
        var validator = new HmacExternalSignatureValidator("TestCorpIdP", key);
        var attestationValidator = new ExternalHumanAttestationValidator("CorporateIdP", _ => (true, null));

        // Even with allowHmacForIntegrityOnly: true, pairing HMAC with human attestation MUST fail closed
        var verifier = new ApprovalBindingVerifier(
            externalValidator: validator,
            attestationValidator: attestationValidator,
            allowHmacForIntegrityOnly: true);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-hmac-reject-human",
            TargetId: "plc-target-A",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash_project_v1",
            ApprovedBy: "alice@corporate-idp.example.com",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "",
            Metadata: new Dictionary<string, string>
            {
                ["idp_token"] = "fake-token",
                ["is_human"] = "true"
            }
        );
        var signature = HmacExternalSignatureValidator.Sign(binding, key);
        var signedBinding = binding with { Signature = signature };

        var result = verifier.Verify(signedBinding, "plc-target-A", "DownloadProgram", "hash_project_v1", now);
        Assert.False(result.IsValid);
        Assert.Contains("HMAC signature cannot serve as human approval", result.ErrorReason);
    }

    [Fact]
    public async Task ApprovalVerifier_Verify_PrecheckOnly_And_VerifyAndConsume_EnforcesReplayConsumer()
    {
        using var rsa = RSA.Create(2048);
        var pubParams = rsa.ExportParameters(includePrivateParameters: false);
        using var pubRsa = RSA.Create();
        pubRsa.ImportParameters(pubParams);

        var publicKeyVerifier = new RsaPublicKeySignatureVerifier("rsa-key-01", pubRsa);

        // Fake in-memory test callback: validates fixed token, approver, issuer, claims without calling external services
        const string expectedToken = "test-token-okta-999";
        const string expectedApprover = "alice@corporate-idp.example.com";
        const string expectedIssuer = "https://fake-idp.example.internal";
        const string expectedClaim = "role=safety_officer";

        var attestationValidator = new ExternalHumanAttestationValidator("CorporateIdP", binding =>
        {
            if (binding.Metadata == null) return (false, "Missing metadata");
            if (!binding.Metadata.TryGetValue("idp_token", out var t) || t != expectedToken) return (false, "Bad token");
            if (!string.Equals(binding.ApprovedBy, expectedApprover, StringComparison.OrdinalIgnoreCase)) return (false, "Bad approver");
            if (!binding.Metadata.TryGetValue("issuer", out var iss) || iss != expectedIssuer) return (false, "Bad issuer");
            if (!binding.Metadata.TryGetValue("claims", out var c) || !c.Contains(expectedClaim, StringComparison.Ordinal)) return (false, "Bad claims");
            return (true, null);
        });

        // 1. Without replayConsumer: Verify() dry-run succeeds, but VerifyAndConsumeAsync() fails closed
        var verifierWithoutConsumer = new ApprovalBindingVerifier(
            publicKeyVerifier: publicKeyVerifier,
            attestationValidator: attestationValidator,
            replayConsumer: null);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-flow-01",
            TargetId: "plc-main",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash123",
            ApprovedBy: expectedApprover,
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "",
            Metadata: new Dictionary<string, string>
            {
                ["idp_token"] = expectedToken,
                ["is_human"] = "true",
                ["issuer"] = expectedIssuer,
                ["claims"] = expectedClaim
            }
        );

        var payloadBytes = Encoding.UTF8.GetBytes(ApprovalBindingVerifier.ComputeCanonicalBindingPayload(binding));
        var signatureBytes = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signedBinding = binding with { Signature = Convert.ToHexString(signatureBytes) };

        var precheck = verifierWithoutConsumer.Verify(signedBinding, "plc-main", "DownloadProgram", "hash123", now);
        Assert.True(precheck.IsValid);
        Assert.False(precheck.IsPhysicalApprovalValid, "Verify() is dry-run only; must never authorize physical execution.");

        var failConsume = await verifierWithoutConsumer.VerifyAndConsumeAsync(signedBinding, "plc-main", "DownloadProgram", "hash123", now);
        Assert.False(failConsume.IsValid);
        Assert.Contains("Approval replay consumer is not configured", failConsume.ErrorReason);
        Assert.False(failConsume.IsPhysicalApprovalValid);

        // 2. With replayConsumer: VerifyAndConsumeAsync() succeeds and authorizes physical approval
        var replayConsumer = new InMemoryApprovalReplayConsumer();
        var verifierWithConsumer = new ApprovalBindingVerifier(
            publicKeyVerifier: publicKeyVerifier,
            attestationValidator: attestationValidator,
            replayConsumer: replayConsumer);

        var consumeOk = await verifierWithConsumer.VerifyAndConsumeAsync(signedBinding, "plc-main", "DownloadProgram", "hash123", now);
        Assert.True(consumeOk.IsValid);
        Assert.True(consumeOk.IsPhysicalApprovalValid);

        // 3. Replay detection rejects subsequent consume
        var consumeReplay = await verifierWithConsumer.VerifyAndConsumeAsync(signedBinding, "plc-main", "DownloadProgram", "hash123", now);
        Assert.False(consumeReplay.IsValid);
        Assert.Contains("already been consumed or replay was detected", consumeReplay.ErrorReason);
    }
}
