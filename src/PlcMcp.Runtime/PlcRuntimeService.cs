using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Runtime.Clients;
using PlcMcp.Runtime.Policy;

namespace PlcMcp.Runtime;

public sealed class PlcRuntimeService
{
    private readonly IReadOnlyDictionary<string, TargetProfile> _targets;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TagDefinition>> _manifests;
    private readonly IPlcRuntimeClient _client;
    private readonly SafetyPolicy _policy;
    private readonly PlanStore _plans;
    private readonly IAuditSink _audit;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _targetGates = new(StringComparer.Ordinal);

    public PlcRuntimeService(
        IEnumerable<TargetProfile> targets,
        IReadOnlyDictionary<string, IReadOnlyList<TagDefinition>> manifests,
        IPlcRuntimeClient client,
        SafetyPolicy policy,
        PlanStore plans,
        IAuditSink audit)
    {
        _targets = targets.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        _manifests = manifests;
        _client = client;
        _policy = policy;
        _plans = plans;
        _audit = audit;
    }

    public IReadOnlyList<TargetProfile> ListTargets() => _targets.Values.OrderBy(x => x.Id).ToArray();

    public TargetProfile GetTarget(string targetId) =>
        _targets.TryGetValue(targetId, out var target)
            ? target
            : throw new KeyNotFoundException($"Unknown target '{targetId}'.");

    public IReadOnlyList<TagDefinition> ListTags(string targetId, string? filter = null)
    {
        GetTarget(targetId);
        if (!_manifests.TryGetValue(targetId, out var tags))
        {
            return [];
        }

        return string.IsNullOrWhiteSpace(filter)
            ? tags
            : tags.Where(x => x.AllNames.Any(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public async Task<ProbeResult> ProbeAsync(string targetId, CancellationToken cancellationToken = default)
    {
        var target = GetTarget(targetId);
        using var lease = await EnterTargetAsync(targetId, cancellationToken).ConfigureAwait(false);
        return await _client.ProbeAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TagValue>> ReadAsync(
        string targetId,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        var target = GetTarget(targetId);
        var tags = ResolveTags(targetId, names);
        using var lease = await EnterTargetAsync(targetId, cancellationToken).ConfigureAwait(false);
        return await _client.ReadAsync(target, tags, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationPlan> PlanWriteAsync(
        string targetId,
        IReadOnlyDictionary<string, object?> changes,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var target = GetTarget(targetId);
        if (changes.Count is < 1 or > 128) throw new ArgumentException("Plans must contain 1 to 128 tag changes.");
        var ttl = lifetime ?? TimeSpan.FromMinutes(5);
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromMinutes(5)) throw new ArgumentException("Plan lifetime must be positive and at most 5 minutes.");
        var tagMapping = ResolveTagMapping(target.Id, changes.Keys);
        var tags = tagMapping.Values.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        using var lease = await EnterTargetAsync(target.Id, cancellationToken).ConfigureAwait(false);
        var current = await _client.ReadAsync(target, tags, cancellationToken).ConfigureAwait(false);
        if (current.Count != tags.Length || current.Any(v => v.Quality is QualityCode.Bad or QualityCode.Uncertain || v.Value is null))
            throw new InvalidOperationException("Cannot plan a write from missing or invalid current values.");
        var currentByName = current.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var planned = new List<PlannedChange>();
        var seenCanonicalTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in changes)
        {
            var tag = tagMapping[pair.Key];
            if (!seenCanonicalTags.Add(tag.Name))
            {
                throw new InvalidOperationException($"Duplicate change specified for canonical tag '{tag.Name}'.");
            }

            if (!_policy.CanWrite(target, tag, pair.Value!, out var reason))
            {
                throw new InvalidOperationException($"Write rejected for '{tag.Name}': {reason}");
            }

            if (!ValueConverter.TryConvertAndValidate(pair.Value, tag, out var convertedValue, out var valError))
            {
                throw new InvalidOperationException($"Write rejected for '{tag.Name}': {valError}");
            }

            planned.Add(new PlannedChange(
                tag.Name,
                currentByName[tag.Name].Value,
                convertedValue,
                tag.DataType,
                tag.Unit,
                tag.SafetyClass,
                tag.Minimum,
                tag.Maximum));
        }

        var stateHash = StateHasher.Compute(current);
        var plan = new OperationPlan(
            Guid.NewGuid().ToString("N"),
            target.Id,
            OperationKind.WriteTag,
            planned,
            stateHash,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            DateTimeOffset.UtcNow.Add(ttl),
            RequiresHumanApproval: !target.IsSimulation,
            target.IsSimulation ? "Simulation write; no physical side effect." : "Physical write requires explicit approval.");
        _plans.Add(plan);
        return plan;
    }

    public async Task<ApplyWriteResult> ApplyWriteAsync(
        string planId,
        string approvalToken,
        CancellationToken cancellationToken = default)
    {
        if (!_plans.TryTake(planId, out var plan) || plan is null)
        {
            return Failure(planId, "Plan does not exist or has already been consumed.");
        }

        if (!CryptographicEquals(plan.ApprovalToken, approvalToken))
        {
            return Failure(planId, "Approval token is invalid.");
        }

        if (plan.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Failure(planId, "Plan has expired.");
        }

        var values = new List<TagValue>();
        try
        {
            var target = GetTarget(plan.TargetId);
            var tags = ResolveTags(target.Id, plan.Changes.Select(x => x.Tag));
            using var lease = await EnterTargetAsync(target.Id, cancellationToken).ConfigureAwait(false);
            var before = await _client.ReadAsync(target, tags, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(StateHasher.Compute(before), plan.StateHash, StringComparison.Ordinal))
            {
                return Failure(planId, "Target state changed since the plan was created.");
            }

            foreach (var change in plan.Changes)
            {
                var tag = tags.Single(x => string.Equals(x.Name, change.Tag, StringComparison.OrdinalIgnoreCase));
                if (!_policy.CanWrite(target, tag, change.NewValue!, out var rejection))
                    throw new InvalidOperationException($"Write rejected for '{tag.Name}': {rejection}");
                await _audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "write_intent", plan.TargetId,
                    plan.PlanId, tag.Name, change.CurrentValue, change.NewValue, false, null), cancellationToken);
                var value = await _client.WriteAsync(target, tag, change.NewValue!, cancellationToken).ConfigureAwait(false);
                values.Add(value);
                await _audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "write_tag", plan.TargetId,
                    plan.PlanId, tag.Name, change.CurrentValue, change.NewValue, true, null), cancellationToken);
            }

            return new ApplyWriteResult(planId, true, values, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            await _audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "write_cancelled", plan.TargetId,
                plan.PlanId, null, null, null, false, $"Cancelled after {values.Count} completed writes; plan consumed."), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var message = $"{ex.Message} Completed writes: {values.Count}/{plan.Changes.Count}. No automatic rollback was attempted.";
            await _audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "write_failed", plan.TargetId,
                plan.PlanId, null, null, null, false, message), CancellationToken.None);
            return new ApplyWriteResult(planId, false, values.AsReadOnly(), message, DateTimeOffset.UtcNow);
        }
    }

    private IReadOnlyDictionary<string, TagDefinition> ResolveTagMapping(string targetId, IEnumerable<string> names)
    {
        var target = GetTarget(targetId);
        if (!_manifests.TryGetValue(target.Id, out var manifest))
        {
            throw new KeyNotFoundException($"No tag manifest is registered for '{target.Id}'.");
        }

        var lookup = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in manifest)
            foreach (var name in tag.AllNames)
                if (string.IsNullOrWhiteSpace(name) || !lookup.TryAdd(name, tag))
                    throw new ArgumentException("Tag names and aliases must be unique within a target.");
        var result = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!lookup.TryGetValue(name, out var tag))
            {
                throw new KeyNotFoundException($"Unknown tag '{name}' for target '{target.Id}'.");
            }

            result[name] = tag;
        }

        return result;
    }

    private IReadOnlyList<TagDefinition> ResolveTags(string targetId, IEnumerable<string> names)
    {
        var mapping = ResolveTagMapping(targetId, names);
        return mapping.Values.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IDisposable> EnterTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        var target = GetTarget(targetId);
        var gate = _targetGates.GetOrAdd(target.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateLease(gate);
    }

    private static ApplyWriteResult Failure(string planId, string message) =>
        new(planId, false, [], message, DateTimeOffset.UtcNow);

    private static bool CryptographicEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual ?? string.Empty);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
