using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Core.Catalog;
using PlcMcp.Runtime.Policy;

namespace PlcMcp.Tests;

public class SafetyPolicyTests
{
    private readonly TargetProfile _simulationTarget;
    private readonly TargetProfile _physicalTarget;

    public SafetyPolicyTests()
    {
        _simulationTarget = VendorCatalog.CreateDefaultTargets().First(t => t.Id == "sim-siemens");
        _physicalTarget = new TargetProfile(
            "real-siemens",
            PlcVendor.Siemens,
            "S7-1500",
            "1516-3 PN/DP",
            "V2.9",
            new EndpointProfile("192.0.2.10", 102, TransportKind.S7Comm, 0, 1),
            [TransportKind.S7Comm],
            null,
            _simulationTarget.Capabilities,
            "default",
            IsSimulation: false);
    }

    [Fact]
    public void CanWrite_WhenTagIsNotWritable_RejectsWrite()
    {
        var policy = new SafetyPolicy("test-policy", writableTags: ["RunMode", "MotorOutput"]);
        var nonWritableTag = new TagDefinition(
            Name: "RunMode",
            NativeAddress: "M0.0",
            DataType: PlcDataType.Bool,
            CanWrite: false,
            SafetyClass: SafetyClass.ReadOnly);

        var allowed = policy.CanWrite(_simulationTarget, nonWritableTag, true, out var reason);

        Assert.False(allowed);
        Assert.Contains("Tag is not writable by its manifest", reason);
    }

    [Theory]
    [InlineData(SafetyClass.Actuator)]
    [InlineData(SafetyClass.Safety)]
    public void CanWrite_DangerousActuatorOrSafetyTag_RejectsWriteEvenIfCanWriteIsTrue(SafetyClass dangerousClass)
    {
        var policy = new SafetyPolicy("test-policy", writableTags: ["MotorOutput", "EmergencyStop"]);
        var dangerousTag = new TagDefinition(
            Name: dangerousClass == SafetyClass.Actuator ? "MotorOutput" : "EmergencyStop",
            NativeAddress: "Q0.0",
            DataType: PlcDataType.Bool,
            CanWrite: true,
            SafetyClass: dangerousClass);

        var allowed = policy.CanWrite(_simulationTarget, dangerousTag, true, out var reason);

        Assert.False(allowed);
        Assert.Contains("Direct actuator and safety writes are disabled", reason);
    }

    [Fact]
    public void CanWrite_PhysicalTarget_RejectsWhenAllowSimulationWritesIsFalse()
    {
        var policy = new SafetyPolicy("test-policy", writableTags: ["PressureSetpoint"], allowSimulationWrites: false);
        var parameterTag = new TagDefinition(
            Name: "PressureSetpoint",
            NativeAddress: "VD310",
            DataType: PlcDataType.Real,
            CanWrite: true,
            SafetyClass: SafetyClass.Parameter,
            Minimum: 0,
            Maximum: 10);

        var allowed = policy.CanWrite(_physicalTarget, parameterTag, 5.0, out var reason);

        Assert.False(allowed);
        Assert.Contains("Physical writes are disabled in the starter build", reason);
    }

    [Fact]
    public void CanWrite_TagNotInAllowlist_RejectsWrite()
    {
        var policy = new SafetyPolicy("test-policy", writableTags: ["OtherTag"]);
        var parameterTag = new TagDefinition(
            Name: "PressureSetpoint",
            NativeAddress: "VD310",
            DataType: PlcDataType.Real,
            CanWrite: true,
            SafetyClass: SafetyClass.Parameter,
            Minimum: 0,
            Maximum: 10);

        var allowed = policy.CanWrite(_simulationTarget, parameterTag, 5.0, out var reason);

        Assert.False(allowed);
        Assert.Contains("Tag is not present in the policy write allowlist", reason);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-10.0)]
    public void CanWrite_ValueBelowMinimum_RejectsWrite(double value)
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

        var allowed = policy.CanWrite(_simulationTarget, parameterTag, value, out var reason);

        Assert.False(allowed);
        Assert.Contains("below the minimum", reason);
    }

    [Theory]
    [InlineData(10.1)]
    [InlineData(50.0)]
    public void CanWrite_ValueAboveMaximum_RejectsWrite(double value)
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

        var allowed = policy.CanWrite(_simulationTarget, parameterTag, value, out var reason);

        Assert.False(allowed);
        Assert.Contains("above the maximum", reason);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(5.5)]
    [InlineData(10.0)]
    public void CanWrite_ValueWithinRange_AllowsWrite(double value)
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

        var allowed = policy.CanWrite(_simulationTarget, parameterTag, value, out var reason);

        Assert.True(allowed);
        Assert.Empty(reason);
    }

    [Fact]
    public void CanWrite_JsonElementNumber_ValidatesRangeCorrectly()
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

        using var validDoc = JsonDocument.Parse("4.5");
        Assert.True(policy.CanWrite(_simulationTarget, parameterTag, validDoc.RootElement, out _));

        using var outOfRangeDoc = JsonDocument.Parse("15.2");
        Assert.False(policy.CanWrite(_simulationTarget, parameterTag, outOfRangeDoc.RootElement, out var reason));
        Assert.Contains("above the maximum", reason);
    }

    [Fact]
    public void CanWrite_NonNumericValues_PassRangeCheck()
    {
        var policy = new SafetyPolicy("test-policy", writableTags: ["HostReady"]);
        var handshakeTag = new TagDefinition(
            Name: "HostReady",
            NativeAddress: "M0.5",
            DataType: PlcDataType.Bool,
            CanWrite: true,
            SafetyClass: SafetyClass.Handshake);

        var allowed = policy.CanWrite(_simulationTarget, handshakeTag, true, out var reason);

        Assert.True(allowed);
        Assert.Empty(reason);
    }
}
