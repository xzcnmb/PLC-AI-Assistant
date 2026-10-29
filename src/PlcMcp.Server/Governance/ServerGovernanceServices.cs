using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Analyzers;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Plcopen;
using PlcMcp.Engineering.Workspace;
using PlcMcp.Engineering.Workers.Siemens;
using PlcMcp.Engineering.Workers.AutoShop;
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

    /// <summary>
    /// AutoShop engineering bridge services; non-null only when explicitly enabled via
    /// --autoshop-bridge. When null, the AutoShop MCP tools are not registered at all.
    /// </summary>
    public AutoShopBridgeServices? AutoShopBridge { get; }

    /// <summary>
    /// Local AutoShop H5U simulator daemon management (h5u_daemon.exe as a Windows process only);
    /// non-null together with <see cref="AutoShopBridge"/> behind the same --autoshop-bridge switch.
    /// Simulator start/stop are local process operations, not PLC actions.
    /// </summary>
    public AutoShopSimulatorServices? AutoShopSimulator { get; }

    public IEngineeringWorker? AutoShopWorker { get; }

    /// <summary>
    /// E4/G3 inject-bridge MCP tools (open project / compile / generate project /
    /// in-process monitor read / axis configuration / simulator download / simulator
    /// run-stop); non-null together with <see cref="AutoShopBridge"/> behind the same
    /// --autoshop-bridge switch. Starts/journals/verifies its own AutoShop instances
    /// only; element write and force are not exposed, and download/run-stop are
    /// loopback-simulator-only by default (<see cref="AllowPhysicalDownload"/>).
    /// </summary>
    public AutoShopInjectTools? AutoShopInject { get; }

    /// <summary>
    /// Loopback-first Modbus TCP write executor for plc_autoshop_write_register;
    /// non-null together with <see cref="AutoShopBridge"/>. By default only
    /// simulation targets on 127.0.0.0/8 hosts are writable; physical targets
    /// require <see cref="AllowPhysicalWrite"/> (--autoshop-allow-physical-write,
    /// default OFF).
    /// </summary>
    public AutoShopWriteRegisterService? AutoShopWriteRegister { get; }

    /// <summary>
    /// Operator switch for physical writes (default OFF): when false, only
    /// simulation targets on loopback hosts are writable by
    /// <see cref="AutoShopWriteRegister"/>; when true, physical targets may be
    /// written (still requires operator approval per contracts.md).
    /// </summary>
    public bool AllowPhysicalWrite { get; }

    /// <summary>
    /// Operator switch for physical PLC download / run-stop (default OFF,
    /// --autoshop-allow-physical-download): when false, plc_autoshop_download and
    /// plc_autoshop_run_stop accept loopback (127.0.0.1) simulator targets only;
    /// when true they still cannot reach hardware — the D1 protocol clients keep a
    /// deep loopback red line at construction, so physical endpoints are refused
    /// there as well (physical download/run stays Experimental(真机待验) until a
    /// verified hardware path exists). Enabling the switch requires operator
    /// approval per contracts.md.
    /// </summary>
    public bool AllowPhysicalDownload { get; }

    public ServerGovernanceServices(string dataRoot, string? smartProjectRoot = null, AutoShopBridgeConfig? autoShopBridgeConfig = null, AutoShopEnvironmentDetector? autoShopDetector = null, AutoShopSimulatorOptions? autoShopSimulatorOptions = null, IAutoShopSimulatorProcessDriver? autoShopSimulatorDriver = null, AutoShopInjectToolsOptions? autoShopInjectOptions = null, bool allowPhysicalWrite = false, bool allowPhysicalDownload = false)
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

        if (autoShopBridgeConfig is not null)
        {
            // Resolve the ITP endpoint from registry evidence when available (port is persisted by
            // the service under HKLM\SOFTWARE\WOW6432Node\AutoServicePort); otherwise keep the configured default.
            var detector = autoShopDetector ?? new AutoShopEnvironmentDetector();
            AutoShopEnvironmentReport environment = detector.Detect();
            var bridgeConfig = new AutoShopBridgeConfig
            {
                Host = autoShopBridgeConfig.Host,
                Port = environment.RegistryPort ?? autoShopBridgeConfig.Port,
                RegisterPath = autoShopBridgeConfig.RegisterPath,
                Timeout = autoShopBridgeConfig.Timeout,
                RequireLoopback = autoShopBridgeConfig.RequireLoopback
            };
            AutoShopBridge = new AutoShopBridgeServices(bridgeConfig, detector);
            AutoShopWorker = new AutoShopBridgeWorker(AutoShopBridge);
            AutoShopSimulator = new AutoShopSimulatorServices(
                Path.Combine(root, "autoshop-simulator"),
                autoShopSimulatorOptions,
                autoShopSimulatorDriver);
            // E4 inject-bridge tools share the same explicit switch; construction is
            // offline (options only) — discovery/launch happens per tool call.
            AutoShopInject = new AutoShopInjectTools(
                Path.Combine(root, "autoshop-inject"),
                autoShopInjectOptions);
            AutoShopWriteRegister = new AutoShopWriteRegisterService(
                Path.Combine(root, "autoshop-write", "autoshop-write-register.log"));
            AllowPhysicalWrite = allowPhysicalWrite;
            AllowPhysicalDownload = allowPhysicalDownload;
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
        var autoShopReady = AutoShopBridge is not null && target.Vendor == PlcVendor.Inovance;
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_itp_bridge", autoShopReady ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            autoShopReady ? "AutoShop bridge explicitly enabled (--autoshop-bridge); read-only local environment evidence plus ITP register handshake against InoVarSharedService. Reverse-engineered interface, no vendor endorsement, version-locked to AutoShop 4.12.0.2."
                : "AutoShop bridge not enabled; pass --autoshop-bridge to opt in. An installed AutoShop alone never proves license, project/CPU compatibility or deployment support."));
        var autoShopInjectReady = AutoShopInject is not null && target.Vendor == PlcVendor.Inovance;
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_inject_bridge_open", autoShopReady ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            autoShopReady ? "plc_autoshop_open_project starts a self-launched, journaled AutoShop instance with the inject bridge and opens a locally constructed project (E3 method, verified on this host for template-based projects); local IDE instance only, no PLC connection."
                : "AutoShop bridge not enabled; pass --autoshop-bridge to opt in."));
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_inject_bridge_compile", autoShopReady ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            autoShopReady ? "plc_autoshop_compile runs the F1-verified in-process compile-all chain (root 0x1581A70 -> cmpMgr 0x1581CA0 -> entry 0x15A2EC0, project gate 0x161B620 checked first) inside the owned AutoShop instance and polls/decrypts the Compile artifacts; offline IDE behaviour, no PLC action; verified on this host against template-based projects only."
                : "AutoShop bridge not enabled; pass --autoshop-bridge to opt in."));
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_inject_bridge_generate", autoShopReady ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            autoShopReady ? "plc_autoshop_generate_project constructs an openable project offline (UCodeCrypt .hcp + declared files, E3 construction, offline round-trip verified); whether AutoShop accepts generated file contents is unverified."
                : "AutoShop bridge not enabled; pass --autoshop-bridge to opt in."));
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_inject_bridge_monitor_read", autoShopReady ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            autoShopReady ? "plc_autoshop_monitor_read performs an in-process vendor read inside the owned AutoShop instance (CSoftAddrMap filled via tagSoftAddr ctor + CMap::SetAt, ReadElemValue accepted rc=0, dual-slot polling: tag+0x0C CString delivery slot + tag+0x20 double mirror, with a delivered=N discriminator). The raw-HWND fault is explained (CWnd* vs HWND misuse); the write-back blocker is the in-process session establishment, not reverse-engineered, so the current backend always reports delivered=0: values are call-level evidence, never PLC data."
                : "AutoShop bridge not enabled; pass --autoshop-bridge to opt in."));
        var autoshopSimLoopbackReady = autoShopInjectReady;
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_download_transfer", autoshopSimLoopbackReady ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            autoshopSimLoopbackReady ? "plc_autoshop_download transfers the repackaged project.hcpp (PPCH container) into the LOOPBACK H5U simulator workspace over the D1 12939 native protocol (G2 byte-exact landing evidence); a download is a PLC action - local simulator demo only, physical targets require --autoshop-allow-physical-download (default OFF) plus operator approval and remain Experimental(真机待验) behind the protocol clients' deep loopback red line."
                : "AutoShop bridge not enabled; pass --autoshop-bridge to opt in."));
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_run_stop", autoshopSimLoopbackReady ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            autoshopSimLoopbackReady ? "plc_autoshop_run_stop writes the 0xFF000020 RUN/STOP control word to the loopback simulator register chain (G2 evidence: RUN acked [06] + Modbus D100 0->4660 registers alive; STOP acks, values retained). Local simulator demo only; hardware CPU RUN/STOP remains Unsupported (path not reversed)."
                : "AutoShop bridge not enabled; pass --autoshop-bridge to opt in."));
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_program_load", CapabilityStatus.Unsupported,
            "The simulator load_down_file_start is a stub: the load trigger (0xFF000010<-1) answers daemon status 0x82 and downloaded files are NOT activated as the running program (G2 + D1 evidence); hardware load is unverified."));
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_download_via_vendor_export", CapabilityStatus.Unsupported,
            "The in-process vendor CCommunicateEth::Download export is a proven dead automation channel (G2: rc=0 accepted, zero wire frames; DownloadPlcSoft standalone crashes); the bridge gate defaults closed and the export is never invoked."));
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_inject_bridge_axis_config", autoShopReady ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported,
            autoShopReady ? "plc_autoshop_axis_config reads/edits the xml\\axis.xml member of a .hcpp package (F2 pipeline, offline round-trip verified) and can re-open the project; structure-level only - the axis schema is reverse-engineered and vendor consumption of edited values is unverified."
                : "AutoShop bridge not enabled; pass --autoshop-bridge to opt in."));
        capabilities.Add(new CapabilityDescriptor(
            "autoshop_write_register", CapabilityStatus.Experimental,
            autoShopReady ? "plc_autoshop_write_register writes FC5/FC6 Modbus TCP frames to a configured Inovance target with readback; by default ONLY simulation targets on 127.0.0.0/8 hosts are writable (local H5U simulator); physical targets require --autoshop-allow-physical-write (default OFF) plus operator approval. Mapping table is an unverified documentation default."
                : "AutoShop bridge not enabled; pass --autoshop-bridge to opt in."));
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
