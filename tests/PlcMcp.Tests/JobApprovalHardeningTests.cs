using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Runtime.Governance;

namespace PlcMcp.Tests;

public class JobApprovalHardeningTests
{
    [Fact]
    public void JobId_FormatValidation_EnforcesHexLower32()
    {
        // Valid: job_<32 lowercase hex characters> (36 total chars)
        var validId = $"job_{Guid.NewGuid():N}".ToLowerInvariant();
        Assert.True(FileJobStateMachine.IsValidJobId(validId));
        FileJobStateMachine.ValidateJobId(validId);

        // Invalid: empty, null, whitespace
        Assert.False(FileJobStateMachine.IsValidJobId(null));
        Assert.False(FileJobStateMachine.IsValidJobId(""));
        Assert.False(FileJobStateMachine.IsValidJobId("   "));
        Assert.Throws<ArgumentException>(() => FileJobStateMachine.ValidateJobId(""));

        // Invalid: wrong prefix
        Assert.False(FileJobStateMachine.IsValidJobId("task_0123456789abcdef0123456789abcdef"));
        Assert.Throws<ArgumentException>(() => FileJobStateMachine.ValidateJobId("task_0123456789abcdef0123456789abcdef"));

        // Invalid: uppercase characters
        var upperId = $"job_{Guid.NewGuid():N}".ToUpperInvariant();
        Assert.False(FileJobStateMachine.IsValidJobId(upperId));
        Assert.Throws<ArgumentException>(() => FileJobStateMachine.ValidateJobId(upperId));

        // Invalid: non-hex characters
        Assert.False(FileJobStateMachine.IsValidJobId("job_0123456789abcdef0123456789abcdeg"));
        Assert.Throws<ArgumentException>(() => FileJobStateMachine.ValidateJobId("job_0123456789abcdef0123456789abcdeg"));

        // Invalid: length too short or too long
        Assert.False(FileJobStateMachine.IsValidJobId("job_12345"));
        Assert.False(FileJobStateMachine.IsValidJobId("job_0123456789abcdef0123456789abcdef00"));
        Assert.Throws<ArgumentException>(() => FileJobStateMachine.ValidateJobId("job_12345"));
    }

    [Fact]
    public async Task JobStateMachine_PendingToFailed_IsValidTransition()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"job_pending_failed_{Guid.NewGuid():N}");
        try
        {
            var sm = new FileJobStateMachine(tempDir);
            var job = await sm.CreateJobAsync("target-plc-1", EngineeringActionKind.DownloadProgram, "hash_v1");

            Assert.Equal(JobState.Pending, job.State);
            Assert.Null(job.CompletedAt);

            // Direct transition Pending -> Failed
            var failedJob = await sm.TransitionAsync(job.JobId, JobState.Failed, error: "Pre-execution precheck failed");

            Assert.Equal(JobState.Failed, failedJob.State);
            Assert.NotNull(failedJob.CompletedAt);
            Assert.Equal("Pre-execution precheck failed", failedJob.Error);

            // Verify reloaded state
            var reloaded = await sm.GetJobAsync(job.JobId);
            Assert.NotNull(reloaded);
            Assert.Equal(JobState.Failed, reloaded.State);
            Assert.Equal("Pre-execution precheck failed", reloaded.Error);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task JobStateMachine_SaveCheck_EnforcesRecordJobIdMatchesCaller()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"job_savecheck_{Guid.NewGuid():N}");
        try
        {
            var sm = new FileJobStateMachine(tempDir);
            var job1 = await sm.CreateJobAsync("target-plc-1", EngineeringActionKind.Compile, "hash_1");
            var job2Id = $"job_{Guid.NewGuid():N}";

            // Tamper disk file of job1 to contain job2's JobId
            var job1Path = Path.Combine(tempDir, $"{job1.JobId}.json");
            var tamperedJob = job1 with { JobId = job2Id };
            await File.WriteAllTextAsync(job1Path, JsonSerializer.Serialize(tamperedJob));

            // Attempting to transition job1 should detect that persistent record has mismatched JobId
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await sm.TransitionAsync(job1.JobId, JobState.Running);
            });

            Assert.Contains("Persistent record JobId mismatch", ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task JobStateMachine_CrossInstanceConcurrency_PerStoreLockingEnsuresIntegrity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"job_concurrency_{Guid.NewGuid():N}");
        try
        {
            const int instanceCount = 4;
            const int jobsPerInstance = 5;

            var tasks = Enumerable.Range(0, instanceCount).Select(async instanceIndex =>
            {
                var localSm = new FileJobStateMachine(tempDir);
                for (int i = 0; i < jobsPerInstance; i++)
                {
                    var created = await localSm.CreateJobAsync($"target-{instanceIndex}", EngineeringActionKind.DownloadProgram, $"hash_{i}");
                    var running = await localSm.TransitionAsync(created.JobId, JobState.Running, log: $"Instance {instanceIndex} running step {i}");
                    await localSm.TransitionAsync(running.JobId, JobState.Succeeded, log: "Finished");
                }
            });

            await Task.WhenAll(tasks);

            var verificationSm = new FileJobStateMachine(tempDir);
            var files = Directory.GetFiles(tempDir, "job_*.json");
            Assert.Equal(instanceCount * jobsPerInstance, files.Length);

            foreach (var file in files)
            {
                var jobId = Path.GetFileNameWithoutExtension(file);
                var job = await verificationSm.GetJobAsync(jobId);
                Assert.NotNull(job);
                Assert.Equal(JobState.Succeeded, job.State);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void JobStateMachine_StartupOrphanDetectionAndCleanup_CollectsTemporaryFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"job_orphans_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Seed stray temporary files from interrupted writes
            var orphan1 = Path.Combine(tempDir, $"job_{Guid.NewGuid():N}.json.tmp_{Guid.NewGuid():N}");
            var orphan2 = Path.Combine(tempDir, $"job_{Guid.NewGuid():N}.json.tmp");
            File.WriteAllText(orphan1, "stray temp data 1");
            File.WriteAllText(orphan2, "stray temp data 2");

            // Instantiating SM on this directory should discover orphans on startup
            var sm = new FileJobStateMachine(tempDir);
            Assert.Contains(orphan1, sm.DiscoveredOrphans);
            Assert.Contains(orphan2, sm.DiscoveredOrphans);
            Assert.True(sm.DiscoveredOrphans.Count >= 2);

            // Clean orphans
            var cleaned = sm.CleanOrphanFiles();
            Assert.Contains(orphan1, cleaned);
            Assert.Contains(orphan2, cleaned);
            Assert.Empty(sm.DiscoveredOrphans);
            Assert.False(File.Exists(orphan1));
            Assert.False(File.Exists(orphan2));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task JobStateMachine_CrashRecovery_PerFileCorruptionResilience()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"job_corrupt_recovery_{Guid.NewGuid():N}");
        try
        {
            var sm = new FileJobStateMachine(tempDir);

            // Job 1: Succeeded before crash
            var j1 = await sm.CreateJobAsync("plc-1", EngineeringActionKind.DownloadProgram, "h1");
            await sm.TransitionAsync(j1.JobId, JobState.Running);
            await sm.TransitionAsync(j1.JobId, JobState.Succeeded);

            // Job 2: Running mid-flight
            var j2 = await sm.CreateJobAsync("plc-1", EngineeringActionKind.DownloadProgram, "h2");
            await sm.TransitionAsync(j2.JobId, JobState.Running);

            // Job 3: Corrupted file on disk (torn JSON)
            var j3Id = $"job_{Guid.NewGuid():N}";
            var j3Path = Path.Combine(tempDir, $"{j3Id}.json");
            await File.WriteAllTextAsync(j3Path, "{ \"JobId\": \"job_corrupted\", incomplete json payload...");

            // Job 4: Running mid-flight
            var j4 = await sm.CreateJobAsync("plc-1", EngineeringActionKind.DownloadProgram, "h4");
            await sm.TransitionAsync(j4.JobId, JobState.Running);

            // Simulate reboot and crash recovery with new instance
            var restartSm = new FileJobStateMachine(tempDir);
            var recovered = await restartSm.RecoverFromCrashAsync();

            // The corrupted file was quarantined, and BOTH running jobs j2 and j4 were also quarantined!
            Assert.Contains(recovered, r => r.JobId == j2.JobId && r.State == JobState.Quarantined);
            Assert.Contains(recovered, r => r.JobId == j4.JobId && r.State == JobState.Quarantined);

            var reloadedJ1 = await restartSm.GetJobAsync(j1.JobId);
            var reloadedJ2 = await restartSm.GetJobAsync(j2.JobId);
            var reloadedJ4 = await restartSm.GetJobAsync(j4.JobId);

            Assert.Equal(JobState.Succeeded, reloadedJ1!.State);
            Assert.Equal(JobState.Quarantined, reloadedJ2!.State);
            Assert.Equal(JobState.Quarantined, reloadedJ4!.State);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApprovalVerifier_HmacWithoutExplicitIntegrityFlag_FailsClosed()
    {
        var key = Encoding.UTF8.GetBytes("key-32-bytes-long-for-hmac-test!");
        var hmacValidator = new HmacExternalSignatureValidator("TestHmacIdP", key);

        // Default: allowHmacForIntegrityOnly is false
        var verifier = new ApprovalBindingVerifier(externalValidator: hmacValidator);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-hmac-01",
            TargetId: "plc-station-1",
            ActionKind: "DownloadProgram",
            ProjectHash: "sha256_proj_hash",
            ApprovedBy: "alice_engineer",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "",
            Metadata: new Dictionary<string, string> { ["source"] = "external-portal" }
        );
        var signature = HmacExternalSignatureValidator.Sign(binding, key);
        var signedBinding = binding with { Signature = signature };

        var result = verifier.Verify(signedBinding, "plc-station-1", "DownloadProgram", "sha256_proj_hash", now);

        Assert.False(result.IsValid);
        Assert.Contains("HMAC signature cannot serve as human approval", result.ErrorReason);
    }

    [Fact]
    public async Task ApprovalVerifier_RsaPublicKeyVerification_WithHumanAttestation_Succeeds()
    {
        using var rsa = RSA.Create(2048);
        using var pubRsa = CreatePublicOnlyRsa(rsa);
        var publicKeyVerifier = new RsaPublicKeySignatureVerifier("rsa-key-001", pubRsa);

        // Fake in-memory trusted IdP verification callback for test environment (no external network or real IdP called).
        // Validates fixed signed token, approver identity, expected issuer, and required claims.
        const string expectedToken = "fake-fixed-idp-token-12345";
        const string expectedApprover = "alice@corporate-idp.example.com";
        const string expectedIssuer = "https://fake-idp.example.internal";
        const string expectedClaim = "role=plc_safety_engineer";

        var attestationValidator = new ExternalHumanAttestationValidator(
            "CorporateOktaIdP",
            CreateFakeTrustedIdpCheck(expectedToken, expectedApprover, expectedIssuer, expectedClaim));
        var replayConsumer = new InMemoryApprovalReplayConsumer();

        var verifier = new ApprovalBindingVerifier(
            publicKeyVerifier: publicKeyVerifier,
            attestationValidator: attestationValidator,
            replayConsumer: replayConsumer);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-rsa-01",
            TargetId: "plc-main-assembly",
            ActionKind: "DownloadProgram",
            ProjectHash: "sha256_verified_project_hash",
            ApprovedBy: expectedApprover,
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(15),
            Signature: "",
            Metadata: new Dictionary<string, string>
            {
                ["idp_token"] = expectedToken,
                ["is_human"] = "true",
                ["issuer"] = expectedIssuer,
                ["claims"] = expectedClaim,
                ["target_serial"] = "PLC-HW-SN-998822",
                ["nonce"] = "random-nonce-123456"
            }
        );

        // Sign canonical payload with RSA private key (external identity holder)
        var canonicalPayload = ApprovalBindingVerifier.ComputeCanonicalBindingPayload(binding);
        var payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var signatureBytes = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signedBinding = binding with { Signature = Convert.ToHexString(signatureBytes).ToLowerInvariant() };

        // 1. Dry-run precheck Verify() succeeds for validity, but MUST NOT authorize physical approval without consumption
        var precheckResult = verifier.Verify(signedBinding, "plc-main-assembly", "DownloadProgram", "sha256_verified_project_hash", now);
        Assert.True(precheckResult.IsValid);
        Assert.Null(precheckResult.ErrorReason);
        Assert.Equal("RSA-SHA256:rsa-key-001", precheckResult.ProviderName);
        Assert.False(precheckResult.IsPhysicalApprovalValid, "Verify() is dry-run precheck only and must not authorize physical execution.");

        // 2. Execution authorization VerifyAndConsumeAsync() consumes replay protection and validates physical approval
        var consumeResult = await verifier.VerifyAndConsumeAsync(signedBinding, "plc-main-assembly", "DownloadProgram", "sha256_verified_project_hash", now);
        Assert.True(consumeResult.IsValid);
        Assert.True(consumeResult.IsPhysicalApprovalValid);
        Assert.False(consumeResult.IsIntegrityOnly);
    }

    [Fact]
    public async Task ApprovalVerifier_EcdsaPublicKeyVerification_WithHumanAttestation_Succeeds()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var pubEcdsa = CreatePublicOnlyEcdsa(ecdsa);
        var publicKeyVerifier = new ECDsaPublicKeySignatureVerifier("ecdsa-p256-key", pubEcdsa);

        // Fake in-memory trusted IdP verification callback for test environment (no external network or real IdP called).
        const string expectedToken = "fake-fixed-idp-token-12345";
        const string expectedApprover = "bob@enterprise-idp.example.internal";
        const string expectedIssuer = "https://fake-enterprise-idp.example.internal";
        const string expectedClaim = "role=plc_operator";

        var attestationValidator = new ExternalHumanAttestationValidator(
            "EnterpriseIdP",
            CreateFakeTrustedIdpCheck(expectedToken, expectedApprover, expectedIssuer, expectedClaim));
        var replayConsumer = new InMemoryApprovalReplayConsumer();

        var verifier = new ApprovalBindingVerifier(
            publicKeyVerifier: publicKeyVerifier,
            attestationValidator: attestationValidator,
            replayConsumer: replayConsumer);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-ecdsa-01",
            TargetId: "plc-packaging-cell",
            ActionKind: "DownloadProgram",
            ProjectHash: "sha256_project_v3",
            ApprovedBy: expectedApprover,
            IssuedAt: now.AddMinutes(-2),
            ExpiresAt: now.AddMinutes(20),
            Signature: "",
            Metadata: new Dictionary<string, string>
            {
                ["idp_token"] = expectedToken,
                ["is_human"] = "true",
                ["issuer"] = expectedIssuer,
                ["claims"] = expectedClaim,
                ["target_serial"] = "SIMATIC-S7-SN-4455",
                ["nonce"] = "nonce-987"
            }
        );

        var canonicalPayload = ApprovalBindingVerifier.ComputeCanonicalBindingPayload(binding);
        var payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var signatureBytes = ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);
        var signedBinding = binding with { Signature = Convert.ToHexString(signatureBytes).ToLowerInvariant() };

        var precheckResult = verifier.Verify(signedBinding, "plc-packaging-cell", "DownloadProgram", "sha256_project_v3", now);
        Assert.True(precheckResult.IsValid);
        Assert.False(precheckResult.IsPhysicalApprovalValid);
        Assert.Equal("ECDSA-SHA256:ecdsa-p256-key", precheckResult.ProviderName);

        var consumeResult = await verifier.VerifyAndConsumeAsync(signedBinding, "plc-packaging-cell", "DownloadProgram", "sha256_project_v3", now);
        Assert.True(consumeResult.IsValid);
        Assert.True(consumeResult.IsPhysicalApprovalValid);
    }

    [Fact]
    public void ApprovalVerifier_CanonicalBindingPayload_BindsAllImportantFields()
    {
        using var rsa = RSA.Create(2048);
        using var pubRsa = CreatePublicOnlyRsa(rsa);
        var publicKeyVerifier = new RsaPublicKeySignatureVerifier("rsa-key-01", pubRsa);

        // Fake in-memory IdP callback for unit test
        var attestationValidator = new ExternalHumanAttestationValidator(
            "CorpIdP",
            CreateFakeTrustedIdpCheck("token-valid", "engineer@corp.internal", "https://fake-idp.example.internal", "role=engineer"));
        var verifier = new ApprovalBindingVerifier(publicKeyVerifier: publicKeyVerifier, attestationValidator: attestationValidator);

        var now = DateTimeOffset.UtcNow;
        var baseBinding = new ApprovalBinding(
            ApprovalId: "appr-bind-01",
            TargetId: "target-line-A",
            ActionKind: "DownloadProgram",
            ProjectHash: "project_hash_v1",
            ApprovedBy: "engineer@corp.internal",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "",
            Metadata: new Dictionary<string, string>
            {
                ["idp_token"] = "token-valid",
                ["is_human"] = "true",
                ["issuer"] = "https://fake-idp.example.internal",
                ["claims"] = "role=engineer",
                ["target_serial"] = "SN-LINE-A-001",
                ["nonce"] = "nonce-unique-8877"
            }
        );

        var payloadBytes = Encoding.UTF8.GetBytes(ApprovalBindingVerifier.ComputeCanonicalBindingPayload(baseBinding));
        var signatureBytes = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signedBinding = baseBinding with { Signature = Convert.ToHexString(signatureBytes) };

        // Baseline succeeds
        Assert.True(verifier.Verify(signedBinding, "target-line-A", "DownloadProgram", "project_hash_v1", now).IsValid);

        // 1. Tampering target_serial in metadata breaks canonical signature
        var tamperedSerialMetadata = new Dictionary<string, string>(baseBinding.Metadata!) { ["target_serial"] = "SN-LINE-B-TAMPERED" };
        var tamperedSerialBinding = signedBinding with { Metadata = tamperedSerialMetadata };
        var serialMismatch = verifier.Verify(tamperedSerialBinding, "target-line-A", "DownloadProgram", "project_hash_v1", now);
        Assert.False(serialMismatch.IsValid);
        Assert.Contains("signature verification failed", serialMismatch.ErrorReason);

        // 2. Tampering nonce in metadata breaks canonical signature
        var tamperedNonceMetadata = new Dictionary<string, string>(baseBinding.Metadata!) { ["nonce"] = "nonce-replayed-1111" };
        var tamperedNonceBinding = signedBinding with { Metadata = tamperedNonceMetadata };
        var nonceMismatch = verifier.Verify(tamperedNonceBinding, "target-line-A", "DownloadProgram", "project_hash_v1", now);
        Assert.False(nonceMismatch.IsValid);
        Assert.Contains("signature verification failed", nonceMismatch.ErrorReason);

        // 3. Tampering ApprovedBy identity breaks canonical signature
        var tamperedUserBinding = signedBinding with { ApprovedBy = "forged-engineer@corp.internal" };
        var userMismatch = verifier.Verify(tamperedUserBinding, "target-line-A", "DownloadProgram", "project_hash_v1", now);
        Assert.False(userMismatch.IsValid);
        Assert.Contains("signature verification failed", userMismatch.ErrorReason);
    }

    [Fact]
    public void ApprovalVerifier_SignatureAloneDoesNotProveHumanIdentity_FailsWithoutAttestation()
    {
        using var rsa = RSA.Create(2048);
        using var pubRsa = CreatePublicOnlyRsa(rsa);
        var publicKeyVerifier = new RsaPublicKeySignatureVerifier("rsa-key-01", pubRsa);

        // No attestation validator provided
        var verifierWithoutAttestation = new ApprovalBindingVerifier(publicKeyVerifier: publicKeyVerifier, attestationValidator: null);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-nohuman-01",
            TargetId: "plc-main",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash_ok",
            ApprovedBy: "automated_pipeline_runner",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "",
            Metadata: new Dictionary<string, string> { ["source"] = "ci_cd" }
        );

        var payloadBytes = Encoding.UTF8.GetBytes(ApprovalBindingVerifier.ComputeCanonicalBindingPayload(binding));
        var signatureBytes = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signedBinding = binding with { Signature = Convert.ToHexString(signatureBytes) };

        var result = verifierWithoutAttestation.Verify(signedBinding, "plc-main", "DownloadProgram", "hash_ok", now);

        Assert.False(result.IsValid);
        Assert.Contains("External human identity provider attestation missing", result.ErrorReason);
    }

    [Fact]
    public void ApprovalVerifier_NullMetadata_FailsClosed()
    {
        using var rsa = RSA.Create(2048);
        using var pubRsa = CreatePublicOnlyRsa(rsa);
        var publicKeyVerifier = new RsaPublicKeySignatureVerifier("rsa-key-01", pubRsa);
        var attestationValidator = new ExternalHumanAttestationValidator("IdP", CreateFakeTrustedIdpCheck());
        var verifier = new ApprovalBindingVerifier(publicKeyVerifier: publicKeyVerifier, attestationValidator: attestationValidator);

        var now = DateTimeOffset.UtcNow;
        var bindingWithNullMetadata = new ApprovalBinding(
            ApprovalId: "appr-nullmeta-01",
            TargetId: "plc-main",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash_ok",
            ApprovedBy: "john_doe",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "deadbeef",
            Metadata: null // Null metadata must fail closed
        );

        var result = verifier.Verify(bindingWithNullMetadata, "plc-main", "DownloadProgram", "hash_ok", now);

        Assert.False(result.IsValid);
        Assert.Contains("Approval metadata is required (fail-closed)", result.ErrorReason);
    }

    [Fact]
    public async Task ApprovalVerifier_ReplayPrevention_PersistentConsumerInterface()
    {
        using var rsa = RSA.Create(2048);
        using var pubRsa = CreatePublicOnlyRsa(rsa);
        var publicKeyVerifier = new RsaPublicKeySignatureVerifier("rsa-key-01", pubRsa);

        const string expectedToken = "fake-fixed-idp-token-12345";
        const string expectedApprover = "alice@corporate-idp.example.com";
        const string expectedIssuer = "https://fake-idp.example.internal";
        const string expectedClaim = "role=engineer";

        var attestationValidator = new ExternalHumanAttestationValidator(
            "CorporateIdP",
            CreateFakeTrustedIdpCheck(expectedToken, expectedApprover, expectedIssuer, expectedClaim));
        var replayConsumer = new InMemoryApprovalReplayConsumer();

        var verifier = new ApprovalBindingVerifier(
            publicKeyVerifier: publicKeyVerifier,
            attestationValidator: attestationValidator,
            replayConsumer: replayConsumer);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-consume-once-999",
            TargetId: "plc-main",
            ActionKind: "DownloadProgram",
            ProjectHash: "project_hash_v1",
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

        // First verification & consumption must succeed and authorize physical approval
        var firstResult = await verifier.VerifyAndConsumeAsync(signedBinding, "plc-main", "DownloadProgram", "project_hash_v1", now);
        Assert.True(firstResult.IsValid);
        Assert.Null(firstResult.ErrorReason);
        Assert.True(firstResult.IsPhysicalApprovalValid);

        // Second verification with the exact same approvalId must be rejected as a replay!
        var replayResult = await verifier.VerifyAndConsumeAsync(signedBinding, "plc-main", "DownloadProgram", "project_hash_v1", now);
        Assert.False(replayResult.IsValid);
        Assert.Contains("already been consumed or replay was detected", replayResult.ErrorReason);
        Assert.False(replayResult.IsPhysicalApprovalValid);
    }

    [Fact]
    public void ApprovalVerifier_RsaPublicKeyVerifier_RejectsPrivateKey_FailsClosed()
    {
        // Supplying an RSA object that holds private keys must throw ArgumentException (fail-closed)
        using var rsaWithPrivateKey = RSA.Create(2048);
        var ex = Assert.Throws<ArgumentException>(() => new RsaPublicKeySignatureVerifier("rsa-key-leak", rsaWithPrivateKey));
        Assert.Contains("must only be initialized with an external public key", ex.Message);
    }

    [Fact]
    public void ApprovalVerifier_EcdsaPublicKeyVerifier_RejectsPrivateKey_FailsClosed()
    {
        // Supplying an ECDsa object that holds private keys must throw ArgumentException (fail-closed)
        using var ecdsaWithPrivateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ex = Assert.Throws<ArgumentException>(() => new ECDsaPublicKeySignatureVerifier("ecdsa-key-leak", ecdsaWithPrivateKey));
        Assert.Contains("must only be initialized with an external public key", ex.Message);
    }

    [Fact]
    public void ApprovalVerifier_ExternalHumanAttestation_DefaultNoCustomCheck_FailsClosed()
    {
        // Default constructor without customCheck callback must fail closed on attestation
        var validator = new ExternalHumanAttestationValidator("CorporateIdP");

        var binding = new ApprovalBinding(
            ApprovalId: "appr-01",
            TargetId: "target-1",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash1",
            ApprovedBy: "alice@corporate-idp.example.com",
            IssuedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            Signature: "sig",
            Metadata: new Dictionary<string, string>
            {
                ["idp_token"] = "some-token",
                ["is_human"] = "true"
            }
        );

        var valid = validator.ValidateAttestation(binding, out var failureReason);
        Assert.False(valid);
        Assert.Contains("requires a configured customCheck verification callback", failureReason);
    }

    [Fact]
    public void ApprovalVerifier_ExternalHumanAttestation_MissingOrInvalidIsHuman_FailsClosed()
    {
        var validator = new ExternalHumanAttestationValidator("CorporateIdP", CreateFakeTrustedIdpCheck());

        // 1. Missing is_human claim
        var bindingMissingIsHuman = new ApprovalBinding(
            ApprovalId: "appr-02",
            TargetId: "target-1",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash1",
            ApprovedBy: "alice@corporate-idp.example.com",
            IssuedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            Signature: "sig",
            Metadata: new Dictionary<string, string>
            {
                ["idp_token"] = "fake-fixed-idp-token-12345",
                ["issuer"] = "https://fake-idp.example.internal",
                ["claims"] = "role=plc_safety_engineer"
                // No is_human!
            }
        );

        Assert.False(validator.ValidateAttestation(bindingMissingIsHuman, out var reason1));
        Assert.Contains("missing or has invalid 'is_human=true' attestation claim", reason1);

        // 2. is_human explicitly "false"
        var bindingFalseIsHuman = bindingMissingIsHuman with
        {
            Metadata = new Dictionary<string, string>(bindingMissingIsHuman.Metadata!) { ["is_human"] = "false" }
        };
        Assert.False(validator.ValidateAttestation(bindingFalseIsHuman, out var reason2));
        Assert.Contains("missing or has invalid 'is_human=true' attestation claim", reason2);

        // 3. is_human malformed string
        var bindingMalformedIsHuman = bindingMissingIsHuman with
        {
            Metadata = new Dictionary<string, string>(bindingMissingIsHuman.Metadata!) { ["is_human"] = "yes_maybe" }
        };
        Assert.False(validator.ValidateAttestation(bindingMalformedIsHuman, out var reason3));
        Assert.Contains("missing or has invalid 'is_human=true' attestation claim", reason3);
    }

    [Fact]
    public void ApprovalVerifier_ExternalHumanAttestation_CustomCheckRejectsMismatches_FailsClosed()
    {
        var validator = new ExternalHumanAttestationValidator(
            "CorporateIdP",
            CreateFakeTrustedIdpCheck(
                expectedToken: "trusted-token",
                expectedApprover: "alice@corporate-idp.example.com",
                expectedIssuer: "https://fake-idp.example.internal",
                expectedClaim: "role=senior_engineer"));

        var validMetadata = new Dictionary<string, string>
        {
            ["idp_token"] = "trusted-token",
            ["is_human"] = "true",
            ["issuer"] = "https://fake-idp.example.internal",
            ["claims"] = "role=senior_engineer"
        };

        var validBinding = new ApprovalBinding(
            ApprovalId: "appr-valid",
            TargetId: "target-1",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash1",
            ApprovedBy: "alice@corporate-idp.example.com",
            IssuedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            Signature: "sig",
            Metadata: validMetadata
        );

        // Valid binding passes
        Assert.True(validator.ValidateAttestation(validBinding, out var nullReason));
        Assert.Null(nullReason);

        // Untrusted token fails
        var untrustedToken = validBinding with { Metadata = new Dictionary<string, string>(validMetadata) { ["idp_token"] = "forged-token" } };
        Assert.False(validator.ValidateAttestation(untrustedToken, out var reasonToken));
        Assert.Contains("token verification failed", reasonToken);

        // Approver mismatch fails
        var wrongApprover = validBinding with { ApprovedBy = "intruder@evil.com" };
        Assert.False(validator.ValidateAttestation(wrongApprover, out var reasonApprover));
        Assert.Contains("approver identity does not match", reasonApprover);

        // Issuer mismatch fails
        var wrongIssuer = validBinding with { Metadata = new Dictionary<string, string>(validMetadata) { ["issuer"] = "https://untrusted-idp.com" } };
        Assert.False(validator.ValidateAttestation(wrongIssuer, out var reasonIssuer));
        Assert.Contains("issuer verification failed", reasonIssuer);

        // Missing claim fails
        var missingClaim = validBinding with { Metadata = new Dictionary<string, string>(validMetadata) { ["claims"] = "role=junior_guest" } };
        Assert.False(validator.ValidateAttestation(missingClaim, out var reasonClaim));
        Assert.Contains("required claims missing", reasonClaim);
    }

    [Fact]
    public async Task ApprovalVerifier_VerifyAndConsumeAsync_WithoutReplayConsumer_FailsClosed()
    {
        using var rsa = RSA.Create(2048);
        using var pubRsa = CreatePublicOnlyRsa(rsa);
        var publicKeyVerifier = new RsaPublicKeySignatureVerifier("rsa-key-01", pubRsa);
        var attestationValidator = new ExternalHumanAttestationValidator("IdP", CreateFakeTrustedIdpCheck());

        // Replay consumer is null, allowHmacForIntegrityOnly is false
        var verifier = new ApprovalBindingVerifier(
            publicKeyVerifier: publicKeyVerifier,
            attestationValidator: attestationValidator,
            replayConsumer: null,
            allowHmacForIntegrityOnly: false);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-no-consumer",
            TargetId: "target-line-A",
            ActionKind: "DownloadProgram",
            ProjectHash: "project_hash_v1",
            ApprovedBy: "alice@corporate-idp.example.com",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "",
            Metadata: new Dictionary<string, string>
            {
                ["idp_token"] = "fake-fixed-idp-token-12345",
                ["is_human"] = "true",
                ["issuer"] = "https://fake-idp.example.internal",
                ["claims"] = "role=plc_safety_engineer"
            }
        );

        var payloadBytes = Encoding.UTF8.GetBytes(ApprovalBindingVerifier.ComputeCanonicalBindingPayload(binding));
        var signatureBytes = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signedBinding = binding with { Signature = Convert.ToHexString(signatureBytes) };

        // Verify() dry-run succeeds
        var precheck = verifier.Verify(signedBinding, "target-line-A", "DownloadProgram", "project_hash_v1", now);
        Assert.True(precheck.IsValid);
        Assert.False(precheck.IsPhysicalApprovalValid);

        // VerifyAndConsumeAsync() MUST fail closed when replayConsumer is missing
        var consumeResult = await verifier.VerifyAndConsumeAsync(signedBinding, "target-line-A", "DownloadProgram", "project_hash_v1", now);
        Assert.False(consumeResult.IsValid);
        Assert.Contains("Approval replay consumer is not configured; cannot safely authorize execution", consumeResult.ErrorReason);
        Assert.False(consumeResult.IsPhysicalApprovalValid);
    }

    [Fact]
    public void ApprovalVerifier_Hmac_AlwaysRejectedForHumanApproval()
    {
        var key = Encoding.UTF8.GetBytes("key-32-bytes-long-for-hmac-test!");
        var hmacValidator = new HmacExternalSignatureValidator("TestHmacIdP", key);
        var attestationValidator = new ExternalHumanAttestationValidator("TestIdP", CreateFakeTrustedIdpCheck());

        // Attempting to pair HMAC with an attestation validator MUST fail closed: HMAC cannot serve as human approval
        var verifier = new ApprovalBindingVerifier(
            externalValidator: hmacValidator,
            attestationValidator: attestationValidator,
            allowHmacForIntegrityOnly: true);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-hmac-human-reject",
            TargetId: "target-1",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash1",
            ApprovedBy: "alice@corporate-idp.example.com",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "",
            Metadata: new Dictionary<string, string>
            {
                ["idp_token"] = "fake-fixed-idp-token-12345",
                ["is_human"] = "true",
                ["issuer"] = "https://fake-idp.example.internal",
                ["claims"] = "role=plc_safety_engineer"
            }
        );
        var signature = HmacExternalSignatureValidator.Sign(binding, key);
        var signedBinding = binding with { Signature = signature };

        var result = verifier.Verify(signedBinding, "target-1", "DownloadProgram", "hash1", now);
        Assert.False(result.IsValid);
        Assert.Contains("HMAC signature cannot serve as human approval", result.ErrorReason);
    }

    [Fact]
    public async Task ApprovalVerifier_HmacIntegrityOnly_VerifyAndConsumeAsync_NeverMarksPhysicalApprovalValid()
    {
        var key = Encoding.UTF8.GetBytes("key-32-bytes-long-for-hmac-test!");
        var hmacValidator = new HmacExternalSignatureValidator("TestHmacIdP", key);

        // In test integrity-only mode without attestation validator and without replay consumer
        var verifier = new ApprovalBindingVerifier(
            externalValidator: hmacValidator,
            allowHmacForIntegrityOnly: true);

        var now = DateTimeOffset.UtcNow;
        var binding = new ApprovalBinding(
            ApprovalId: "appr-hmac-integrity-01",
            TargetId: "target-1",
            ActionKind: "DownloadProgram",
            ProjectHash: "hash1",
            ApprovedBy: "test-system",
            IssuedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddMinutes(10),
            Signature: "",
            Metadata: new Dictionary<string, string> { ["source"] = "test" }
        );
        var signature = HmacExternalSignatureValidator.Sign(binding, key);
        var signedBinding = binding with { Signature = signature };

        var verifyResult = verifier.Verify(signedBinding, "target-1", "DownloadProgram", "hash1", now);
        Assert.True(verifyResult.IsValid);
        Assert.True(verifyResult.IsIntegrityOnly);
        Assert.False(verifyResult.IsPhysicalApprovalValid);

        var consumeResult = await verifier.VerifyAndConsumeAsync(signedBinding, "target-1", "DownloadProgram", "hash1", now);
        Assert.True(consumeResult.IsValid);
        Assert.True(consumeResult.IsIntegrityOnly);
        Assert.False(consumeResult.IsPhysicalApprovalValid, "Integrity-only test mode MUST NEVER mark physical approval as valid.");
    }

    /// <summary>
    /// Creates an RSA instance containing only public key parameters from a full key pair.
    /// External verifiers must only hold public keys, never private keys.
    /// </summary>
    private static RSA CreatePublicOnlyRsa(RSA rsa)
    {
        var pubParams = rsa.ExportParameters(includePrivateParameters: false);
        var pubRsa = RSA.Create();
        pubRsa.ImportParameters(pubParams);
        return pubRsa;
    }

    /// <summary>
    /// Creates an ECDsa instance containing only public key parameters from a full key pair.
    /// External verifiers must only hold public keys, never private keys.
    /// </summary>
    private static ECDsa CreatePublicOnlyEcdsa(ECDsa ecdsa)
    {
        var pubParams = ecdsa.ExportParameters(includePrivateParameters: false);
        var pubEcdsa = ECDsa.Create();
        pubEcdsa.ImportParameters(pubParams);
        return pubEcdsa;
    }

    /// <summary>
    /// Creates a fake in-memory trusted IdP verification callback for test environments.
    /// NOTE: This is purely a fake test callback; it never connects to real external services or devices.
    /// Validates fixed signed token, approver identity, expected issuer, and required claims.
    /// </summary>
    private static Func<ApprovalBinding, (bool IsValid, string? Reason)> CreateFakeTrustedIdpCheck(
        string expectedToken = "fake-fixed-idp-token-12345",
        string expectedApprover = "alice@corporate-idp.example.com",
        string expectedIssuer = "https://fake-idp.example.internal",
        string expectedClaim = "role=plc_safety_engineer")
    {
        return binding =>
        {
            if (binding.Metadata == null)
            {
                return (false, "Fake IdP: metadata is null.");
            }

            if (!binding.Metadata.TryGetValue("idp_token", out var token) || !string.Equals(token, expectedToken, StringComparison.Ordinal))
            {
                return (false, "Fake IdP: token verification failed (untrusted or mismatched token).");
            }

            if (!string.Equals(binding.ApprovedBy, expectedApprover, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Fake IdP: approver identity does not match token subject.");
            }

            if (!binding.Metadata.TryGetValue("issuer", out var issuer) || !string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
            {
                return (false, "Fake IdP: issuer verification failed (untrusted issuer).");
            }

            if (!binding.Metadata.TryGetValue("claims", out var claims) || !claims.Contains(expectedClaim, StringComparison.Ordinal))
            {
                return (false, "Fake IdP: required claims missing from attestation.");
            }

            return (true, null);
        };
    }
}
