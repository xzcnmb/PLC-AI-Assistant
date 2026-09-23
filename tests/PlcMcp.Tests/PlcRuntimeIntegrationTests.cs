using PlcMcp.Adapters;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Tests;

public class PlcRuntimeIntegrationTests
{
    [Fact]
    public void Composition_InitializesSuccessfullyWithFourTargets()
    {
        var host = DefaultPlcComposition.Create();
        var targets = host.Service.ListTargets();

        Assert.Equal(4, targets.Count);
        Assert.Contains(targets, t => t.Id == "sim-siemens");
        Assert.Contains(targets, t => t.Id == "sim-omron");
        Assert.Contains(targets, t => t.Id == "sim-mitsubishi");
        Assert.Contains(targets, t => t.Id == "sim-inovance");
    }

    [Theory]
    [InlineData("sim-siemens")]
    [InlineData("sim-omron")]
    [InlineData("sim-mitsubishi")]
    [InlineData("sim-inovance")]
    public async Task Service_CanProbeAndReadTargetTags(string targetId)
    {
        var host = DefaultPlcComposition.Create();
        var probe = await host.Service.ProbeAsync(targetId);

        Assert.True(probe.Reachable);
        Assert.True(probe.IsSimulation);

        var tags = host.Service.ListTags(targetId);
        Assert.NotEmpty(tags);

        var values = await host.Service.ReadAsync(targetId, ["RunMode", "PressureSetpoint", "PressureActual"]);
        Assert.Equal(3, values.Count);
        Assert.All(values, v => Assert.Equal(QualityCode.Simulated, v.Quality));
    }

    [Fact]
    public async Task Service_PlanAndApplyWrite_SucceedsForAllowedParameter()
    {
        var host = DefaultPlcComposition.Create();
        var targetId = "sim-siemens";

        var changes = new Dictionary<string, object?>
        {
            ["PressureSetpoint"] = 4.5
        };

        var plan = await host.Service.PlanWriteAsync(targetId, changes);
        Assert.NotNull(plan);
        Assert.Equal(targetId, plan.TargetId);
        Assert.NotEmpty(plan.PlanId);
        Assert.NotEmpty(plan.ApprovalToken);
        Assert.Single(plan.Changes);

        var applyResult = await host.Service.ApplyWriteAsync(plan.PlanId, plan.ApprovalToken);
        Assert.True(applyResult.Applied);
        Assert.Null(applyResult.Error);

        var readBack = await host.Service.ReadAsync(targetId, ["PressureSetpoint"]);
        Assert.Equal(4.5f, Convert.ToSingle(readBack.Single().Value));

        // Verify audit trail
        Assert.Contains(host.Audit.Entries, entry =>
            entry.TargetId == targetId &&
            entry.Action == "write_tag" &&
            entry.Tag == "PressureSetpoint" &&
            entry.Succeeded);
    }

    [Fact]
    public async Task Service_PlanWrite_RejectsActuator()
    {
        var host = DefaultPlcComposition.Create();
        var targetId = "sim-siemens";

        var changes = new Dictionary<string, object?>
        {
            ["MotorOutput"] = true
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.PlanWriteAsync(targetId, changes));

        Assert.Contains("Write rejected for 'MotorOutput'", ex.Message);
    }

    [Fact]
    public async Task Service_PlanWrite_RejectsOutOfRangeValue()
    {
        var host = DefaultPlcComposition.Create();
        var targetId = "sim-siemens";

        var changes = new Dictionary<string, object?>
        {
            ["PressureSetpoint"] = 15.0 // Maximum is 10
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.PlanWriteAsync(targetId, changes));

        Assert.Contains("above the maximum", ex.Message);
    }

    [Fact]
    public async Task Service_ApplyWrite_RejectsIfStateHashMismatch()
    {
        var host = DefaultPlcComposition.Create();
        var targetId = "sim-siemens";

        var plan = await host.Service.PlanWriteAsync(targetId, new Dictionary<string, object?>
        {
            ["PressureSetpoint"] = 5.0
        });

        // Concurrently change another state or update directly to invalidate state hash
        var concurrentPlan = await host.Service.PlanWriteAsync(targetId, new Dictionary<string, object?>
        {
            ["PressureSetpoint"] = 6.0
        });
        var appliedConcurrent = await host.Service.ApplyWriteAsync(concurrentPlan.PlanId, concurrentPlan.ApprovalToken);
        Assert.True(appliedConcurrent.Applied);

        // Now trying to apply the first plan should fail due to state change
        var staleResult = await host.Service.ApplyWriteAsync(plan.PlanId, plan.ApprovalToken);
        Assert.False(staleResult.Applied);
        Assert.Equal("Target state changed since the plan was created.", staleResult.Error);
    }
}
