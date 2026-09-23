using System.Security.Cryptography;
using System.Text;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime.Governance;

public interface IExternalSignatureValidator
{
    string ProviderName { get; }
    bool VerifySignature(ApprovalBinding binding);
}

/// <summary>
/// Verifies external public key signatures (e.g. RSA, ECDSA, Ed25519) on canonical approval payloads.
/// </summary>
public interface IPublicKeySignatureVerifier
{
    string KeyId { get; }
    string Algorithm { get; }
    bool VerifySignature(byte[] payloadBytes, byte[] signatureBytes);
}

/// <summary>
/// Validates external identity provider attestation (claims, token validity, human approver verification).
/// Cryptographic signature alone does not prove human identity; this verifies the approver is an authenticated human operator.
/// </summary>
public interface IHumanAttestationValidator
{
    string ProviderName { get; }
    bool ValidateAttestation(ApprovalBinding binding, out string? failureReason);
}

/// <summary>
/// Interface for persistent consumption and replay prevention of approval IDs.
/// Does not implement real physical writes.
/// </summary>
public interface IApprovalReplayConsumer
{
    ValueTask<bool> TryConsumeApprovalAsync(
        string approvalId,
        string targetId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);
}

public interface IApprovalBindingVerifier
{
    /// <summary>
    /// Performs non-consuming dry-run precheck verification.
    /// WARNING: This method does NOT consume the approval token and MUST NOT be used to authorize
    /// physical execution or state-modifying actions. Callers MUST call <see cref="VerifyAndConsumeAsync"/>
    /// for execution authorization. Result will always have IsPhysicalApprovalValid = false.
    /// </summary>
    ApprovalVerificationResult Verify(
        ApprovalBinding? binding,
        string expectedTargetId,
        string expectedActionKind,
        string expectedProjectHash,
        DateTimeOffset? asOfUtc = null);

    ValueTask<ApprovalVerificationResult> VerifyAndConsumeAsync(
        ApprovalBinding? binding,
        string expectedTargetId,
        string expectedActionKind,
        string expectedProjectHash,
        DateTimeOffset? asOfUtc = null,
        CancellationToken cancellationToken = default);
}

public sealed record ApprovalVerificationResult(
    bool IsValid,
    string? ErrorReason,
    bool ExternalIdentityProviderConfigured,
    string? ProviderName = null,
    bool IsPhysicalApprovalValid = false,
    bool IsIntegrityOnly = false);

public sealed class ApprovalBindingVerifier : IApprovalBindingVerifier
{
    private readonly IExternalSignatureValidator? _externalValidator;
    private readonly IPublicKeySignatureVerifier? _publicKeyVerifier;
    private readonly IHumanAttestationValidator? _attestationValidator;
    private readonly IApprovalReplayConsumer? _replayConsumer;
    private readonly bool _allowHmacForIntegrityOnly;

    public ApprovalBindingVerifier(
        IExternalSignatureValidator? externalValidator = null,
        IPublicKeySignatureVerifier? publicKeyVerifier = null,
        IHumanAttestationValidator? attestationValidator = null,
        IApprovalReplayConsumer? replayConsumer = null,
        bool allowHmacForIntegrityOnly = false)
    {
        _externalValidator = externalValidator;
        _publicKeyVerifier = publicKeyVerifier;
        _attestationValidator = attestationValidator;
        _replayConsumer = replayConsumer;
        _allowHmacForIntegrityOnly = allowHmacForIntegrityOnly;
    }

    public static string ComputeCanonicalBindingPayload(ApprovalBinding binding)
    {
        var metaTargetSerial = "";
        var metaNonce = "";
        if (binding.Metadata != null)
        {
            if (binding.Metadata.TryGetValue("target_serial", out var s) && !string.IsNullOrWhiteSpace(s))
            {
                metaTargetSerial = s;
            }
            if (binding.Metadata.TryGetValue("nonce", out var n) && !string.IsNullOrWhiteSpace(n))
            {
                metaNonce = n;
            }
        }

        return $"{binding.ApprovalId}|{binding.TargetId}|{binding.ActionKind}|{binding.ProjectHash}|{binding.ApprovedBy}|{binding.IssuedAt:O}|{binding.ExpiresAt:O}|{metaTargetSerial}|{metaNonce}";
    }

    /// <summary>
    /// Performs non-consuming verification (dry-run / pre-check only).
    /// WARNING: This does NOT consume the approval and MUST NOT be used as authorization for physical execution or writes.
    /// To authorize execution, call <see cref="VerifyAndConsumeAsync"/>.
    /// </summary>
    public ApprovalVerificationResult Verify(
        ApprovalBinding? binding,
        string expectedTargetId,
        string expectedActionKind,
        string expectedProjectHash,
        DateTimeOffset? asOfUtc = null)
    {
        // Fail closed if binding is null
        if (binding == null)
        {
            return new ApprovalVerificationResult(false, "Missing approval binding (fail-closed).", HasAnyExternalValidatorConfigured());
        }

        // Fail-closed if no external identity/signature validator is connected
        if (!HasAnyExternalValidatorConfigured())
        {
            return new ApprovalVerificationResult(
                false,
                "No external identity provider/signature validator configured; failing closed.",
                ExternalIdentityProviderConfigured: false);
        }

        // Required field validations
        if (string.IsNullOrWhiteSpace(binding.ApprovalId))
        {
            return new ApprovalVerificationResult(false, "ApprovalId is required.", true);
        }

        if (string.IsNullOrWhiteSpace(binding.ApprovedBy))
        {
            return new ApprovalVerificationResult(false, "ApprovedBy identity is required.", true);
        }

        if (string.IsNullOrWhiteSpace(binding.Signature))
        {
            return new ApprovalVerificationResult(false, "Cryptographic signature is required.", true);
        }

        // Reject self-minted local tokens (before metadata check so specific error is reported)
        if (IsSelfMintedOrLocal(binding.ApprovedBy) ||
            (binding.Metadata != null && binding.Metadata.TryGetValue("source", out var src) && IsSelfMintedOrLocal(src)))
        {
            return new ApprovalVerificationResult(
                false,
                "Self-minted or local machine token cannot substitute for external human approval.",
                true);
        }

        // Null Metadata must fail-closed: metadata must carry explicit human approval context
        if (binding.Metadata == null)
        {
            return new ApprovalVerificationResult(false, "Approval metadata is required (fail-closed).", true);
        }

        // Strict target binding
        if (!string.Equals(binding.TargetId, expectedTargetId, StringComparison.OrdinalIgnoreCase))
        {
            return new ApprovalVerificationResult(
                false,
                $"Approval target mismatch: expected '{expectedTargetId}', got '{binding.TargetId}'.",
                true);
        }

        // Strict action binding
        if (!string.Equals(binding.ActionKind, expectedActionKind, StringComparison.OrdinalIgnoreCase))
        {
            return new ApprovalVerificationResult(
                false,
                $"Approval action mismatch: expected '{expectedActionKind}', got '{binding.ActionKind}'.",
                true);
        }

        // Strict project hash binding
        if (!string.Equals(binding.ProjectHash, expectedProjectHash, StringComparison.OrdinalIgnoreCase))
        {
            return new ApprovalVerificationResult(
                false,
                $"Approval project hash mismatch: expected '{expectedProjectHash}', got '{binding.ProjectHash}'.",
                true);
        }

        // Expiry and validity window
        var now = asOfUtc ?? DateTimeOffset.UtcNow;
        if (binding.IssuedAt > now)
        {
            return new ApprovalVerificationResult(false, "Approval token is not yet valid (issued in future).", true);
        }

        if (binding.ExpiresAt <= now)
        {
            return new ApprovalVerificationResult(false, "Approval token has expired.", true);
        }

        // 1. Verify Cryptographic Signature
        string providerName;
        if (_publicKeyVerifier != null)
        {
            providerName = $"{_publicKeyVerifier.Algorithm}:{_publicKeyVerifier.KeyId}";
            try
            {
                var canonicalPayload = ComputeCanonicalBindingPayload(binding);
                var payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
                if (!TryDecodeSignature(binding.Signature, out var signatureBytes))
                {
                    return new ApprovalVerificationResult(
                        false,
                        "Cryptographic signature format is invalid (expected hex or base64 string).",
                        ExternalIdentityProviderConfigured: true,
                        ProviderName: providerName);
                }

                if (!_publicKeyVerifier.VerifySignature(payloadBytes, signatureBytes))
                {
                    return new ApprovalVerificationResult(
                        false,
                        "Cryptographic public key signature verification failed.",
                        ExternalIdentityProviderConfigured: true,
                        ProviderName: providerName);
                }
            }
            catch (Exception ex)
            {
                return new ApprovalVerificationResult(
                    false,
                    $"Public key signature verifier error: {ex.Message}",
                    ExternalIdentityProviderConfigured: true,
                    ProviderName: providerName);
            }
        }
        else if (_externalValidator != null)
        {
            providerName = _externalValidator.ProviderName;

            // HMAC is strictly for cryptographic integrity testing and is ALWAYS rejected for human approval
            if (_externalValidator is HmacExternalSignatureValidator)
            {
                if (_attestationValidator != null)
                {
                    return new ApprovalVerificationResult(
                        false,
                        "HMAC signature cannot serve as human approval; symmetric keys cannot prove human identity even with attestation.",
                        ExternalIdentityProviderConfigured: true,
                        ProviderName: providerName);
                }

                if (!_allowHmacForIntegrityOnly)
                {
                    return new ApprovalVerificationResult(
                        false,
                        "HMAC signature cannot serve as human approval (asymmetric public key verification or human attestation required).",
                        ExternalIdentityProviderConfigured: true,
                        ProviderName: providerName);
                }
            }

            try
            {
                if (!_externalValidator.VerifySignature(binding))
                {
                    return new ApprovalVerificationResult(
                        false,
                        "Cryptographic signature verification failed by external provider.",
                        ExternalIdentityProviderConfigured: true,
                        ProviderName: providerName);
                }
            }
            catch (Exception ex)
            {
                return new ApprovalVerificationResult(
                    false,
                    $"External identity provider error: {ex.Message}",
                    ExternalIdentityProviderConfigured: true,
                    ProviderName: providerName);
            }
        }
        else
        {
            return new ApprovalVerificationResult(
                false,
                "No external signature verifier available.",
                ExternalIdentityProviderConfigured: false);
        }

        // 2. Cryptographic signature alone does NOT prove human identity:
        // Must verify human attestation via external identity provider unless in explicit test integrity mode
        if (_attestationValidator != null)
        {
            if (!_attestationValidator.ValidateAttestation(binding, out var attestationError))
            {
                return new ApprovalVerificationResult(
                    false,
                    $"Human identity attestation failed: {attestationError ?? "Attestation rejected by external identity provider."}",
                    ExternalIdentityProviderConfigured: true,
                    ProviderName: _attestationValidator.ProviderName);
            }

            // Verify() is a non-consuming precheck only.
            // IsPhysicalApprovalValid MUST remain false to prevent bypassing consumption.
            return new ApprovalVerificationResult(
                true,
                null,
                ExternalIdentityProviderConfigured: true,
                ProviderName: providerName,
                IsPhysicalApprovalValid: false,
                IsIntegrityOnly: false);
        }
        else if (_allowHmacForIntegrityOnly)
        {
            // Explicit test integrity-only mode: valid cryptographically for unit tests,
            // but NEVER valid for physical human approval.
            return new ApprovalVerificationResult(
                true,
                null,
                ExternalIdentityProviderConfigured: true,
                ProviderName: providerName,
                IsPhysicalApprovalValid: false,
                IsIntegrityOnly: true);
        }
        else
        {
            // Fail-closed if no human attestation validator is attached
            return new ApprovalVerificationResult(
                false,
                "External human identity provider attestation missing (cryptographic signature alone does not prove human identity).",
                ExternalIdentityProviderConfigured: true,
                ProviderName: providerName);
        }
    }

    private static bool TryDecodeSignature(string signature, out byte[] signatureBytes)
    {
        var trimmed = signature.Trim();
        try
        {
            signatureBytes = Convert.FromHexString(trimmed);
            return true;
        }
        catch (FormatException)
        {
            try
            {
                signatureBytes = Convert.FromBase64String(trimmed);
                return true;
            }
            catch (FormatException)
            {
                signatureBytes = Array.Empty<byte>();
                return false;
            }
        }
    }

    public async ValueTask<ApprovalVerificationResult> VerifyAndConsumeAsync(
        ApprovalBinding? binding,
        string expectedTargetId,
        string expectedActionKind,
        string expectedProjectHash,
        DateTimeOffset? asOfUtc = null,
        CancellationToken cancellationToken = default)
    {
        var result = Verify(binding, expectedTargetId, expectedActionKind, expectedProjectHash, asOfUtc);
        if (!result.IsValid)
        {
            return result;
        }

        if (_replayConsumer == null)
        {
            // 唯独显式测试 integrity-only 模式可用于单测但返回中不能标成 PhysicalApproval Valid
            if (_allowHmacForIntegrityOnly && result.IsIntegrityOnly)
            {
                return result with { IsPhysicalApprovalValid = false };
            }

            return new ApprovalVerificationResult(
                false,
                "Approval replay consumer is not configured; cannot safely authorize execution without replay prevention (fail-closed).",
                ExternalIdentityProviderConfigured: result.ExternalIdentityProviderConfigured,
                ProviderName: result.ProviderName,
                IsPhysicalApprovalValid: false,
                IsIntegrityOnly: result.IsIntegrityOnly);
        }

        if (binding != null)
        {
            var consumed = await _replayConsumer.TryConsumeApprovalAsync(
                binding.ApprovalId,
                binding.TargetId,
                binding.ExpiresAt,
                cancellationToken).ConfigureAwait(false);

            if (!consumed)
            {
                return new ApprovalVerificationResult(
                    false,
                    $"Approval '{binding.ApprovalId}' has already been consumed or replay was detected.",
                    ExternalIdentityProviderConfigured: true,
                    ProviderName: result.ProviderName,
                    IsPhysicalApprovalValid: false,
                    IsIntegrityOnly: result.IsIntegrityOnly);
            }
        }

        // Successfully consumed.
        // If it was integrity-only, IsPhysicalApprovalValid remains false.
        // If it was genuine asymmetric human approval with attestation, IsPhysicalApprovalValid is true.
        return result with { IsPhysicalApprovalValid = !result.IsIntegrityOnly };
    }

    private bool HasAnyExternalValidatorConfigured()
    {
        return _externalValidator != null || _publicKeyVerifier != null || _attestationValidator != null;
    }

    private static bool IsSelfMintedOrLocal(string identity)
    {
        var normalized = identity.Trim().ToLowerInvariant();
        return normalized is "self"
            or "self-minted"
            or "local"
            or "localhost"
            or "system"
            or "auto"
            or "anonymous"
            or "plcruntimeservice";
    }
}

/// <summary>
/// HMAC-based validator retained STRICTLY for testing signature cryptographic integrity.
/// WARNING: Cannot be used as proof of human approval in production environments.
/// </summary>
public sealed class HmacExternalSignatureValidator : IExternalSignatureValidator
{
    private readonly byte[] _key;

    public string ProviderName { get; }

    public HmacExternalSignatureValidator(string providerName, byte[] key)
    {
        ProviderName = providerName;
        _key = key;
    }

    /// <summary>
    /// Computes HMAC signature over canonical payload.
    /// Strictly for integrity testing; cannot serve as human approval.
    /// </summary>
    public static string Sign(ApprovalBinding binding, byte[] key)
    {
        var payload = ApprovalBindingVerifier.ComputeCanonicalBindingPayload(binding);
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public bool VerifySignature(ApprovalBinding binding)
    {
        var expectedSig = Sign(binding, _key);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedSig),
            Encoding.UTF8.GetBytes(binding.Signature.Trim().ToLowerInvariant()));
    }
}

/// <summary>
/// External public-key signature verifier using RSA public key (e.g. PKCS#1 or PSS).
/// Fail-closed: only accepts public keys. Passing an RSA object with private keys is rejected.
/// </summary>
public sealed class RsaPublicKeySignatureVerifier : IPublicKeySignatureVerifier
{
    private readonly RSA _rsa;
    private readonly HashAlgorithmName _hashAlgorithm;
    private readonly RSASignaturePadding _padding;

    public string KeyId { get; }
    public string Algorithm => $"RSA-{_hashAlgorithm.Name}";

    public RsaPublicKeySignatureVerifier(
        string keyId,
        RSA rsa,
        HashAlgorithmName? hashAlgorithm = null,
        RSASignaturePadding? padding = null)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("KeyId must be provided.", nameof(keyId));
        }

        if (HasPrivateKey(rsa))
        {
            throw new ArgumentException(
                "RsaPublicKeySignatureVerifier must only be initialized with an external public key. Supplying an RSA object with private key material cannot claim external identity.",
                nameof(rsa));
        }

        KeyId = keyId;
        _rsa = rsa;
        _hashAlgorithm = hashAlgorithm ?? HashAlgorithmName.SHA256;
        _padding = padding ?? RSASignaturePadding.Pkcs1;
    }

    private static bool HasPrivateKey(RSA rsa)
    {
        try
        {
            var parameters = rsa.ExportParameters(includePrivateParameters: true);
            return parameters.D != null && parameters.D.Length > 0;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public bool VerifySignature(byte[] payloadBytes, byte[] signatureBytes)
    {
        return _rsa.VerifyData(payloadBytes, signatureBytes, _hashAlgorithm, _padding);
    }
}

/// <summary>
/// External public-key signature verifier using ECDsa public key.
/// Fail-closed: only accepts public keys. Passing an ECDsa object with private keys is rejected.
/// </summary>
public sealed class ECDsaPublicKeySignatureVerifier : IPublicKeySignatureVerifier
{
    private readonly ECDsa _ecdsa;
    private readonly HashAlgorithmName _hashAlgorithm;

    public string KeyId { get; }
    public string Algorithm => $"ECDSA-{_hashAlgorithm.Name}";

    public ECDsaPublicKeySignatureVerifier(
        string keyId,
        ECDsa ecdsa,
        HashAlgorithmName? hashAlgorithm = null)
    {
        ArgumentNullException.ThrowIfNull(ecdsa);
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("KeyId must be provided.", nameof(keyId));
        }

        if (HasPrivateKey(ecdsa))
        {
            throw new ArgumentException(
                "ECDsaPublicKeySignatureVerifier must only be initialized with an external public key. Supplying an ECDsa object with private key material cannot claim external identity.",
                nameof(ecdsa));
        }

        KeyId = keyId;
        _ecdsa = ecdsa;
        _hashAlgorithm = hashAlgorithm ?? HashAlgorithmName.SHA256;
    }

    private static bool HasPrivateKey(ECDsa ecdsa)
    {
        try
        {
            var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
            return parameters.D != null && parameters.D.Length > 0;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public bool VerifySignature(byte[] payloadBytes, byte[] signatureBytes)
    {
        return _ecdsa.VerifyData(payloadBytes, signatureBytes, _hashAlgorithm);
    }
}

/// <summary>
/// Claims-based external identity provider attestation validator.
/// Verifies external claim tokens, identity verification, and human operator flags.
/// Fail-closed: requires an explicit external customCheck verification callback to validate tokens/claims.
/// </summary>
public sealed class ExternalHumanAttestationValidator : IHumanAttestationValidator
{
    public string ProviderName { get; }
    private readonly Func<ApprovalBinding, (bool IsValid, string? Reason)>? _customCheck;

    public ExternalHumanAttestationValidator(
        string providerName,
        Func<ApprovalBinding, (bool IsValid, string? Reason)>? customCheck = null)
    {
        ProviderName = providerName;
        _customCheck = customCheck;
    }

    public bool ValidateAttestation(ApprovalBinding binding, out string? failureReason)
    {
        // Fail-closed: must have customCheck configured
        if (_customCheck == null)
        {
            failureReason = "External human attestation requires a configured customCheck verification callback; none provided (fail-closed).";
            return false;
        }

        if (binding.Metadata == null)
        {
            failureReason = "Metadata is missing; cannot verify human identity claims (fail-closed).";
            return false;
        }

        if (!binding.Metadata.TryGetValue("idp_token", out var token) || string.IsNullOrWhiteSpace(token))
        {
            failureReason = "Missing external IdP attestation token in approval metadata.";
            return false;
        }

        // 不对缺失 is_human 报 true (fail-closed if missing, unparseable, or false)
        if (!binding.Metadata.TryGetValue("is_human", out var isHumanStr) ||
            !bool.TryParse(isHumanStr, out var isHuman) ||
            !isHuman)
        {
            failureReason = "Approval metadata is missing or has invalid 'is_human=true' attestation claim (fail-closed).";
            return false;
        }

        // Callback must verify token/approver/claims/issuer
        var (valid, reason) = _customCheck(binding);
        if (!valid)
        {
            failureReason = reason ?? "Custom IdP attestation policy rejected approval.";
            return false;
        }

        failureReason = null;
        return true;
    }
}

/// <summary>
/// In-memory replay consumer implementation for tracking consumed approval IDs in tests or single node setups.
/// Does not perform physical writes.
/// </summary>
public sealed class InMemoryApprovalReplayConsumer : IApprovalReplayConsumer
{
    private readonly HashSet<string> _consumedApprovalIds = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ValueTask<bool> TryConsumeApprovalAsync(
        string approvalId,
        string targetId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            return ValueTask.FromResult(false);
        }

        lock (_lock)
        {
            if (_consumedApprovalIds.Contains(approvalId))
            {
                return ValueTask.FromResult(false);
            }

            _consumedApprovalIds.Add(approvalId);
            return ValueTask.FromResult(true);
        }
    }
}
