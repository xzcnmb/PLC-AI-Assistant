using PlcMcp.Adapters;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ExampleConfiguration_LoadsAsReadOnlyTargets()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "profiles", "readonly.example.json");
        Assert.True(File.Exists(path), $"Missing example configuration: {path}");
        var host = ConfiguredPlcComposition.Load(path);
        Assert.Equal(4, host.Service.ListTargets().Count);
        Assert.All(host.Service.ListTargets(), target =>
        {
            Assert.False(target.IsSimulation);
            Assert.Equal(CapabilityStatus.Experimental, target.Capabilities.GetStatus("read_tags"));
            Assert.Equal(CapabilityStatus.Unsupported, target.Capabilities.GetStatus("write_tags"));
        });
    }

    [Fact]
    public void PhysicalConfiguration_RejectsWritableTagAndBadEndpointFields()
    {
        var target = new ConfiguredTarget("bad", PlcVendor.Inovance, "H5U",
            new("127.0.0.1", 502, TransportKind.ModbusTcp, Unit: 0),
            [new("x", "HR1", PlcDataType.Int16, CanWrite: true)]);
        Assert.Throws<ArgumentException>(() => ConfiguredPlcComposition.Create(new(1, [target])));
    }
}
