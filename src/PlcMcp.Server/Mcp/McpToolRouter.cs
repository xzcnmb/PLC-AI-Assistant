using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Adapters;
using PlcMcp.Contracts.Models;
using PlcMcp.Core.Catalog;
using PlcMcp.Runtime;
using PlcMcp.Server.Governance;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;

namespace PlcMcp.Server.Mcp;

public sealed class McpToolRouter
{
    private readonly PlcRuntimeHost _host;
    private readonly PlcRuntimeService _service;
    private readonly ServerGovernanceServices _governance;
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object>>> _handlers;
    private readonly List<ToolDescriptor> _descriptors;

    public McpToolRouter(PlcRuntimeHost host, ServerGovernanceServices? governance = null)
    {
        _host = host;
        _service = host.Service;
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
                        description = "Optional keyword to filter tag names or aliases."
                    }
                },
                required = new[] { "targetId" }
            },
            handler: HandleListTagsAsync);

        Register(
            name: "plc_probe_target",
            description: "Probes a target to verify reachability and runtime communication state.",
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
                        description = "Target identifier to probe (e.g. 'sim-siemens')."
                    }
                },
                required = new[] { "targetId" }
            },
            handler: HandleProbeTargetAsync);

        Register(
            name: "plc_read_tags",
            description: "Reads current values, data types, and quality codes for specified tags on a target.",
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
                    },
                    names = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Alias for 'tags'."
                    }
                },
                required = new[] { "targetId" }
            },
            handler: HandleReadTagsAsync);

        Register(
            name: "plc_plan_write",
            description: "Creates a safety-checked write plan with state hash fingerprint and approval token, without modifying PLC state immediately.",
            readOnly: false,
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
                        description = "Dictionary of tag names to requested new values, e.g. {\"PressureSetpoint\": 5.5}."
                    },
                    lifetimeMinutes = new
                    {
                        type = "number",
                        description = "Optional plan validity duration in minutes (default: 5)."
                    }
                },
                required = new[] { "targetId", "changes" }
            },
            handler: HandlePlanWriteAsync);

        Register(
            name: "plc_apply_write",
            description: "Applies an approved write plan using its plan ID and approval token after verifying state hash consistency.",
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
                        description = "The plan ID generated by plc_plan_write."
                    },
                    approvalToken = new
                    {
                        type = "string",
                        description = "The approval token generated by plc_plan_write."
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
        Register("plc_lint_program", "Runs heuristic ST/SCL precheck; this is not vendor compiler validation.", true, false,
            new { type = "object", properties = new { source = new { type = "string" }, filePath = new { type = "string" } }, required = Array.Empty<string>() }, HandleLintProgramAsync);
        Register("plc_compare_projects", "Compares two supported PLCopen XML projects offline.", true, false,
            new { type = "object", properties = new { leftPath = new { type = "string" }, rightPath = new { type = "string" } }, required = new[] { "leftPath", "rightPath" } }, HandleCompareProjectsAsync);
        Register("plc_get_capabilities_report", "Returns capability evidence and local doctor status for a target.", true, false,
            new { type = "object", properties = new { targetId = new { type = "string" } }, required = new[] { "targetId" } }, HandleCapabilityReportAsync);
        Register("plc_get_job", "Returns a persisted engineering/deployment job and its state.", true, false,
            new { type = "object", properties = new { jobId = new { type = "string" } }, required = new[] { "jobId" } }, HandleGetJobAsync);
        Register("plc_get_audit", "Verifies and reads the append-only audit hash chain.", true, false,
            new { type = "object", properties = new { verify = new { type = "boolean" } }, required = Array.Empty<string>() }, HandleGetAuditAsync);
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

    private Task<object> HandleDoctorAsync(JsonElement args, CancellationToken cancellationToken) =>
        Task.FromResult<object>(_governance.Doctor.RunFullDiagnosis());

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

    private Task<object> HandleLintProgramAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var source = TryGetString(args, "source");
        if (string.IsNullOrWhiteSpace(source))
        {
            var filePath = RequireString(args, "filePath", "file_path");
            source = File.ReadAllText(filePath);
        }
        return Task.FromResult<object>(_governance.StAnalyzer.Analyze(source));
    }

    private Task<object> HandleCompareProjectsAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var left = _governance.Plcopen.ParseFile(RequireString(args, "leftPath", "left_path"));
        var right = _governance.Plcopen.ParseFile(RequireString(args, "rightPath", "right_path"));
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
        string approvalToken = RequireString(args, "approvalToken", "approval_token", "token");

        var applyResult = await _service.ApplyWriteAsync(planId, approvalToken, cancellationToken).ConfigureAwait(false);
        return applyResult;
    }

    private static object CreateToolCallResult(bool isError, object payload, string? errorText = null)
    {
        string jsonText = JsonSerializer.Serialize(payload, JsonRpc.SerializerOptions);
        JsonObject? node = null;
        try
        {
            node = JsonSerializer.Deserialize<JsonObject>(jsonText, JsonRpc.SerializerOptions);
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
                        return [s];
                    }
                    throw new ArgumentException($"Parameter '{name}' cannot be empty string.");
                }

                throw new ArgumentException($"Parameter '{name}' must be a string array or string.");
            }
        }

        string fallbackName = propertyNames.FirstOrDefault() ?? "parameter";
        throw new ArgumentException($"Parameter '{fallbackName}' is required and must be a non-empty array of strings.");
    }

    private static IReadOnlyDictionary<string, object?> RequireDictionary(JsonElement args, params string[] propertyNames)
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
                if (prop.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in prop.EnumerateObject())
                    {
                        dict[property.Name] = ConvertJsonElement(property.Value);
                    }

                    if (dict.Count == 0)
                    {
                        throw new ArgumentException($"Parameter '{name}' cannot be an empty object.");
                    }

                    return dict;
                }

                throw new ArgumentException($"Parameter '{name}' must be an object with tag changes.");
            }
        }

        string fallbackName = propertyNames.FirstOrDefault() ?? "parameter";
        throw new ArgumentException($"Parameter '{fallbackName}' is required and must be an object with tag changes.");
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
                if (prop.ValueKind == JsonValueKind.Number)
                {
                    return prop.GetDouble();
                }
                if (prop.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }
                if (prop.ValueKind == JsonValueKind.String &&
                    double.TryParse(prop.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
                throw new ArgumentException($"Parameter '{name}' must be a numeric value.");
            }
        }

        return null;
    }

    private static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetInt64(out var i) => i,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null => null,
        _ => throw new ArgumentException($"Unsupported JSON value type '{element.ValueKind}' in parameter payload.")
    };
}
