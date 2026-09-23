using PlcMcp.Contracts.Models;

namespace PlcMcp.Core.Catalog;

public static class VendorCatalog
{
    public static IReadOnlyList<ProtocolDescriptor> Protocols { get; } =
    [
        new("s7comm", PlcVendor.Siemens, 102, "DB/M/I/Q byte and bit addresses", true, false,
            "Read-only S7comm; TIA Openness applies to supported TIA families, Micro/WIN SMART is separate. No engineering worker is installed."),
        new("fins", PlcVendor.Omron, 9600, "CIO/W/H/A/DM word addresses", true, false,
            "Runtime FINS profile for CP/CJ/CS families; NJ/NX may use CIP or OPC UA."),
        new("slmp", PlcVendor.Mitsubishi, 5000, "MC device addresses such as D/M/X/Y", true, false,
            "Runtime MC Protocol/SLMP profile; GX Works engineering bridge is experimental."),
        new("modbus-tcp", PlcVendor.Inovance, 502, "0-based coils and holding registers", true, false,
            "Runtime Modbus profile; word order must be explicit for 32-bit values."),
        new("opcua", PlcVendor.Generic, 4840, "OPC UA NodeId", false, false,
            "Planned transport only; no OPC UA client is installed in this build."),
        new("simulation", PlcVendor.Generic, 0, "semantic tag names", true, true,
            "In-memory simulator for tests and offline development only.")
    ];

    public static IReadOnlyList<TargetProfile> CreateDefaultTargets() =>
    [
        CreateSiemensSimulation(),
        CreateOmronSimulation(),
        CreateMitsubishiSimulation(),
        CreateInovanceSimulation()
    ];

    private static TargetProfile CreateSiemensSimulation() => new(
        "sim-siemens", PlcVendor.Siemens, "S7-200 SMART", "simulation", null,
        new("[IP]", 0, TransportKind.Simulation, 0, 1),
        [TransportKind.Simulation], null,
        SimulationCapabilities("S7-200 SMART simulation"), "default", true);

    private static TargetProfile CreateOmronSimulation() => new(
        "sim-omron", PlcVendor.Omron, "CJ/NJ", "simulation", null,
        new("[IP]", 0, TransportKind.Simulation, Unit: 0),
        [TransportKind.Simulation], null,
        SimulationCapabilities("Omron runtime simulation"), "default", true);

    private static TargetProfile CreateMitsubishiSimulation() => new(
        "sim-mitsubishi", PlcVendor.Mitsubishi, "iQ-F/iQ-R", "simulation", null,
        new("[IP]", 0, TransportKind.Simulation),
        [TransportKind.Simulation], null,
        SimulationCapabilities("Mitsubishi runtime simulation"), "default", true);

    private static TargetProfile CreateInovanceSimulation() => new(
        "sim-inovance", PlcVendor.Inovance, "H5U/AM", "simulation", null,
        new("[IP]", 0, TransportKind.Simulation),
        [TransportKind.Simulation], null,
        SimulationCapabilities("Inovance runtime simulation"), "default", true);

    private static CapabilitySet SimulationCapabilities(string note) => new(
    [
        new("read_tags", CapabilityStatus.Supported, note),
        new("write_tags", CapabilityStatus.Supported, "Simulator only; production adapters remain read-only in MVP."),
        new("browse_symbols", CapabilityStatus.Supported, "Manifest-backed symbols."),
        new("diagnostics", CapabilityStatus.Supported, "Connection and simulator diagnostics."),
        new("parse_program", CapabilityStatus.Unsupported, "Planned after program IR is defined."),
        new("edit_program", CapabilityStatus.Unsupported, "Requires vendor engineering backend."),
        new("compile", CapabilityStatus.Unsupported, "Requires vendor compiler or IEC toolchain."),
        new("download", CapabilityStatus.Unsupported, "Disabled in MVP."),
        new("set_run_mode", CapabilityStatus.Unsupported, "Disabled in MVP."),
        new("force_io", CapabilityStatus.Unsupported, "Disabled in MVP."),
        new("subscribe", CapabilityStatus.Unsupported, "Planned monitoring channel.")
    ]);
}
