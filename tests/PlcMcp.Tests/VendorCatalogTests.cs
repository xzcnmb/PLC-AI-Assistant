using PlcMcp.Contracts.Models;
using PlcMcp.Core.Catalog;

namespace PlcMcp.Tests;

public class VendorCatalogTests
{
    [Fact]
    public void CreateDefaultTargets_ContainsAllFourVendors()
    {
        var targets = VendorCatalog.CreateDefaultTargets();

        Assert.Equal(4, targets.Count);

        var vendors = targets.Select(t => t.Vendor).ToHashSet();
        Assert.Contains(PlcVendor.Siemens, vendors);
        Assert.Contains(PlcVendor.Omron, vendors);
        Assert.Contains(PlcVendor.Mitsubishi, vendors);
        Assert.Contains(PlcVendor.Inovance, vendors);
    }

    [Theory]
    [InlineData("sim-siemens", PlcVendor.Siemens, "S7-200 SMART")]
    [InlineData("sim-omron", PlcVendor.Omron, "CJ/NJ")]
    [InlineData("sim-mitsubishi", PlcVendor.Mitsubishi, "iQ-F/iQ-R")]
    [InlineData("sim-inovance", PlcVendor.Inovance, "H5U/AM")]
    public void CreateDefaultTargets_SimulatedTargetsAreProperlyConfigured(string expectedId, PlcVendor expectedVendor, string expectedFamily)
    {
        var targets = VendorCatalog.CreateDefaultTargets();
        var target = targets.FirstOrDefault(t => t.Id == expectedId);

        Assert.NotNull(target);
        Assert.Equal(expectedVendor, target.Vendor);
        Assert.Equal(expectedFamily, target.Family);
        Assert.True(target.IsSimulation);
        Assert.Equal(TransportKind.Simulation, target.Endpoint.Transport);
        Assert.Contains(TransportKind.Simulation, target.RuntimeProtocols);
        Assert.NotNull(target.Capabilities);
    }

    [Fact]
    public void SimulationCapabilities_ExpectedCapabilitiesStatus()
    {
        var targets = VendorCatalog.CreateDefaultTargets();

        foreach (var target in targets)
        {
            var capabilities = target.Capabilities;

            // Supported capabilities in simulation MVP
            Assert.True(capabilities.Supports("read_tags"));
            Assert.True(capabilities.Supports("write_tags"));
            Assert.True(capabilities.Supports("browse_symbols"));
            Assert.True(capabilities.Supports("diagnostics"));

            Assert.Equal(CapabilityStatus.Supported, capabilities.GetStatus("read_tags"));
            Assert.Equal(CapabilityStatus.Supported, capabilities.GetStatus("write_tags"));
            Assert.Equal(CapabilityStatus.Supported, capabilities.GetStatus("browse_symbols"));
            Assert.Equal(CapabilityStatus.Supported, capabilities.GetStatus("diagnostics"));

            // Unsupported capabilities in simulation MVP
            Assert.False(capabilities.Supports("parse_program"));
            Assert.False(capabilities.Supports("edit_program"));
            Assert.False(capabilities.Supports("compile"));
            Assert.False(capabilities.Supports("download"));
            Assert.False(capabilities.Supports("set_run_mode"));
            Assert.False(capabilities.Supports("force_io"));
            Assert.False(capabilities.Supports("subscribe"));

            Assert.Equal(CapabilityStatus.Unsupported, capabilities.GetStatus("download"));
            Assert.Equal(CapabilityStatus.Unsupported, capabilities.GetStatus("force_io"));
            Assert.Equal(CapabilityStatus.Unsupported, capabilities.GetStatus("unknown_capability"));
            Assert.Equal([TransportKind.Simulation], target.RuntimeProtocols);
        }
    }

    [Fact]
    public void Protocols_ContainsStandardVendorProtocols()
    {
        var protocols = VendorCatalog.Protocols;

        Assert.Contains(protocols, p => p.Name == "s7comm" && p.Vendor == PlcVendor.Siemens && p.DefaultPort == 102);
        Assert.Contains(protocols, p => p.Name == "fins" && p.Vendor == PlcVendor.Omron && p.DefaultPort == 9600);
        Assert.Contains(protocols, p => p.Name == "slmp" && p.Vendor == PlcVendor.Mitsubishi && p.DefaultPort == 5000);
        Assert.Contains(protocols, p => p.Name == "modbus-tcp" && p.Vendor == PlcVendor.Inovance && p.DefaultPort == 502);
        Assert.Contains(protocols, p => p.Name == "opcua" && p.Vendor == PlcVendor.Generic && p.DefaultPort == 4840 && !p.SupportsRead);
        Assert.Contains(protocols, p => p.Name == "simulation" && p.Vendor == PlcVendor.Generic);

        // Production protocols are read-only in catalog description
        var s7 = protocols.First(p => p.Name == "s7comm");
        Assert.True(s7.SupportsRead);
        Assert.False(s7.SupportsWrite);

        var sim = protocols.First(p => p.Name == "simulation");
        Assert.True(sim.SupportsRead);
        Assert.True(sim.SupportsWrite);
    }
}
