using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Analyzers;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Plcopen;
using PlcMcp.Engineering.Workspace;
using PlcMcp.Engineering.Workers.Siemens;
using PlcMcp.Engineering.Workers.Codesys;
using PlcMcp.Engineering.Workers.Omron;
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
    public CodesysWorker? CodesysWorker { get; }
    public GxWorks3ProbeClient? GxWorks3Probe { get; }
    public OmronInstallationDoctor OmronDoctor { get; }
    public OmronExchangeService? OmronExchange { get; }

    public ServerGovernanceServices(
        string dataRoot,
        string? smartProjectRoot = null,
        CodesysWorkerConfig? codesysConfig = null,
        GxWorks3ProbeConfig? gxworks3Config = null,
        GxWorks3ProbeClient? gxworks3ProbeClient = null)
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
        OmronDoctor = new OmronInstallationDoctor();
        if (SmartProjectRoot is not null)
        {
            OmronExchange = new OmronExchangeService(SmartProjectRoot);
        }
        if (SmartProjectRoot is not null)
        {
            var config = new SiemensSmartBridgeConfig(AllowedWorkspaceRoots: [SmartProjectRoot]);
            var worker = new SiemensSmartEngineeringWorker(Workspace, Doctor, config: config);
            if (worker.IsAvailable) SmartWorker = worker;
        }

        if (codesysConfig is not null)
        {
            CodesysWorker = new CodesysWorker(codesysConfig, workspaceManager: Workspace);
        }

        if (gxworks3ProbeClient is not null)
        {
            GxWorks3Probe = gxworks3ProbeClient;
        }
        else if (gxworks3Config is not null)
        {
            GxWorks3Probe = new GxWorks3ProbeClient(gxworks3Config);
        }
    }

    public async Task<CapabilityReport> GetCapabilityReportAsync(TargetProfile target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capabilities = target.Capabilities.Items.ToList();
        var smartReady = SmartWorker is not null && target.Vendor == PlcVendor.Siemens;
        capabilities.Add(new CapabilityDescriptor(
            "smart_offline_inspect", smartReady ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            smartReady ? "Explicit local root configured; SMART V2 offline overview, independent of target CPU compatibility."
                : "SMART worker not configured for this target; local IDE detection alone is not a target capability."));
        capabilities.Add(new CapabilityDescriptor(
            "smart_engine_validate", smartReady ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            smartReady ? "On a protected workcopy, starts a separate MicroWIN instance to validate selected networks; requires interactive desktop and compatible project."
                : "SMART validation worker unavailable."));
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
