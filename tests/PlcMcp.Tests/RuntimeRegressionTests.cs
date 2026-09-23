using System.Text.Json;
using PlcMcp.Adapters;
using PlcMcp.Contracts.Models;
using PlcMcp.Core.Catalog;
using PlcMcp.Runtime.Policy;

namespace PlcMcp.Tests;

public class RuntimeRegressionTests
{
    private readonly TargetProfile _simulationTarget;

    public RuntimeRegressionTests()
    {
        _simulationTarget = VendorCatalog.CreateDefaultTargets().First(t => t.Id == "sim-siemens");
    }

    [Theory]
    [InlineData("999999")]
    [InlineData("-1.0")]
    [InlineData("10.5")]
    public void SafetyPolicy_NumericStringOutOfRange_IsRejected(string outOfRangeString)
    {
        var policy = new SafetyPolicy("test-policy", writableTags: ["PressureSetpoint"]);
        var parameterTag = new TagDefinition(
            Name: "PressureSetpoint",
            NativeAddress: "VD310",
            DataType: PlcDataType.Real,
            CanWrite: true,
            SafetyClass: SafetyClass.Parameter,
            Minimum: 0,
            Maximum: 10);

        var allowed = policy.CanWrite(_simulationTarget, parameterTag, outOfRangeString, out var reason);
        Assert.False(allowed);
        Assert.NotEmpty(reason);

        using var jsonDoc = JsonDocument.Parse($"\"{outOfRangeString}\"");
        var jsonAllowed = policy.CanWrite(_simulationTarget, parameterTag, jsonDoc.RootElement, out var jsonReason);
        Assert.False(jsonAllowed);
        Assert.NotEmpty(jsonReason);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("abc")]
    public void SafetyPolicy_InvalidNumericString_IsRejected(string invalidNumber)
    {
        var policy = new SafetyPolicy("test-policy", writableTags: ["PressureSetpoint"]);
        var parameterTag = new TagDefinition(
            Name: "PressureSetpoint",
            NativeAddress: "VD310",
            DataType: PlcDataType.Real,
            CanWrite: true,
            SafetyClass: SafetyClass.Parameter,
            Minimum: 0,
            Maximum: 10);

        var allowed = policy.CanWrite(_simulationTarget, parameterTag, invalidNumber, out var reason);
        Assert.False(allowed);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void SafetyPolicy_FloatToInteger_IsRejectedToPreventTruncation()
    {
        var policy = new SafetyPolicy("test-policy", writableTags: ["CycleTimeMs"]);
        var intTag = new TagDefinition(
            Name: "CycleTimeMs",
            NativeAddress: "VD130",
            DataType: PlcDataType.Int32,
            CanWrite: true,
            SafetyClass: SafetyClass.Parameter,
            Minimum: 1,
            Maximum: 60000);

        Assert.False(policy.CanWrite(_simulationTarget, intTag, "123.45", out var r1));
        Assert.NotEmpty(r1);

        Assert.False(policy.CanWrite(_simulationTarget, intTag, 123.45, out var r2));
        Assert.NotEmpty(r2);
    }

    [Fact]
    public void SafetyPolicy_BooleanAndStringTags_RemainValid()
    {
        var policy = new SafetyPolicy("test-policy", writableTags: ["HostReady"]);
        var boolTag = new TagDefinition(
            Name: "HostReady",
            NativeAddress: "M0.5",
            DataType: PlcDataType.Bool,
            CanWrite: true,
            SafetyClass: SafetyClass.Handshake);

        Assert.True(policy.CanWrite(_simulationTarget, boolTag, true, out _));
        Assert.True(policy.CanWrite(_simulationTarget, boolTag, false, out _));
        Assert.True(policy.CanWrite(_simulationTarget, boolTag, "true", out _));
        Assert.True(policy.CanWrite(_simulationTarget, boolTag, "false", out _));
        Assert.False(policy.CanWrite(_simulationTarget, boolTag, "not-a-bool", out _));
    }

    [Fact]
    public async Task PlanWriteAsync_ValidNumericString_ConvertsToTargetType()
    {
        var host = DefaultPlcComposition.Create();
        var targetId = "sim-siemens";

        var changes = new Dictionary<string, object?>
        {
            ["PressureSetpoint"] = "4.5"
        };

        var plan = await host.Service.PlanWriteAsync(targetId, changes);
        Assert.NotNull(plan);
        var change = Assert.Single(plan.Changes);
        Assert.Equal("PressureSetpoint", change.Tag);
        Assert.IsType<float>(change.NewValue);
        Assert.Equal(4.5f, (float)change.NewValue!);

        var apply = await host.Service.ApplyWriteAsync(plan.PlanId, plan.ApprovalToken);
        Assert.True(apply.Applied);
    }

    [Fact]
    public async Task PlanWriteAsync_ChineseAlias_PlansSuccessfullyWithCanonicalName()
    {
        var host = DefaultPlcComposition.Create();
        var targetId = "sim-siemens";

        var changes = new Dictionary<string, object?>
        {
            ["目标压力"] = "5.5"
        };

        var plan = await host.Service.PlanWriteAsync(targetId, changes);
        Assert.NotNull(plan);
        var change = Assert.Single(plan.Changes);
        Assert.Equal("PressureSetpoint", change.Tag);
        Assert.Equal(5.5f, Convert.ToSingle(change.NewValue));

        var apply = await host.Service.ApplyWriteAsync(plan.PlanId, plan.ApprovalToken);
        Assert.True(apply.Applied);

        var readBack = await host.Service.ReadAsync(targetId, ["目标压力"]);
        Assert.Equal(5.5f, Convert.ToSingle(readBack.Single().Value));
    }

    [Fact]
    public async Task PlanWriteAsync_CanonicalAndAliasDuplicate_IsRejected()
    {
        var host = DefaultPlcComposition.Create();
        var targetId = "sim-siemens";

        var changes = new Dictionary<string, object?>
        {
            ["PressureSetpoint"] = 4.0,
            ["目标压力"] = 5.0
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.PlanWriteAsync(targetId, changes));
        Assert.Contains("Duplicate change specified for canonical tag 'PressureSetpoint'", ex.Message);
    }

    [Fact]
    public async Task PlanWriteAsync_TargetIdCasing_ResolvesToCanonicalTarget()
    {
        var host = DefaultPlcComposition.Create();
        var mixedCaseTargetId = "SIM-SIEMENS";

        var changes = new Dictionary<string, object?>
        {
            ["目标压力"] = 6.0
        };

        var plan = await host.Service.PlanWriteAsync(mixedCaseTargetId, changes);
        Assert.Equal("sim-siemens", plan.TargetId);

        var apply = await host.Service.ApplyWriteAsync(plan.PlanId, plan.ApprovalToken);
        Assert.True(apply.Applied);
    }
}
