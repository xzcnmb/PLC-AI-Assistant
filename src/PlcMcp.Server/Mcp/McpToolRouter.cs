using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Adapters;
using PlcMcp.Contracts.Models;
using PlcMcp.Core.Catalog;
using PlcMcp.Runtime;
using PlcMcp.Runtime.Monitoring;
using PlcMcp.Server.Governance;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Hmi;
using PlcMcp.Engineering.Workers.AutoShop;
using PlcMcp.Engineering.Workers.Codesys;
using PlcMcp.Engineering.Workers.Omron;

namespace PlcMcp.Server.Mcp;

public sealed class McpToolRouter
{
    private readonly PlcRuntimeHost _host;
    private readonly PlcRuntimeService _service;
    private readonly ServerGovernanceServices _governance;
    private readonly MonitoringService _monitoring;
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object>>> _handlers;
    private readonly List<ToolDescriptor> _descriptors;

    public McpToolRouter(PlcRuntimeHost host, ServerGovernanceServices? governance = null)
    {
        _host = host;
        _service = host.Service;
        _monitoring = new MonitoringService(_service);
        _governance = governance ?? new ServerGovernanceServices(Path.Combine(AppContext.BaseDirectory, "data"));
        _handlers = new(StringComparer.OrdinalIgnoreCase);
        _descriptors = new();

        RegisterTools();
        if (_governance.SmartWorker is not null)
        {
            Register("plc_smart_inspect", "Inspects a Siemens SMART V2 project offline from a protected working copy.", true, false,
                new { type = "object", properties = new { projectPath = new { type = "string" } }, required = new[] { "projectPath" } }, HandleSmartInspectAsync);
            Register("plc_smart_validate", "Validates specified SMART POU networks using a separate local MicroWIN instance; no PLC connection.", true, false,
                new { type = "object", properties = new { projectPath = new { type = "string" }, blockNames = new { type = "array", items = new { type = "string" } } }, required = new[] { "projectPath", "blockNames" } }, HandleSmartValidateAsync);
        }
        if (_governance.AutoShopBridge is not null)
        {
            Register("plc_autoshop_inspect", "Inspects the local Inovance AutoShop installation and InoVarSharedService (read-only evidence: exe/version, service process, registry port, TCP listener, ITP register handshake). Reverse-engineered interface, no vendor endorsement.", true, false,
                new { type = "object", properties = new { }, required = Array.Empty<string>() }, HandleAutoShopInspectAsync);
            Register("plc_autoshop_read_shared_variables", "Attempts controlled read-only access to AutoShop shared variables via the ITP register handshake. Variable value decoding is Unsupported: the binary push layout is not verified and no publisher may be registered. Never writes, forces, downloads, compiles, or controls run/stop.", true, false,
                new
                {
                    type = "object",
                    properties = new
                    {
                        observationSeconds = new
                        {
                            type = "number",
                            minimum = 0.5,
                            maximum = 10,
                            description = "Bounded read-only window counting pushed ITP packets (default 2s)."
                        }
                    },
                    required = Array.Empty<string>()
                }, HandleAutoShopReadSharedVariablesAsync);
            Register("plc_autoshop_status", "Aggregates read-only AutoShop status evidence: local environment detection (installation, InoVarSharedService), ITP register handshake, H5U software simulator daemon discovery (h5u_daemon process plus loopback 127.0.0.1:502/12939 probes) and an honest capability matrix (Experimental/Unsupported with reasons). Reverse-engineered interface, no vendor endorsement, version-locked to AutoShop 4.12.0.2.", true, false,
                new { type = "object", properties = new { }, required = Array.Empty<string>() }, HandleAutoShopStatusAsync);
            Register("plc_autoshop_simulator", "Manages the local AutoShop H5U software simulator daemon (h5u_daemon.exe) as a local Windows process only. action=status is read-only discovery; action=start starts the daemon when none is running (duplicate-start protection, bounded start verification, on-disk log); action=stop stops only a simulator process previously started by this server (optional integer 'pid'; PID ownership protection, no bare taskkill). Never contacts a physical PLC, writes, forces, downloads, or compiles. Reverse-engineered interface, no vendor endorsement, version-locked to AutoShop 4.12.0.2.", false, false,
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new
                        {
                            type = "string",
                            @enum = new[] { "status", "start", "stop" },
                            description = "status = read-only daemon and endpoint discovery; start = start the local simulator daemon when not already running; stop = stop a simulator process previously started by this server."
                        },
                        pid = new
                        {
                            type = "integer",
                            description = "Optional simulator PID previously started by this server; required for stop when several owned processes exist."
                        }
                    },
                    required = new[] { "action" }
                }, HandleAutoShopSimulatorAsync);
        }
        if (_governance.AutoShopInject is not null)
        {
            Register("plc_autoshop_open_project", "Opens a local AutoShop project directory (E3 method): prepares a fresh .hcp (re-unpacked from the .hcpp source when present, because AutoShop decrypts the .hcp in place), starts a self-launched, journaled AutoShop instance with the inject bridge (AutoShop.exe <path>.hcp) and polls the window state; failure dialogs are classified into the reversed error states (property/version/filelist/open-failed/file-not-found/file-open) and the instance is stopped again. Local IDE instance only: no PLC connection, no writes. Reverse-engineered interface, no vendor endorsement, version-locked to AutoShop 4.12.0.2.", false, false,
                new
                {
                    type = "object",
                    properties = new
                    {
                        projectDir = new
                        {
                            type = "string",
                            description = "Absolute local directory containing a .hcp (or a .hcpp source to re-unpack a fresh .hcp from). Must not be inside the AutoShop installation directory."
                        }
                    },
                    required = new[] { "projectDir" }
                }, HandleAutoShopOpenProjectAsync);
            Register("plc_autoshop_compile", "Compiles the currently opened AutoShop project through the inject bridge: the bridge runs the F1-verified in-process compile-all chain inside the owned AutoShop instance (root 0x1581A70 -> cmpMgr 0x1581CA0 -> entry 0x15A2EC0, project gate 0x161B620 checked first; addresses configurable via --autoshop-compile-rva). The project must have been opened by plc_autoshop_open_project first. Polls the project's Compile directory for *.compile/*.foid artifacts and decrypts each .compile with the reversed UCodeCrypt decrypter. Experimental: offline IDE behaviour, no PLC action, verified against template-based projects on this host only; a bridge that answers Unsupported (old DLL / disabled surface / project-gate refusal) is reported verbatim without faking success. Never writes, forces, or downloads to a PLC. Reverse-engineered interface, no vendor endorsement, version-locked to AutoShop 4.12.0.2.", false, false,
                new { type = "object", properties = new { }, required = Array.Empty<string>() }, HandleAutoShopCompileAsync);
            Register("plc_autoshop_generate_project", "Generates an openable AutoShop project directory offline (E3 construction): builds the project XML (or unpacks a real .hcpp source byte-faithfully), encrypts it with the reversed UCodeCrypt encrypter into <name>.hcp, writes the declared files (full disk content including the 31-byte AutoShop header) and wraps them into a .hcpp container; the result is verified by an offline round trip. Experimental: whether AutoShop accepts generated file contents is unverified; open validation is the job of plc_autoshop_open_project. Local files only, no PLC actions. Reverse-engineered interface, no vendor endorsement, version-locked to AutoShop 4.12.0.2.", false, false,
                new
                {
                    type = "object",
                    properties = new
                    {
                        projectDir = new
                        {
                            type = "string",
                            description = "Absolute local directory to create the project in (created when missing; must not be inside the AutoShop installation directory)."
                        },
                        model = new
                        {
                            type = "string",
                            description = "Target model written to GCMModal (default H3U; H5U/H3U per the catalog)."
                        },
                        projectName = new
                        {
                            type = "string",
                            description = "Project name (1..64 chars; default: the projectDir leaf name)."
                        },
                        projectDescription = new { type = "string", description = "Optional ProjectDesc text." },
                        hardwareFile = new
                        {
                            type = "string",
                            description = "Optional HardwareFile value (default <model>.dll; only H3U.dll is verified from template evidence)."
                        },
                        sourceHcpp = new
                        {
                            type = "string",
                            description = "Optional absolute path of a real .hcpp to unpack byte-faithfully (reuses its main blob and file contents; e.g. a vendor template copy)."
                        },
                        files = new
                        {
                            type = "array",
                            description = "Optional additional declared files: [{name, text?, contentBase64?}]. Defaults declare MAIN.LD, VarList.gdt and Config.sdt with placeholder content (the .LD/.gdt internal format is not reverse-engineered).",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    name = new { type = "string" },
                                    text = new { type = "string" },
                                    contentBase64 = new { type = "string" }
                                },
                                required = new[] { "name" }
                            }
                        }
                    },
                    required = new[] { "projectDir" }
                }, HandleAutoShopGenerateProjectAsync);
            Register("plc_autoshop_monitor_read", "Read-only monitor through the injected AutoShop bridge: stores the loopback target (127.0.0.1, fixed port), returns the read_status call receipt and performs an IN-PROCESS vendor element read: the bridge fills the vendor CSoftAddrMap with the non-stub primitives (tagSoftAddr 5-arg ctor + CMap::SetAt), calls ReadElemValue inside the AutoShop process (accepted, rc=0) and polls BOTH value slots after a settle window — the tag+0x0C CString delivery slot the vendor write-back parser assigns and the tag+0x20 double mirror (type 1=bit / 4=word by address prefix, e.g. M0=bit, D100=word) — reporting a delivered=N discriminator. HONEST VERDICT (G1 value-path analysis, loopback-only): the write-back slot and callback path are fully located and the earlier raw-HWND fault is explained (CWnd* vs HWND misuse, not a UI-state gap), but the vendor value WRITE-BACK is NOT closed on the current backend — the remaining blocker is the in-process session establishment, which is not reverse-engineered (the injected process sends zero wire bytes). delivered=0 values are CALL-LEVEL evidence, never PLC data (a value of 0 may mean either 'register is 0' or 'no response'); delivered>0 values are vendor-stack delivered values, still Experimental and never hardware-verified. Requires a project opened by plc_autoshop_open_project (the injected instance). Reverse-engineered interface, no vendor endorsement, version-locked to AutoShop 4.12.0.2.", true, false,
                new
                {
                    type = "object",
                    properties = new
                    {
                        addresses = new
                        {
                            type = "string",
                            description = "Comma-separated vendor element addresses (e.g. 'D100,M0'); D/R/T/C read as word, M/X/Y/S as bit."
                        }
                    },
                    required = new[] { "addresses" }
                }, HandleAutoShopMonitorReadAsync);
            Register("plc_autoshop_axis_config", "Reads (and optionally edits, repackages and re-opens) the axis configuration (xml\\axis.xml member) of a .hcpp project package via the F2 offline pipeline (AxisPackageEditor): reports the axis list (structural view, RE-derived schema) and applies '<axisName>.<field>' edits (e.g. X1.ForwardLimitValue) through read -> edit -> repackage -> readback verification; with open=true the repackaged project is re-opened in the owned AutoShop instance. Writes touch PROJECT FILES only - no PLC action. Experimental: structure-level only, the axis schema is reverse-engineered and the vendor consumption of edited values is unverified (no real H5U axis sample was available); hasAxisFile=false is reported honestly (\"no axis project\"). Reverse-engineered interface, no vendor endorsement, version-locked to AutoShop 4.12.0.2.", false, false,
                new
                {
                    type = "object",
                    properties = new
                    {
                        projectDir = new
                        {
                            type = "string",
                            description = "Absolute local directory containing exactly one .hcpp project package. Must not be inside the AutoShop installation directory."
                        },
                        edits = new
                        {
                            type = "object",
                            description = "Optional edits keyed '<axisName>.<field>' with the new value, e.g. {\"X1.ForwardLimitValue\": 4321.5, \"X1.EnableLimitSet\": false}. Supported fields: Name/AxisType/OutputDev/InputDev/SlaveName (string), Mode/Row (int), EnableLimitSet/IsVirtual/IsContour (bool), ForwardLimitValue/NegativeLimitValue/RotationPeriod (double). Omit for a read-only report."
                        },
                        open = new
                        {
                            type = "boolean",
                            description = "When true (and edits were applied), the repackaged project is re-opened in the owned AutoShop instance for open-level validation."
                        }
                    },
                    required = new[] { "projectDir" }
                }, HandleAutoShopAxisConfigAsync);
            Register("plc_autoshop_write_register", "Writes one Inovance soft element through Modbus TCP FC5 (single coil: M/X/Y/S/T/C bit devices) or FC6 (single register: D/R word devices) to a CONFIGURED runtime target (targetId) and reads the same address back (FC1/FC3) for closed-loop evidence; the address is mapped through the explicit Inovance soft-element table (no guessing). PHYSICAL WRITES ARE FORBIDDEN BY DEFAULT: only simulation targets on 127.0.0.0/8 hosts (the local H5U software simulator) are writable without the explicit --autoshop-allow-physical-write switch (default OFF; enabling it is an operator decision that still requires approval per the governance policy). Mapping table is an unverified documentation default - align it with the target project before any physical use; the device set follows AutoShop 4.12.0.2 simulator semantics and simulator results never prove hardware behaviour. Reverse-engineered soft-element mapping, no vendor endorsement, version-locked to AutoShop 4.12.0.2.", false, true,
                new
                {
                    type = "object",
                    properties = new
                    {
                        targetId = new
                        {
                            type = "string",
                            description = "Configured Inovance ModbusTcp target id (e.g. 'sim-inovance' or a simulator profile target)."
                        },
                        address = new
                        {
                            type = "string",
                            description = "Inovance soft element address (e.g. 'D100' word / FC6, 'M0' coil / FC5) or a Modbus-native HR address."
                        },
                        value = new
                        {
                            type = "number",
                            description = "Coil: boolean or 0/1. Register: raw integer 0..65535."
                        }
                    },
                    required = new[] { "targetId", "address", "value" }
                }, HandleAutoShopWriteRegisterAsync);
            Register("plc_autoshop_download", "Downloads the repackaged compile product (PPCH container .hcpp) of a local project directory into the loopback H5U software simulator workspace over the reverse-engineered D1 12939 native protocol (hard-limited to 127.0.0.1: both protocol clients refuse any non-loopback endpoint before a socket is created), then triggers program load and issues the RUN control word - the G2-verified minimal closed loop. Returns per-step evidence verbatim: payload path/length/md5, login + transport acks (chunk ACK + one-way finish), the load-trigger verdict and the RUN/STOP acks. HONEST BOUNDARIES: a download is a PLC action; local simulator demo only, physical targets require the explicit --autoshop-allow-physical-download switch (default OFF) plus operator approval and remain Experimental(真机待验) - the deep loopback red line of the protocol clients still refuses physical endpoints in this version, so nothing ever leaves 127.0.0.0/8. On the simulator the program LOAD is a stub (daemon status 0x82): the downloaded file is NOT activated as the running program and this tool never claims it; the vendor in-process Download export path is proven non-transferring (Unsupported). RUN moves the simulator registers live (G2 evidence: Modbus D100 0->4660 via the factory default app). A failed transfer never sends RUN; failures surface frame-level evidence, never a fake success. Reverse-engineered interface, no vendor endorsement, version-locked to AutoShop 4.12.0.2.", false, true,
                new
                {
                    type = "object",
                    properties = new
                    {
                        projectDir = new
                        {
                            type = "string",
                            description = "Absolute local project directory containing the repackaged .hcpp download payload (compile output; the newest .hcpp is used when compile artifacts are present). Must not be inside the AutoShop installation directory."
                        },
                        host = new
                        {
                            type = "string",
                            description = "Optional download host. MUST be loopback (default 127.0.0.1); any other value is refused unless --autoshop-allow-physical-download is enabled by the operator AND the protocol clients' deep loopback red line is lifted in a future version (currently it still refuses)."
                        },
                        port = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 65535,
                            description = "Optional native-protocol port (default 12939, the simulator's AutoShop native port)."
                        },
                        includeStop = new
                        {
                            type = "boolean",
                            description = "Issue a STOP (0xFF000020<-0) after RUN (default false: the simulator stays running for monitoring; STOP retains register values, it is not a reset)."
                        }
                    },
                    required = new[] { "projectDir" }
                }, HandleAutoShopDownloadAsync);
            Register("plc_autoshop_run_stop", "Starts (run: control word 0xFF000020<-1) or stops (stop: 0xFF000020<-0) the loopback H5U software simulator by writing the G2-verified register-chain control word on the native protocol port (default 12939), and returns the exact wire frame as hex, the daemon ack verdict, and the Modbus D100 before/after values read from 127.0.0.1:502 as register-alive evidence (G2 evidence: RUN moved D100 0->4660, i.e. the registers go live; STOP acks and the simulator RETAINS the last values - not a reset). Local simulator demo ONLY: hard-limited to 127.0.0.1 (the protocol client refuses non-loopback endpoints before connecting); a physical CPU RUN/STOP is Unsupported (the hardware control path is not reversed) and additionally requires the explicit --autoshop-allow-physical-download switch (default OFF) plus operator approval. The RUN verdict rests on the native-protocol ack; the Modbus evidence read is annotation - a failed evidence read is reported honestly and never fakes or cancels the verdict. Reverse-engineered interface, no vendor endorsement, version-locked to AutoShop 4.12.0.2.", false, true,
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new
                        {
                            type = "string",
                            @enum = new[] { "run", "stop" },
                            description = "run = write the RUN control word (0xFF000020<-1); stop = write the STOP control word (0xFF000020<-0)."
                        },
                        host = new
                        {
                            type = "string",
                            description = "Optional control host. MUST be loopback (default 127.0.0.1); other values are refused by the deep loopback red line."
                        },
                        port = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 65535,
                            description = "Optional native-protocol port (default 12939)."
                        },
                        readRegisterEvidence = new
                        {
                            type = "boolean",
                            description = "Read Modbus D100 on 127.0.0.1:502 before and after the control word (default true; the loopback evidence read can be turned off)."
                        }
                    },
                    required = new[] { "action" }
                }, HandleAutoShopRunStopAsync);
        }
        if (!_service.ListTargets().Any(t => t.IsSimulation))
        {
            _descriptors.RemoveAll(t => t.Name is "plc_plan_write" or "plc_apply_write");
            _handlers.Remove("plc_plan_write");
            _handlers.Remove("plc_apply_write");
        }
    }

    public IReadOnlyList<ToolDescriptor> ToolDescriptors => _descriptors;

    public async Task<object> CallToolAsync(string name, JsonElement args, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(name, out var handler))
        {
            string message = $"Unknown tool '{name}'.";
            return CreateToolCallResult(isError: true, new { error = message }, message);
        }

        try
        {
            var rawResult = await handler(args, cancellationToken).ConfigureAwait(false);

            if (rawResult is ApplyWriteResult { Applied: false } failedApply)
            {
                string applyErr = failedApply.Error ?? "Apply write rejected or failed.";
                return CreateToolCallResult(isError: true, failedApply, applyErr);
            }
            if (rawResult is EngineeringExecutionResult { Success: false } failedEngineering)
                return CreateToolCallResult(isError: true, failedEngineering, failedEngineering.Message);
            if (rawResult is JsonElement jsonElem && jsonElem.ValueKind == JsonValueKind.Object)
            {
                if (jsonElem.TryGetProperty("healthy", out var healthyProp) && !healthyProp.GetBoolean())
                {
                    string details = jsonElem.TryGetProperty("details", out var det) ? (det.GetString() ?? "Doctor diagnosis reported failures.") : "Doctor diagnosis reported failures.";
                    return CreateToolCallResult(isError: true, jsonElem, details);
                }
            }

            return CreateToolCallResult(isError: false, rawResult);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return CreateToolCallResult(isError: true, new { error = ex.Message }, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return CreateToolCallResult(isError: true, new { error = ex.Message }, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return CreateToolCallResult(isError: true, new { error = ex.Message }, ex.Message);
        }
        catch (Exception ex)
        {
            string message = $"Tool execution error: {ex.Message}";
            return CreateToolCallResult(isError: true, new { error = message }, message);
        }
    }

    private void RegisterTools()
    {
        Register(
            name: "plc_list_targets",
            description: "Lists all configured PLC targets and simulation connection profiles.",
            readOnly: true,
            destructive: false,
            schema: new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            },
            handler: HandleListTargetsAsync);

        Register(
            name: "plc_get_capabilities",
            description: "Returns supported runtime and engineering capabilities for a specified PLC target or the catalog.",
            readOnly: true,
            destructive: false,
            schema: new
            {
                type = "object",
                properties = new
                {
                    targetId = new
                    {
                        type = "string",
                        description = "Target identifier (e.g. 'sim-siemens'). If omitted, returns capabilities of all targets."
                    }
                },
                required = Array.Empty<string>()
            },
            handler: HandleGetCapabilitiesAsync);

        Register(
            name: "plc_list_tags",
            description: "Lists defined PLC tags and manifest metadata for a target, with optional keyword filtering.",
            readOnly: true,
            destructive: false,
            schema: new
            {
                type = "object",
                properties = new
                {
                    targetId = new
                    {
                        type = "string",
                        description = "Target identifier (e.g. 'sim-siemens')."
                    },
                    filter = new
                    {
                        type = "string",
                        description = "Optional filter keyword to match tag names or aliases."
                    }
                },
                required = new[] { "targetId" }
            },
            handler: HandleListTagsAsync);

        Register(
            name: "plc_probe_target",
            description: "Tests reachability and handshake for a specified PLC target.",
            readOnly: true,
            destructive: false,
            schema: new
            {
                type = "object",
                properties = new
                {
                    targetId = new
                    {
                        type = "string",
                        description = "Target identifier (e.g. 'sim-siemens')."
                    }
                },
                required = new[] { "targetId" }
            },
            handler: HandleProbeTargetAsync);

        Register(
            name: "plc_read_tags",
            description: "Reads one or more tag values from a target.",
            readOnly: true,
            destructive: false,
            schema: new
            {
                type = "object",
                properties = new
                {
                    targetId = new
                    {
                        type = "string",
                        description = "Target identifier (e.g. 'sim-siemens')."
                    },
                    tags = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "List of tag names or aliases to read."
                    }
                },
                required = new[] { "targetId", "tags" }
            },
            handler: HandleReadTagsAsync);

        Register(
            name: "plc_plan_write",
            description: "Creates an isolated write plan with type and safety range validation, returning an approval token.",
            readOnly: true,
            destructive: false,
            schema: new
            {
                type = "object",
                properties = new
                {
                    targetId = new
                    {
                        type = "string",
                        description = "Target identifier (e.g. 'sim-siemens')."
                    },
                    changes = new
                    {
                        type = "object",
                        description = "Dictionary of tag names and proposed string values."
                    },
                    lifetimeMinutes = new
                    {
                        type = "number",
                        description = "Optional plan lifetime in minutes (max 5)."
                    }
                },
                required = new[] { "targetId", "changes" }
            },
            handler: HandlePlanWriteAsync);

        Register(
            name: "plc_apply_write",
            description: "Consumes an approved write plan, verifies state hash, and applies changes atomically.",
            readOnly: false,
            destructive: true,
            schema: new
            {
                type = "object",
                properties = new
                {
                    planId = new
                    {
                        type = "string",
                        description = "Plan identifier returned by plc_plan_write."
                    },
                    approvalToken = new
                    {
                        type = "string",
                        description = "Approval token returned by plc_plan_write."
                    }
                },
                required = new[] { "planId", "approvalToken" }
            },
            handler: HandleApplyWriteAsync);

        Register(
            name: "plc_browse_symbols",
            description: "Browses symbol and tag definitions from the target manifest, supporting filtering by keyword.",
            readOnly: true,
            destructive: false,
            schema: new
            {
                type = "object",
                properties = new
                {
                    targetId = new
                    {
                        type = "string",
                        description = "Target identifier (e.g. 'sim-siemens')."
                    },
                    filter = new
                    {
                        type = "string",
                        description = "Optional filter keyword matching symbol name or aliases."
                    }
                },
                required = new[] { "targetId" }
            },
            handler: HandleBrowseSymbolsAsync);

        Register("plc_doctor", "Runs read-only local vendor software and simulator detection.", true, false,
            new { type = "object", properties = new { }, required = Array.Empty<string>() }, HandleDoctorAsync);
        if (_governance.SmartWorker is not null)
            Register("plc_project_inspect", "Inspects Siemens SMART V2 offline from a protected workcopy; V3 encrypted data is reported unsupported.", true, false,
                new { type = "object", properties = new { projectPath = new { type = "string" }, vendor = new { type = "string" } }, required = new[] { "projectPath", "vendor" } }, HandleProjectInspectAsync);
        Register("plc_lint_program", "Runs bounded heuristic ST/SCL precheck; filePath requires configured engineering root.", true, false,
            new { type = "object", properties = new { source = new { type = "string" }, filePath = new { type = "string" } }, required = Array.Empty<string>() }, HandleLintProgramAsync);
        if (_governance.SmartProjectRoot is not null)
            Register("plc_compare_projects", "Compares two <=1 MiB PLCopen XML files within the configured engineering root.", true, false,
                new { type = "object", properties = new { leftPath = new { type = "string" }, rightPath = new { type = "string" } }, required = new[] { "leftPath", "rightPath" } }, HandleCompareProjectsAsync);
        Register("plc_get_capabilities_report", "Returns capability evidence and local doctor status for a target.", true, false,
            new { type = "object", properties = new { targetId = new { type = "string" } }, required = new[] { "targetId" } }, HandleCapabilityReportAsync);
        if (_governance.CodesysWorker is not null)
        {
            Register("plc_codesys_doctor", "Read-only CODESYS offline worker diagnosis (version/profile/script pin); never starts CODESYS or touches a PLC.", true, false,
                new { type = "object", properties = new { }, required = Array.Empty<string>() }, HandleCodesysDoctorAsync);
        }
        if (_governance.CodesysWorker?.IsAvailable == true)
        {
            Register("plc_codesys_inspect", "Inspects a CODESYS project from a protected working copy via the installed ScriptEngine; offline only.", true, false,
                new { type = "object", properties = new { projectPath = new { type = "string" } }, required = new[] { "projectPath" } }, HandleCodesysInspectAsync);
            Register("plc_codesys_export", "Exports a CODESYS project to PLCopen XML inside an isolated working copy; offline only.", true, false,
                new { type = "object", properties = new { projectPath = new { type = "string" } }, required = new[] { "projectPath" } }, HandleCodesysExportAsync);
            Register("plc_codesys_compile", "Runs CODESYS clean/build/rebuild on a protected working copy and returns verifiable artifacts; no download or online action.", false, false,
                new { type = "object", properties = new { projectPath = new { type = "string" }, mode = new { type = "string", @enum = new[] { "build", "clean", "rebuild" } } }, required = new[] { "projectPath" } }, HandleCodesysCompileAsync);
        }
        if (_governance.GxWorks3Probe is not null)
        {
            Register("plc_gxworks3_doctor", "Runs isolated read-only GX Works3 metadata and service probe diagnosis; never starts GXW3 or touches a PLC.", true, false,
                new { type = "object", properties = new { }, required = Array.Empty<string>() }, HandleGxWorks3DoctorAsync);
        }
        Register("plc_omron_doctor", "Reports Sysmac Studio/CX-Server files, versions, PE bitness, hashes, and static COM registration; never starts Omron software or activates COM.", true, false,
            new { type = "object", properties = new { }, required = Array.Empty<string>() }, HandleOmronDoctorAsync);
        if (_governance.OmronExchange is not null)
        {
            Register("plc_omron_exchange_inspect", "Structurally inspects IEC 61131-10/PLCopen XML or AutomationML within the bounded engineering root; no vendor compiler or import.", true, false,
                new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } }, HandleOmronExchangeInspectAsync);
            Register("plc_omron_exchange_compare", "Compares two bounded Omron standard exchange files with stable structural and raw-extension hashes.", true, false,
                new { type = "object", properties = new { leftPath = new { type = "string" }, rightPath = new { type = "string" } }, required = new[] { "leftPath", "rightPath" } }, HandleOmronExchangeCompareAsync);
        }
        Register("plc_get_audit", "Verifies and reads the append-only audit hash chain.", true, false,
            new { type = "object", properties = new { verify = new { type = "boolean" } }, required = Array.Empty<string>() }, HandleGetAuditAsync);
        Register("plc_monitor_window", "Samples a finite read-only window; values are not an atomic PLC scan snapshot.", true, false,
            new { type = "object", properties = new {
                targetId = new { type = "string" }, tags = new { type = "array", items = new { type = "string" } },
                intervalMs = new { type = "integer", minimum = 50, maximum = 60000 },
                durationSeconds = new { type = "number", minimum = 0.05, maximum = 15 },
                maxSamples = new { type = "integer", minimum = 1, maximum = 20 }
            }, required = new[] { "targetId", "tags" } }, HandleMonitorWindowAsync);
        if (_governance.SmartProjectRoot is not null)
        {
            Register("plc_hmi_validate", "Validates vendor-neutral HMI bindings/alarms/recipes against a configured PLC tag manifest; no HMI device contact.", true, false,
                new { type = "object", properties = new { targetId = new { type = "string" }, manifestPath = new { type = "string" } }, required = new[] { "targetId", "manifestPath" } }, HandleHmiValidateAsync);
            Register("plc_hmi_generate", "Writes vendor-neutral JSON/CSV HMI artifacts to a new directory under the local workspaces root; no HMI publish.", false, false,
                new { type = "object", properties = new { targetId = new { type = "string" }, manifestPath = new { type = "string" } }, required = new[] { "targetId", "manifestPath" } }, HandleHmiGenerateAsync);
        }
    }

    private void Register(
        string name,
        string description,
        bool readOnly,
        bool destructive,
        object schema,
        Func<JsonElement, CancellationToken, Task<object>> handler,
        bool openWorld = false)
    {
        var annotations = new ToolAnnotations(
            ReadOnlyHint: readOnly,
            DestructiveHint: destructive,
            OpenWorldHint: openWorld);

        _descriptors.Add(new ToolDescriptor(name, description, readOnly, destructive, schema, annotations));
        _handlers[name] = handler;
    }

    private Task<object> HandleListTargetsAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var targets = _service.ListTargets();
        var result = new
        {
            targets = targets.Select(t => new
            {
                id = t.Id,
                vendor = t.Vendor,
                family = t.Family,
                model = t.Model,
                firmware = t.Firmware,
                endpoint = t.Endpoint,
                runtimeProtocols = t.RuntimeProtocols,
                engineeringBackend = t.EngineeringBackend,
                capabilities = t.Capabilities.Items,
                policyId = t.PolicyId,
                isSimulation = t.IsSimulation
            }).ToArray()
        };
        return Task.FromResult<object>(result);
    }

    private Task<object> HandleGetCapabilitiesAsync(JsonElement args, CancellationToken cancellationToken)
    {
        string? targetId = TryGetString(args, "targetId", "target_id");
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            var target = _service.GetTarget(targetId);
            var singleResult = new
            {
                targetId = target.Id,
                vendor = target.Vendor,
                family = target.Family,
                isSimulation = target.IsSimulation,
                capabilities = target.Capabilities.Items,
                runtimeProtocols = target.RuntimeProtocols,
                engineeringBackend = target.EngineeringBackend,
                policyId = target.PolicyId
            };
            return Task.FromResult<object>(singleResult);
        }

        var allTargets = _service.ListTargets();
        var catalogResult = new
        {
            protocols = VendorCatalog.Protocols,
            targets = allTargets.Select(t => new
            {
                targetId = t.Id,
                vendor = t.Vendor,
                family = t.Family,
                capabilities = t.Capabilities.Items,
                runtimeProtocols = t.RuntimeProtocols
            }).ToArray()
        };
        return Task.FromResult<object>(catalogResult);
    }

    private Task<object> HandleListTagsAsync(JsonElement args, CancellationToken cancellationToken)
    {
        string targetId = RequireString(args, "targetId", "target_id");
        string? filter = TryGetString(args, "filter");

        var tags = _service.ListTags(targetId, filter);
        var result = new
        {
            targetId,
            count = tags.Count,
            tags
        };
        return Task.FromResult<object>(result);
    }

    private Task<object> HandleBrowseSymbolsAsync(JsonElement args, CancellationToken cancellationToken)
    {
        return HandleListTagsAsync(args, cancellationToken);
    }

    private async Task<object> HandleSmartInspectAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var worker = _governance.SmartWorker ?? throw new NotSupportedException("SMART worker unavailable.");
        var path = RequireString(args, "projectPath", "project_path");
        var job = new EngineeringJobRequest(Guid.NewGuid().ToString("N"), EngineeringJobType.InspectProject, PlcVendor.Siemens, path);
        return await worker.ExecuteAsync(job, cancellationToken);
    }

    private async Task<object> HandleSmartValidateAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var worker = _governance.SmartWorker ?? throw new NotSupportedException("SMART worker unavailable.");
        var path = RequireString(args, "projectPath", "project_path");
        var blocks = RequireStringList(args, "blockNames", "block_names");
        var job = new EngineeringJobRequest(Guid.NewGuid().ToString("N"), EngineeringJobType.ValidatePou, PlcVendor.Siemens, path,
            new Dictionary<string, string> { ["blockNames"] = string.Join(",", blocks) });
        return await worker.ExecuteAsync(job, cancellationToken);
    }

    private Task<object> HandleCodesysDoctorAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var worker = _governance.CodesysWorker ?? throw new NotSupportedException("CODESYS worker unavailable.");
        return Task.FromResult<object>(worker.CheckDoctor(cancellationToken));
    }

    private async Task<object> HandleCodesysInspectAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var worker = _governance.CodesysWorker ?? throw new NotSupportedException("CODESYS worker unavailable.");
        var path = RequireString(args, "projectPath", "project_path");
        var job = new EngineeringJobRequest(Guid.NewGuid().ToString("N"), EngineeringJobType.InspectProject, PlcVendor.Generic, path);
        return await worker.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> HandleCodesysExportAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var worker = _governance.CodesysWorker ?? throw new NotSupportedException("CODESYS worker unavailable.");
        var path = RequireString(args, "projectPath", "project_path");
        var job = new EngineeringJobRequest(Guid.NewGuid().ToString("N"), EngineeringJobType.ExportPou, PlcVendor.Generic, path);
        return await worker.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> HandleCodesysCompileAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var worker = _governance.CodesysWorker ?? throw new NotSupportedException("CODESYS worker unavailable.");
        var path = RequireString(args, "projectPath", "project_path");
        var modeRaw = TryGetString(args, "mode") ?? "build";
        var mode = modeRaw.ToLowerInvariant();
        if (mode is not ("build" or "clean" or "rebuild"))
            throw new ArgumentException("Parameter 'mode' must be one of: build, clean, rebuild.");
        var job = new EngineeringJobRequest(Guid.NewGuid().ToString("N"), EngineeringJobType.CompileProject, PlcVendor.Generic, path,
            new Dictionary<string, string> { ["mode"] = mode });
        return await worker.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> HandleGxWorks3DoctorAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var probe = _governance.GxWorks3Probe ?? throw new NotSupportedException("GX Works3 probe worker unavailable.");
        return await probe.RunDoctorAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<object> HandleOmronDoctorAsync(JsonElement args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<object>(_governance.OmronDoctor.Diagnose());
    }

    private Task<object> HandleOmronExchangeInspectAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var service = _governance.OmronExchange ?? throw new NotSupportedException("Omron exchange root is not configured.");
        var path = RequireString(args, "path", "filePath", "file_path");
        return Task.FromResult<object>(service.Inspect(path));
    }

    private Task<object> HandleOmronExchangeCompareAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var service = _governance.OmronExchange ?? throw new NotSupportedException("Omron exchange root is not configured.");
        var left = RequireString(args, "leftPath", "left_path");
        var right = RequireString(args, "rightPath", "right_path");
        return Task.FromResult<object>(service.Compare(left, right));
    }

    private Task<object> HandleDoctorAsync(JsonElement args, CancellationToken cancellationToken) =>
        Task.FromResult<object>(_governance.Doctor.RunFullDiagnosis());

    private async Task<object> HandleAutoShopInspectAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var bridge = _governance.AutoShopBridge ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        return await bridge.InspectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> HandleAutoShopReadSharedVariablesAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var bridge = _governance.AutoShopBridge ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        double observationSeconds = ReadBoundedDouble(args, "observationSeconds", 2, 0.5, 10);
        return await bridge.ReadSharedVariablesAsync(TimeSpan.FromSeconds(observationSeconds), cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> HandleAutoShopStatusAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var bridge = _governance.AutoShopBridge ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        var simulator = _governance.AutoShopSimulator ?? throw new NotSupportedException("AutoShop simulator services are unavailable.");
        AutoShopInspectionReport inspection = await bridge.InspectAsync(cancellationToken).ConfigureAwait(false);
        AutoShopSimulatorStatusReport simulatorStatus = await simulator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return BuildAutoShopStatusPayload(inspection, simulatorStatus);
    }

    private async Task<object> HandleAutoShopSimulatorAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var simulator = _governance.AutoShopSimulator ?? throw new NotSupportedException("AutoShop simulator services are unavailable; pass --autoshop-bridge to opt in.");
        string action = RequireString(args, "action").ToLowerInvariant();
        switch (action)
        {
            case "status":
                return await simulator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            case "start":
                return await simulator.StartAsync(cancellationToken).ConfigureAwait(false);
            case "stop":
                int? pid = TryGetInt(args, "pid");
                return await simulator.StopAsync(pid, cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentException("Parameter 'action' must be one of: status, start, stop.");
        }
    }

    private static object BuildAutoShopStatusPayload(AutoShopInspectionReport inspection, AutoShopSimulatorStatusReport simulator)
    {
        AutoShopEnvironmentReport environment = inspection.Environment;
        return new
        {
            environment = new
            {
                installed = inspection.EnvironmentInstalled,
                environment.ExecutablePath,
                environment.Version,
                environment.ServiceName,
                serviceProcessRunning = environment.ServiceProcessRunning,
                registryPort = environment.RegistryPort,
                registryPortLocation = environment.RegistryPortLocation,
                itpEndpoint = $"127.0.0.1:{environment.EffectivePort}",
                itpLoopbackListening = environment.LoopbackListening,
                newestServiceLog = environment.NewestLogFile,
                environment.Details
            },
            itpHandshake = inspection.Handshake,
            simulator = new
            {
                daemonPath = simulator.DaemonPath,
                daemonFilePresent = simulator.DaemonFilePresent,
                running = simulator.Running,
                runningPids = simulator.RunningPids,
                ownedPids = simulator.OwnedPids,
                modbusTcp = new { host = "127.0.0.1", port = simulator.ModbusPort, listening = simulator.ModbusPortListening },
                nativeProtocol = new { host = "127.0.0.1", port = simulator.NativePort, listening = simulator.NativePortListening },
                simulator.Summary
            },
            capabilities = BuildAutoShopCapabilityMatrix(inspection, simulator),
            summary = $"AutoShop installed: {inspection.EnvironmentInstalled}; ITP handshake answered: {inspection.Handshake?.Answered == true}; " +
                $"h5u_daemon running: {simulator.Running} ({string.Join(", ", simulator.RunningPids)}); " +
                $"loopback 127.0.0.1:{simulator.ModbusPort} (Modbus TCP) listening: {simulator.ModbusPortListening}; " +
                $"loopback 127.0.0.1:{simulator.NativePort} (AutoShop native) listening: {simulator.NativePortListening}.",
            disclaimer = inspection.Disclaimer + " " + simulator.Disclaimer
        };
    }

    private static IReadOnlyList<object> BuildAutoShopCapabilityMatrix(AutoShopInspectionReport inspection, AutoShopSimulatorStatusReport simulator)
    {
        bool handshakeAnswered = inspection.Handshake?.Answered == true;
        bool simListening = simulator.ModbusPortListening;
        var items = new List<object>
        {
            Item("environment_inspection", "Experimental",
                "Local filesystem/registry/TCP/process evidence only; no vendor endorsement."),
            Item("itp_register_handshake", handshakeAnswered ? "Experimental" : "Unsupported",
                handshakeAnswered
                    ? "Register handshake answered by the local InoVarSharedService endpoint."
                    : "ITP endpoint unreachable or handshake not answered; no verified handshake evidence."),
            Item("shared_variable_read", "Unsupported",
                "ITP variable data-push packet layout is not verified; no value decoding is implemented."),
            Item("runtime_read_modbus_tcp", simListening ? "Experimental" : "Unsupported",
                simListening
                    ? "Local simulator daemon is listening on 127.0.0.1:" + simulator.ModbusPort + "; use plc_probe_target/plc_read_tags with a configured Inovance ModbusTcp target (verified against the local simulator only, not hardware)."
                    : "No listener on loopback 127.0.0.1:" + simulator.ModbusPort + "; configure an Inovance ModbusTcp target profile and start the local simulator daemon to test."),
            Item("monitoring_window", simListening ? "Experimental" : "Unsupported",
                simListening
                    ? "plc_monitor_window has a real backend via a configured Inovance ModbusTcp target; values are not an atomic PLC scan snapshot."
                    : "Requires a configured reachable target."),
            Item("simulator_status", "Experimental",
                "Read-only local process and loopback endpoint discovery for the h5u_daemon simulator; probes are restricted to 127.0.0.1."),
            Item("simulator_start", "Experimental",
                "Starts the local h5u_daemon.exe process only (no AutoShop UI, no PLC contact); duplicate-start protection, bounded verification and on-disk log."),
            Item("simulator_stop", "Experimental",
                "Stops only simulator PIDs recorded by this server's own start (PID ownership protection; no bare taskkill of arbitrary processes)."),
            Item("compile", "Experimental",
                "plc_autoshop_compile (behind --autoshop-bridge) runs the F1-verified in-process compile-all " +
                "chain inside the owned AutoShop instance (root 0x1581A70 -> cmpMgr 0x1581CA0 -> entry 0x15A2EC0, " +
                "project gate 0x161B620 checked first) and polls/decrypts the Compile artifacts; offline IDE " +
                "behaviour, no PLC action; verified against template-based projects on this host only."),
            Item("autoshop_inject_bridge_open", "Experimental",
                "plc_autoshop_open_project (behind --autoshop-bridge) starts a self-launched, journaled AutoShop instance " +
                "and opens a locally constructed project (E3 method, verified on this host for template-based projects); " +
                "local IDE instance only, no PLC connection."),
            Item("autoshop_inject_bridge_generate", "Experimental",
                "plc_autoshop_generate_project (behind --autoshop-bridge) constructs an openable project offline " +
                "(UCodeCrypt .hcp + declared files, offline round-trip verified); whether AutoShop accepts generated file " +
                "contents is unverified."),
            Item("autoshop_inject_bridge_monitor_read", "Experimental",
                "plc_autoshop_monitor_read (behind --autoshop-bridge) performs an in-process vendor read inside the " +
                "owned AutoShop instance (CSoftAddrMap filled via tagSoftAddr ctor + CMap::SetAt, ReadElemValue " +
                "accepted rc=0, dual-slot polling: the tag+0x0C CString delivery slot and the +0x20 double mirror, " +
                "with a delivered=N discriminator). The raw-HWND misuse is explained (CWnd* vs HWND); the blocker to " +
                "delivery is the in-process session establishment, which is NOT reverse-engineered (the injected " +
                "process sends zero wire bytes), so on the current backend delivered is always 0 and reported " +
                "values are call-level evidence, never PLC data (delivered>0 would be vendor-stack delivered values, " +
                "still Experimental/loopback scope)."),
            Item("autoshop_inject_bridge_axis_config", "Experimental",
                "plc_autoshop_axis_config (behind --autoshop-bridge) reads/edits the xml\\axis.xml member of a .hcpp " +
                "package (F2 pipeline, offline round-trip verified) and can re-open the project; structure-level only, " +
                "vendor consumption of edited values is unverified (no real H5U axis sample)."),
            Item("autoshop_write_register", "Experimental",
                "plc_autoshop_write_register (behind --autoshop-bridge) writes FC5/FC6 Modbus TCP frames with readback; " +
                "by default ONLY simulation targets on 127.0.0.0/8 hosts are writable; physical targets require " +
                "--autoshop-allow-physical-write (default OFF) plus operator approval; mapping table is an unverified " +
                "documentation default."),
            Item("download", "Unsupported",
                "PHYSICAL download to hardware PLC targets remains Unsupported: the hardware download state machine " +
                "is unverified, the physical gate --autoshop-allow-physical-download defaults OFF (approval required) " +
                "and the D1 protocol clients keep a deep loopback red line that refuses non-loopback endpoints in this " +
                "version. The loopback SIMULATOR download transfer is available behind --autoshop-bridge " +
                "(see autoshop_download_transfer)."),
            Item("autoshop_download_transfer", "Experimental",
                "plc_autoshop_download (behind --autoshop-bridge) transfers the repackaged project.hcpp into the " +
                "LOOPBACK H5U simulator workspace over the D1 12939 native protocol (login + chunk ACKs; byte-exact " +
                "landing verified in the G2 experiment for this payload shape). Local simulator demo only: physical " +
                "targets require --autoshop-allow-physical-download (default OFF) plus operator approval and remain " +
                "Experimental(真机待验); the protocol clients' deep loopback red line still refuses non-loopback " +
                "endpoints in this version, so nothing ever leaves 127.0.0.0/8."),
            Item("autoshop_program_load", "Unsupported",
                "The simulator load_down_file_start is a stub: the load trigger (0xFF000010<-1) answers daemon status " +
                "0x82 and the downloaded file is NOT activated as the running program (G2 + D1 evidence). Hardware " +
                "load (non-stub + full download state machine) is unverified; no tool claims a program load."),
            Item("autoshop_run_stop", "Experimental",
                "plc_autoshop_run_stop (behind --autoshop-bridge) writes the 0xFF000020 RUN/STOP control word on the " +
                "loopback simulator register chain: RUN acked [06] and moved Modbus D100 0->4660 (registers alive, G2 " +
                "evidence via the factory default app); STOP acks and the simulator RETAINS values. Local simulator " +
                "demo only; hardware CPU RUN/STOP remains Unsupported (control path not reversed)."),
            Item("autoshop_download_via_vendor_export", "Unsupported",
                "The in-process vendor CCommunicateEth::Download export was proven a dead automation channel " +
                "(G2 harness: rc=0 accepted, ZERO file-transfer frames; DownloadPlcSoft standalone crashes) - the " +
                "bridge gate defaults closed, the export is never invoked, and the closed loop runs on the D1 " +
                "protocol layer instead."),
            Item("force", "Unsupported",
                "Forcing is forbidden; no force tool is registered (the inject-bridge force gate defaults closed; F1 " +
                "runtime-verified that the vendor Force body terminates the process in a way user SEH cannot catch)."),
            Item("plc_write", "Unsupported",
                "Writes THROUGH AutoShop are deterministically unavailable: the vendor comm-stack eth-channel write " +
                "exports are stubs (WriteElemValue/Unforce return 0xF390=62352 with no function body, F1-verified); the " +
                "inject-bridge write gate defaults closed and the exports are never invoked. Simulator writes go through " +
                "Modbus TCP 502 (plc_autoshop_write_register / plc_plan_write+plc_apply_write), not the inject bridge."),
            Item("plc_run_stop", "Unsupported",
                "The physical CPU RUN/STOP control path is not reversed and no execution entry exists for hardware " +
                "targets (the loopback SIMULATOR run/stop control word is covered by autoshop_run_stop above and " +
                "never claims hardware behaviour)."),
            Item("plugin_framework", "Unsupported",
                "Plugin framework intelligence is archived only; its layout is version-locked to AutoShop 4.12.0.2 and not implemented."),
            Item("native_comm_stack", "Unsupported",
                "Communication-stack exports are archived as evidence; only the inject bridge (behind --autoshop-bridge) " +
                "exercises the verified read/compile subset in-process.")
        };
        return items;

        static object Item(string name, string status, string reason) => new { name, status, reason };
    }

    private async Task<object> HandleAutoShopOpenProjectAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var tools = _governance.AutoShopInject ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        string projectDir = RequireString(args, "projectDir", "project_dir");
        return await tools.OpenProjectAsync(projectDir, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> HandleAutoShopCompileAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var tools = _governance.AutoShopInject ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        return await tools.CompileAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> HandleAutoShopGenerateProjectAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var tools = _governance.AutoShopInject ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        string projectDir = RequireString(args, "projectDir", "project_dir");
        string? model = TryGetString(args, "model");
        string? projectName = TryGetString(args, "projectName", "project_name");
        string? description = TryGetString(args, "projectDescription", "project_description");
        string? hardwareFile = TryGetString(args, "hardwareFile", "hardware_file");
        string? sourceHcpp = TryGetString(args, "sourceHcpp", "source_hcpp");

        List<AutoShopGeneratedFileSpec>? files = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("files", out JsonElement filesElement) &&
            filesElement.ValueKind == JsonValueKind.Array)
        {
            files = new List<AutoShopGeneratedFileSpec>();
            foreach (JsonElement item in filesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out JsonElement nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    throw new ArgumentException("Each entry in 'files' must be an object with a string 'name'.");
                }
                string name = nameElement.GetString() ?? string.Empty;
                byte[] content;
                if (item.TryGetProperty("contentBase64", out JsonElement base64Element) &&
                    base64Element.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        content = Convert.FromBase64String(base64Element.GetString() ?? string.Empty);
                    }
                    catch (FormatException ex)
                    {
                        throw new ArgumentException($"File '{name}' has invalid base64 content: {ex.Message}");
                    }
                }
                else
                {
                    content = Encoding.UTF8.GetBytes(item.TryGetProperty("text", out JsonElement textElement) &&
                        textElement.ValueKind == JsonValueKind.String ? textElement.GetString() ?? string.Empty : string.Empty);
                }
                files.Add(new AutoShopGeneratedFileSpec(name, content));
            }
        }

        return await tools.GenerateProjectAsync(projectDir, model, projectName, description,
            hardwareFile, sourceHcpp, files, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> HandleAutoShopMonitorReadAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var tools = _governance.AutoShopInject ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        string addresses = RequireString(args, "addresses");
        return await tools.MonitorReadAsync(addresses, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> HandleAutoShopAxisConfigAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var tools = _governance.AutoShopInject ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        string projectDir = RequireString(args, "projectDir", "project_dir");
        bool open = false;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("open", out JsonElement openElement) &&
            openElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            open = openElement.GetBoolean();
        }

        IReadOnlyDictionary<string, object?>? edits = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("edits", out JsonElement editsElement) &&
            editsElement.ValueKind == JsonValueKind.Object)
        {
            edits = RequireDictionary(args, "edits");
        }

        return await tools.AxisConfigAsync(projectDir, edits, open, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> HandleAutoShopDownloadAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var tools = _governance.AutoShopInject ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        string projectDir = RequireString(args, "projectDir", "project_dir");
        string? host = TryGetString(args, "host");
        int? port = TryGetBoundedPort(args);
        bool includeStop = false;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("includeStop", out JsonElement stopElement) &&
            stopElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            includeStop = stopElement.GetBoolean();
        }
        return await tools.DownloadAsync(projectDir, host, port, includeStop, _governance.AllowPhysicalDownload, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<object> HandleAutoShopRunStopAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var tools = _governance.AutoShopInject ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        string action = RequireString(args, "action");
        string? host = TryGetString(args, "host");
        int? port = TryGetBoundedPort(args);
        bool readEvidence = true;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("readRegisterEvidence", out JsonElement evidenceElement) &&
            evidenceElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            readEvidence = evidenceElement.GetBoolean();
        }
        return await tools.RunStopAsync(action, host, port, readEvidence, _governance.AllowPhysicalDownload, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<object> HandleAutoShopWriteRegisterAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var service = _governance.AutoShopWriteRegister ?? throw new NotSupportedException("AutoShop bridge is not enabled; pass --autoshop-bridge to opt in.");
        string targetId = RequireString(args, "targetId", "target_id");
        string address = RequireString(args, "address");
        object? value = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("value", out JsonElement valueElement) &&
            valueElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            value = valueElement;
        }
        TargetProfile target = _service.GetTarget(targetId);
        return await service.WriteAsync(target, address, value, _governance.AllowPhysicalWrite, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Bounded optional port parameter: absent/null -> null (caller default), otherwise 1..65535.</summary>
    private static int? TryGetBoundedPort(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("port", out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed) || parsed < 1 || parsed > 65535)
        {
            throw new ArgumentException("Parameter 'port' must be an integer from 1 to 65535.");
        }
        return parsed;
    }

    private static int? TryGetInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) && parsed > 0) return parsed;
        throw new ArgumentException($"Parameter '{name}' must be a positive integer.");
    }

    private Task<object> HandleCapabilityReportAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var target = _service.GetTarget(RequireString(args, "targetId", "target_id"));
        return GetReportAsync(target, cancellationToken);

        async Task<object> GetReportAsync(TargetProfile profile, CancellationToken token) =>
            await _governance.GetCapabilityReportAsync(profile, token).ConfigureAwait(false);
    }

    private async Task<object> HandleProjectInspectAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var path = RequireString(args, "projectPath", "project_path");
        var vendorText = RequireString(args, "vendor");
        if (!Enum.TryParse<PlcVendor>(vendorText, true, out var vendor)) throw new ArgumentException("Unknown vendor.");
        if (vendor != PlcVendor.Siemens || _governance.SmartWorker is null)
            throw new NotSupportedException("No verified engineering worker is configured for this vendor. Set --smart-project-root to enable SMART offline inspect.");
        var job = new EngineeringJobRequest(Guid.NewGuid().ToString("N"), EngineeringJobType.InspectProject, vendor, path);
        return await _governance.SmartWorker.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private string ReadAllowedEngineeringFile(string requestedPath)
    {
        var root = _governance.SmartProjectRoot ?? throw new NotSupportedException(
            "File operations require --smart-project-root; pass bounded inline source for the standalone linter.");
        var safe = PlcMcp.Engineering.Workspace.ProjectWorkspaceManager.SanitizePath(requestedPath, root);
        var file = new FileInfo(safe);
        if (!file.Exists || file.Length > 1_048_576)
            throw new ArgumentException("Engineering input file must exist and be <=1 MiB under the configured root.");
        return safe;
    }

    private Task<object> HandleLintProgramAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var source = TryGetString(args, "source");
        if (source is null)
            source = File.ReadAllText(ReadAllowedEngineeringFile(RequireString(args, "filePath", "file_path")));
        if (source.Length > 1_048_576)
            throw new ArgumentException("Inline ST source is too large (maximum 1 MiB characters).");
        return Task.FromResult<object>(_governance.StAnalyzer.Analyze(source));
    }

    private Task<object> HandleCompareProjectsAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var left = _governance.Plcopen.ParseFile(ReadAllowedEngineeringFile(RequireString(args, "leftPath", "left_path")));
        var right = _governance.Plcopen.ParseFile(ReadAllowedEngineeringFile(RequireString(args, "rightPath", "right_path")));
        return Task.FromResult<object>(_governance.Plcopen.Compare(left, right));
    }

    private async Task<object> HandleGetJobAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var job = await _governance.Jobs.GetJobAsync(RequireString(args, "jobId", "job_id"), cancellationToken);
        return job ?? throw new KeyNotFoundException("Job not found.");
    }

    private async Task<object> HandleGetAuditAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var result = await _governance.Audit.VerifyChainAsync(cancellationToken);
        if (!result.IsValid) return new { verification = result, records = Array.Empty<AuditRecord>() };
        return new { verification = result, records = await _governance.Audit.ReadAllAsync(cancellationToken) };
    }

    private HmiManifest LoadHmiManifest(JsonElement args, out TargetProfile target)
    {
        target = _service.GetTarget(RequireString(args, "targetId", "target_id"));
        var path = RequireString(args, "manifestPath", "manifest_path");
        if (_governance.SmartProjectRoot is null)
            throw new NotSupportedException("HMI source root is not configured; supply --smart-project-root as a local engineering workspace root.");
        var safe = PlcMcp.Engineering.Workspace.ProjectWorkspaceManager.SanitizePath(path, _governance.SmartProjectRoot);
        if (!File.Exists(safe) || new FileInfo(safe).Length > 1_048_576)
            throw new ArgumentException("HMI manifest must exist and be <=1 MiB under the configured engineering root.");
        var json = File.ReadAllText(safe);
        var manifest = JsonSerializer.Deserialize<HmiManifest>(json, new JsonSerializerOptions(JsonRpc.SerializerOptions) { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("HMI manifest cannot be null.");
        if (manifest.TargetVendor != target.Vendor)
            throw new ArgumentException("HMI manifest vendor must match the selected PLC target vendor.");
        return manifest;
    }

    private Task<object> HandleHmiValidateAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var manifest = LoadHmiManifest(args, out var target);
        return Task.FromResult<object>(new HmiValidator().Validate(manifest, _service.ListTags(target.Id), requirePlcDefinitions: true));
    }

    private Task<object> HandleHmiGenerateAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var manifest = LoadHmiManifest(args, out var target);
        var root = Path.Combine(_governance.DataRoot, "workspaces", "hmi");
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, Guid.NewGuid().ToString("N"));
        return Task.FromResult<object>(new HmiArtifactGenerator(defaultAllowedRoot: root).Generate(
            manifest, output, _service.ListTags(target.Id), requirePlcDefinitions: true));
    }

    private async Task<object> HandleMonitorWindowAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var targetId = RequireString(args, "targetId", "target_id");
        var tags = RequireStringList(args, "tags");
        var target = _service.GetTarget(targetId);
        var maxSamples = ReadBoundedInteger(args, "maxSamples", 3, 1, 20);
        var intervalMs = ReadBoundedInteger(args, "intervalMs", 500, 50, 60000);
        var durationSeconds = ReadBoundedDouble(args, "durationSeconds", 5, 0.05, 15);
        if (!target.IsSimulation && intervalMs < 200)
            throw new ArgumentException("Physical targets require at least 200 ms between monitoring samples.");
        var request = new MonitoringRequest
        {
            TargetId = target.Id, Tags = tags,
            Interval = TimeSpan.FromMilliseconds(intervalMs),
            Duration = TimeSpan.FromSeconds(durationSeconds),
            MaxSamples = maxSamples
        };
        return await _monitoring.SampleWindowAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static int ReadBoundedInteger(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed) || parsed < min || parsed > max)
            throw new ArgumentException($"Parameter '{name}' must be an integer from {min} to {max}.");
        return parsed;
    }

    private static double ReadBoundedDouble(JsonElement args, string name, double defaultValue, double min, double max)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var parsed) || !double.IsFinite(parsed) || parsed < min || parsed > max)
            throw new ArgumentException($"Parameter '{name}' must be a number from {min} to {max}.");
        return parsed;
    }

    private async Task<object> HandleProbeTargetAsync(JsonElement args, CancellationToken cancellationToken)
    {
        string targetId = RequireString(args, "targetId", "target_id");
        var probe = await _service.ProbeAsync(targetId, cancellationToken).ConfigureAwait(false);
        return probe;
    }

    private async Task<object> HandleReadTagsAsync(JsonElement args, CancellationToken cancellationToken)
    {
        string targetId = RequireString(args, "targetId", "target_id");
        var names = RequireStringList(args, "tags", "names");

        var values = await _service.ReadAsync(targetId, names, cancellationToken).ConfigureAwait(false);
        return new
        {
            targetId,
            values
        };
    }

    private async Task<object> HandlePlanWriteAsync(JsonElement args, CancellationToken cancellationToken)
    {
        string targetId = RequireString(args, "targetId", "target_id");
        var changes = RequireDictionary(args, "changes");
        double? lifetimeMinutes = TryGetDouble(args, "lifetimeMinutes", "lifetime");

        TimeSpan? lifetime = lifetimeMinutes.HasValue ? TimeSpan.FromMinutes(lifetimeMinutes.Value) : null;
        if (lifetime.HasValue && (lifetime.Value <= TimeSpan.Zero || lifetime.Value > TimeSpan.FromMinutes(5)))
        {
            throw new ArgumentException("Parameter 'lifetimeMinutes' must be a positive number up to 5 minutes.");
        }

        var plan = await _service.PlanWriteAsync(targetId, changes, lifetime, cancellationToken).ConfigureAwait(false);
        return plan;
    }

    private async Task<object> HandleApplyWriteAsync(JsonElement args, CancellationToken cancellationToken)
    {
        string planId = RequireString(args, "planId", "plan_id");
        string approvalToken = RequireString(args, "approvalToken", "approval_token");

        var result = await _service.ApplyWriteAsync(planId, approvalToken, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static object CreateToolCallResult(bool isError, object structuredResult, string? errorText = null)
    {
        string jsonText = JsonSerializer.Serialize(structuredResult, JsonRpc.SerializerOptions);
        JsonObject? node = null;
        try
        {
            node = JsonNode.Parse(jsonText) as JsonObject;
        }
        catch
        {
            node = new JsonObject();
        }
        node ??= new JsonObject();

        string textToEmit = isError && !string.IsNullOrWhiteSpace(errorText)
            ? $"Error: {errorText}"
            : jsonText;

        var contentArray = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = textToEmit
            }
        };

        node["content"] = contentArray;
        node["isError"] = isError;

        if (isError && !node.ContainsKey("error") && !string.IsNullOrWhiteSpace(errorText))
        {
            node["error"] = errorText;
        }

        return node;
    }

    private static string? TryGetString(JsonElement args, params string[] propertyNames)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in propertyNames)
        {
            if (args.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                {
                    return prop.GetString();
                }
                if (prop.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }
                throw new ArgumentException($"Parameter '{name}' must be a string.");
            }
        }

        return null;
    }

    private static string RequireString(JsonElement args, params string[] propertyNames)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            string primary = propertyNames.FirstOrDefault() ?? "parameter";
            throw new ArgumentException($"Parameter '{primary}' must be provided in an arguments object.");
        }

        var val = TryGetString(args, propertyNames);
        if (string.IsNullOrWhiteSpace(val))
        {
            string primaryName = propertyNames.FirstOrDefault() ?? "parameter";
            throw new ArgumentException($"Parameter '{primaryName}' is required and cannot be empty.");
        }
        return val;
    }

    private static IReadOnlyList<string> RequireStringList(JsonElement args, params string[] propertyNames)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            string primaryName = propertyNames.FirstOrDefault() ?? "parameter";
            throw new ArgumentException($"Parameter '{primaryName}' must be provided in an arguments object.");
        }

        foreach (var name in propertyNames)
        {
            if (args.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in prop.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var s = item.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                list.Add(s);
                            }
                            else
                            {
                                throw new ArgumentException($"Elements in array '{name}' cannot be empty strings.");
                            }
                        }
                        else
                        {
                            throw new ArgumentException($"Elements in array '{name}' must be strings.");
                        }
                    }

                    if (list.Count == 0)
                    {
                        throw new ArgumentException($"Parameter '{name}' cannot be an empty array.");
                    }

                    return list;
                }

                if (prop.ValueKind == JsonValueKind.String)
                {
                    var s = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return new[] { s };
                    }
                    throw new ArgumentException($"Parameter '{name}' cannot be an empty string.");
                }

                throw new ArgumentException($"Parameter '{name}' must be an array of strings.");
            }
        }

        string primary = propertyNames.FirstOrDefault() ?? "parameter";
        throw new ArgumentException($"Parameter '{primary}' must be provided in an arguments object.");
    }

    private static double? TryGetDouble(JsonElement args, params string[] propertyNames)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in propertyNames)
        {
            if (args.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var num))
                {
                    return num;
                }
                if (prop.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }
                throw new ArgumentException($"Parameter '{name}' must be a number.");
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, object?> RequireDictionary(JsonElement args, params string[] propertyNames)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            string primary = propertyNames.FirstOrDefault() ?? "parameter";
            throw new ArgumentException($"Parameter '{primary}' must be provided in an arguments object.");
        }

        foreach (var name in propertyNames)
        {
            if (args.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException($"Parameter '{name}' must be an object.");
                }

                var dict = new Dictionary<string, object?>();
                foreach (var propItem in prop.EnumerateObject())
                {
                    if (propItem.Value.ValueKind == JsonValueKind.String)
                    {
                        dict[propItem.Name] = propItem.Value.GetString()!;
                    }
                    else if (propItem.Value.ValueKind == JsonValueKind.Number)
                    {
                        dict[propItem.Name] = propItem.Value.GetRawText();
                    }
                    else if (propItem.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        dict[propItem.Name] = propItem.Value.GetBoolean().ToString().ToLowerInvariant();
                    }
                    else
                    {
                        throw new ArgumentException($"Property '{propItem.Name}' in '{name}' must be a scalar string, number, or boolean.");
                    }
                }

                if (dict.Count == 0)
                {
                    throw new ArgumentException($"Parameter '{name}' cannot be an empty dictionary.");
                }

                return dict;
            }
        }

        string primaryName = propertyNames.FirstOrDefault() ?? "parameter";
        throw new ArgumentException($"Parameter '{primaryName}' must be provided in an arguments object.");
    }
}
