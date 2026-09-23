using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Analyzers;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Plcopen;
using PlcMcp.Engineering.Workspace;
using PlcMcp.Engineering.Workers.Siemens;
using PlcMcp.Runtime.Governance;

namespace PlcMcp.Server.Governance;

public sealed class ServerGovernanceServices
{
    public IVendorDoctor Doctor { get; } = new VendorDoctor();
    public IProjectWorkspaceManager Workspace { get; }
    public IStPrecheckAnalyzer StAnalyzer { get; } = new StPrecheckAnalyzer();
    public IPlcopenXmlParser Plcopen { get; } = new PlcopenXmlParser();
    public IJobStateMachine Jobs { get; }
    public IAuditLog Audit { get; }
    public string DataRoot { get; }
    public string? SmartProjectRoot { get; }
    public IEngineeringWorker? SmartWorker { get; }

    public ServerGovernanceServices(string dataRoot, string? smartProjectRoot = null)
    {
        var root = Path.GetFullPath(dataRoot);
        DataRoot = root;
        SmartProjectRoot = string.IsNullOrWhiteSpace(smartProjectRoot) ? null : Path.GetFullPath(smartProjectRoot);
        if (SmartProjectRoot is not null && !Directory.Exists(SmartProjectRoot))
            throw new DirectoryNotFoundException($"SMART project root does not exist: {SmartProjectRoot}");
        Directory.CreateDirectory(root);
        Workspace = new ProjectWorkspaceManager(Path.Combine(root, "workspaces"));
        Jobs = new FileJobStateMachine(Path.Combine(root, "jobs"));
        Audit = new FileAuditLog(Path.Combine(root, "audit.jsonl"));
        if (SmartProjectRoot is not null)
        {
            var config = new SiemensSmartBridgeConfig(AllowedWorkspaceRoots: [SmartProjectRoot]);
            var worker = new SiemensSmartEngineeringWorker(Workspace, Doctor, config: config);
            if (worker.IsAvailable) SmartWorker = worker;
        }
    }

    public async Task<CapabilityReport> GetCapabilityReportAsync(TargetProfile target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capabilities = target.Capabilities.Items.ToList();
        capabilities.Add(new CapabilityDescriptor(
            "smart_offline_inspect",
            SmartWorker is not null && target.Vendor == PlcVendor.Siemens ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            SmartWorker is not null && target.Vendor == PlcVendor.Siemens
                ? "Explicit local project root configured; V2 offline overview only. This does not prove target CPU compatibility."
                : "SMART worker not configured for this target; local IDE detection alone is not a target capability."));
        return await Task.FromResult(new CapabilityReport(
            target.Id,
            "capability-report-v1",
            DateTimeOffset.UtcNow,
            capabilities,
            target.RuntimeProtocols,
            AtomicityScope.BestEffortNonAtomic,
            RollbackSupportKind.Unsupported,
            RequiresApprovalForPhysical: !target.IsSimulation,
            Notes: "Only listed target operations are executable. Use plc_doctor for separate host installation clues; an installed IDE never proves license, project/CPU compatibility or deployment support."));
    }
}
