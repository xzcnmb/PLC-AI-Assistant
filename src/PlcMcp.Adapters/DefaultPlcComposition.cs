using PlcMcp.Contracts.Models;
using PlcMcp.Core.Catalog;
using PlcMcp.Runtime;
using PlcMcp.Runtime.Clients;
using PlcMcp.Runtime.Policy;

namespace PlcMcp.Adapters;

public sealed record PlcRuntimeHost(PlcRuntimeService Service, InMemoryAuditSink Audit);

public static class DefaultPlcComposition
{
    public static PlcRuntimeHost Create()
    {
        var targets = VendorCatalog.CreateDefaultTargets();
        var manifests = targets.ToDictionary(x => x.Id, CreateManifest, StringComparer.OrdinalIgnoreCase);
        var seeds = manifests.SelectMany(pair => pair.Value.Select(tag =>
            (TargetId: pair.Key, Tag: tag, InitialValue: (object?)InitialValueFor(tag))));
        var client = new InMemoryPlcRuntimeClient(seeds);
        var audit = new InMemoryAuditSink();
        var service = new PlcRuntimeService(
            targets,
            manifests,
            client,
            new SafetyPolicy("default", manifests.Values.SelectMany(x => x)
                .Where(x => x.CanWrite && x.SafetyClass is SafetyClass.Parameter or SafetyClass.Handshake)
                .Select(x => x.Name)),
            new PlanStore(),
            audit);
        return new PlcRuntimeHost(service, audit);
    }

    private static IReadOnlyList<TagDefinition> CreateManifest(TargetProfile target) =>
    [
        new("RunMode", NativeAddress(target, "RUN"), PlcDataType.Bool,
            Description: "Controller mode indicator; read-only."),
        new("AlarmActive", NativeAddress(target, "ALARM"), PlcDataType.Bool,
            Description: "Simulated aggregate alarm indicator."),
        new("HostReady", NativeAddress(target, "HOST_READY"), PlcDataType.Bool,
            CanWrite: true, SafetyClass: SafetyClass.Handshake,
            Aliases: ["上位机就绪"], Description: "Coordination handshake; not a safety circuit."),
        new("Heartbeat", NativeAddress(target, "HEARTBEAT"), PlcDataType.Bool,
            CanWrite: true, SafetyClass: SafetyClass.Handshake,
            Aliases: ["心跳"], Description: "Optional host heartbeat template."),
        new("PressureSetpoint", NativeAddress(target, "PRESSURE_SETPOINT"), PlcDataType.Real,
            Unit: "bar", Minimum: 0, Maximum: 10, CanWrite: true, SafetyClass: SafetyClass.Parameter,
            Aliases: ["目标压力"], Description: "Simulation parameter with an explicit engineering range."),
        new("CycleTimeMs", NativeAddress(target, "CYCLE_TIME_MS"), PlcDataType.Int32,
            Unit: "ms", Minimum: 1, Maximum: 60000, CanWrite: true, SafetyClass: SafetyClass.Parameter,
            Aliases: ["周期时间"], Description: "Simulation cycle-time parameter."),
        new("MotorOutput", NativeAddress(target, "MOTOR_OUTPUT"), PlcDataType.Bool,
            CanWrite: false, SafetyClass: SafetyClass.Actuator,
            Description: "Direct actuator output is intentionally read-only in the MVP."),
        new("PressureActual", NativeAddress(target, "PRESSURE_ACTUAL"), PlcDataType.Real,
            Unit: "bar", Minimum: 0, Maximum: 10,
            Description: "Simulated measured pressure.")
    ];

    private static object InitialValueFor(TagDefinition tag) => tag.DataType switch
    {
        PlcDataType.Bool => tag.Name switch
        {
            "RunMode" => true,
            "AlarmActive" => false,
            "MotorOutput" => false,
            _ => false
        },
        PlcDataType.Real => tag.Name == "PressureActual" ? 0.0f : 2.5f,
        PlcDataType.Int32 => 1000,
        _ => string.Empty
    };

    private static string NativeAddress(TargetProfile target, string logical)
    {
        return target.Vendor switch
        {
            PlcVendor.Siemens => logical switch
            {
                "RUN" => "M0.0",
                "ALARM" => "M2.0",
                "HOST_READY" => "M0.5",
                "HEARTBEAT" => "M0.6",
                "PRESSURE_SETPOINT" => "VD310",
                "CYCLE_TIME_MS" => "VD130",
                "MOTOR_OUTPUT" => "Q0.0",
                _ => "VD200"
            },
            PlcVendor.Omron => logical switch
            {
                "RUN" => "CIO0.00",
                "ALARM" => "CIO2.00",
                "HOST_READY" => "W0.05",
                "HEARTBEAT" => "W0.06",
                "PRESSURE_SETPOINT" => "D310",
                "CYCLE_TIME_MS" => "D130",
                "MOTOR_OUTPUT" => "CIO100.00",
                _ => "D200"
            },
            PlcVendor.Mitsubishi => logical switch
            {
                "RUN" => "M0",
                "ALARM" => "M20",
                "HOST_READY" => "M5",
                "HEARTBEAT" => "M6",
                "PRESSURE_SETPOINT" => "D310",
                "CYCLE_TIME_MS" => "D130",
                "MOTOR_OUTPUT" => "Y0",
                _ => "D200"
            },
            PlcVendor.Inovance => logical switch
            {
                "RUN" => "M0",
                "ALARM" => "M20",
                "HOST_READY" => "M5",
                "HEARTBEAT" => "M6",
                "PRESSURE_SETPOINT" => "MW310",
                "CYCLE_TIME_MS" => "MD130",
                "MOTOR_OUTPUT" => "Y0",
                _ => "MD200"
            },
            _ => logical
        };
    }
}
