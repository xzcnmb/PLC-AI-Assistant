using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Workspace;

namespace PlcMcp.Engineering.Workers.Siemens;

public sealed class SiemensSmartEngineeringWorker : IEngineeringWorker
{
    public PlcVendor Vendor => PlcVendor.Siemens;
    public string Name => "Siemens-MicroWinSmart-NativeWorker";

    private readonly IProjectWorkspaceManager _workspaceManager;
    private readonly IVendorDoctor _doctor;
    private readonly ISiemensSmartBridge _bridge;
    private readonly SiemensSmartBridgeConfig _config;

    public bool IsAvailable
    {
        get
        {
            var doctorReport = _doctor.CheckSoftware(VendorSoftwareKind.MicroWinSmart);
            return doctorReport.Installed && _bridge.IsAvailable;
        }
    }

    public SiemensSmartEngineeringWorker(
        IProjectWorkspaceManager workspaceManager,
        IVendorDoctor doctor,
        ISiemensSmartBridge? bridge = null,
        SiemensSmartBridgeConfig? config = null)
    {
        _workspaceManager = workspaceManager;
        _doctor = doctor;
        _config = config ?? new SiemensSmartBridgeConfig();
        _bridge = bridge ?? new SiemensSmartBridge(_config);
    }

    public async Task<EngineeringExecutionResult> ExecuteAsync(
        EngineeringJobRequest job,
        CancellationToken cancellationToken = default)
    {
        // 1. Safety check: Guard against prohibited operations
        if (IsProhibitedOperation(job, out string safetyReason))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Operation refused by safety policy.",
                Details: safetyReason);
        }

        // 2. Validate parameters and paths
        if (string.IsNullOrWhiteSpace(job.ProjectPath))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Project path cannot be null or empty.");
        }

        string fullSourcePath;
        try
        {
            fullSourcePath = ValidateAndSanitizePath(job.ProjectPath);
        }
        catch (Exception ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Path validation failed: {ex.Message}");
        }

        if (!File.Exists(fullSourcePath))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Target project file does not exist: {fullSourcePath}");
        }

        // 3. Pre-flight host capability check
        if (!IsAvailable)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Siemens STEP 7-MicroWIN SMART or smart200_mcp runtime is not available on this host.",
                Details: "Requires STEP 7-MicroWIN SMART installation and Python smart200_mcp environment.");
        }

        // 4. Capture original source SHA-256 for strict non-tampering verification
        string originalSourceSha256 = _workspaceManager.ComputeSha256(fullSourcePath);

        try
        {
            return job.JobType switch
            {
                EngineeringJobType.InspectProject => await HandleInspectProjectAsync(job, fullSourcePath, cancellationToken),
                EngineeringJobType.ExportPou => await HandleExportPouAsync(job, fullSourcePath, cancellationToken),
                EngineeringJobType.ValidatePou => await HandleValidatePouAsync(job, fullSourcePath, cancellationToken),
                EngineeringJobType.CompileProject => await HandleCompileProjectAsync(job, fullSourcePath, cancellationToken),
                _ => new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Unsupported,
                    Message: $"Job type '{job.JobType}' is not supported by Siemens SMART worker.",
                    Details: "Supported jobs: InspectProject, ExportPou, ValidatePou, CompileProject.")
            };
        }
        catch (TimeoutException ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"Smart bridge operation timed out: {ex.Message}",
                Details: ex.ToString());
        }
        catch (JsonException ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"Invalid JSON response from Smart backend: {ex.Message}",
                Details: ex.ToString());
        }
        catch (IOException ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"I/O failure during execution: {ex.Message}",
                Details: ex.ToString());
        }
        catch (InvalidOperationException ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"Smart bridge operation failed: {ex.Message}",
                Details: ex.ToString());
        }
        catch (Exception ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"Execution failed: {ex.Message}",
                Details: ex.ToString());
        }
        finally
        {
            // CRITICAL VERIFICATION: Ensure original source file is byte-for-byte identical
            try
            {
                _workspaceManager.VerifyIntegrity(fullSourcePath, originalSourceSha256);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"CRITICAL SECURITY FAILURE: Original source file '{fullSourcePath}' was modified! " + ex.Message, ex);
            }
        }
    }

    private static readonly Regex SafeBlockNameRegex = new(@"^[\p{L}\p{N}_ -]+$", RegexOptions.Compiled);
    private static readonly Regex SafeSymbolNameRegex = new(@"^[\p{L}\p{N}_ -]{1,128}$", RegexOptions.Compiled);
    private static readonly Regex SafeAddressRegex = new(@"^[A-Za-z][A-Za-z0-9._]{0,31}$", RegexOptions.Compiled);

    public static bool IsValidBlockName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.Length > 128)
            return false;

        foreach (char c in name)
        {
            if (c < 0x20 || c == '|' || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>')
                return false;
        }

        return SafeBlockNameRegex.IsMatch(name.Trim());
    }

    public static bool IsValidSymbolName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        foreach (char c in name)
        {
            if (c < 0x20 || c == '|' || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == ';')
                return false;
        }

        return SafeSymbolNameRegex.IsMatch(name.Trim());
    }

    public static bool IsValidSymbolAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        return SafeAddressRegex.IsMatch(address.Trim());
    }

    private static bool IsProhibitedOperation(EngineeringJobRequest job, out string reason)
    {
        if (job.Options != null)
        {
            foreach (var key in job.Options.Keys)
            {
                string lowerKey = key.ToLowerInvariant();
                string lowerVal = job.Options[key]?.ToLowerInvariant() ?? "";
                if (lowerKey.Contains("download") || lowerVal.Contains("download") ||
                    lowerKey.Contains("run") || lowerKey.Contains("stop") ||
                    lowerKey.Contains("force") || lowerVal.Contains("force") ||
                    lowerKey.Contains("touch_plc") || lowerKey.Contains("attach_ui") ||
                    lowerKey.Contains("upload"))
                {
                    reason = $"Prohibited operation key/value detected: '{key}'. Worker is strictly isolated and forbidden from touching PLC hardware or user UI windows.";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }

    private string ValidateAndSanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is empty.", nameof(path));

        foreach (char c in path)
        {
            if (c < 0x20 && c != '\t')
                throw new ArgumentException($"Path contains invalid control character (code: {(int)c}).", nameof(path));
        }

        if (_config.AllowedWorkspaceRoots == null || _config.AllowedWorkspaceRoots.Count == 0)
        {
            throw new UnauthorizedAccessException("No allowed workspace roots configured for Siemens SMART worker (fail closed). Explicit AllowedWorkspaceRoots configuration is required.");
        }

        string? sanitized = null;
        foreach (var root in _config.AllowedWorkspaceRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            try
            {
                sanitized = ProjectWorkspaceManager.SanitizePath(path, root);
                break;
            }
            catch (UnauthorizedAccessException)
            {
                // Not in this root, continue to next
            }
        }

        if (sanitized == null)
        {
            throw new UnauthorizedAccessException($"Path traversal or unauthorized root: '{path}' is not within any allowed workspace root.");
        }

        return sanitized;
    }

    private async Task<EngineeringExecutionResult> HandleInspectProjectAsync(
        EngineeringJobRequest job,
        string fullSourcePath,
        CancellationToken cancellationToken)
    {
        // For inspect/overview, we use isolated working copy
        var snapshot = _workspaceManager.CreateWorkingCopy(fullSourcePath, readOnly: true);

        var args = new Dictionary<string, string>
        {
            ["path"] = snapshot.WorkingPath
        };

        var timeout = TimeSpan.FromSeconds(_config.StepTimeoutSeconds);
        string responseJson = await _bridge.ExecuteCommandAsync("overview", args, timeout, cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        if (!success)
        {
            string err = root.TryGetProperty("error", out var e) ? e.GetString() ?? "Unknown error" : "Unknown error";
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"Inspect project failed: {err}",
                Details: $"Working copy at {snapshot.WorkingPath}");
        }

        string format = root.TryGetProperty("format", out var f) ? f.GetString() ?? "V2" : "V2";
        bool offlineParsable = true;
        if (root.TryGetProperty("offline_parsable", out var op))
        {
            offlineParsable = op.GetBoolean();
        }
        else if (format.Contains("V3", StringComparison.OrdinalIgnoreCase))
        {
            offlineParsable = false;
        }

        string limitations = root.TryGetProperty("limitations", out var lim) ? lim.GetString() ?? "" : "";
        string reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";

        if (!offlineParsable)
        {
            string limitDesc = !string.IsNullOrWhiteSpace(limitations)
                ? limitations
                : "V3 (.smartV3) data segment is encrypted. Offline overview is unsupported.";
            if (!string.IsNullOrWhiteSpace(reason))
            {
                limitDesc += $" Reason: {reason}";
            }

            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Offline project overview is unsupported for V3 project: {limitDesc}",
                Details: limitDesc);
        }

        string? version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        string? projName = root.TryGetProperty("project_name", out var pn) ? pn.GetString() : Path.GetFileNameWithoutExtension(fullSourcePath);
        int symCount = root.TryGetProperty("symbol_count", out var sc) ? sc.GetInt32() : 0;

        var pous = new List<EngineeringPou>();
        if (root.TryGetProperty("pou_names", out var pnArray) && pnArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in pnArray.EnumerateArray())
            {
                string? pName = elem.GetString();
                if (!string.IsNullOrWhiteSpace(pName))
                {
                    pous.Add(new EngineeringPou(
                        Name: pName,
                        PouType: PouType.Program,
                        Language: "LAD/STL",
                        BodyText: null,
                        Variables: Array.Empty<EngineeringSymbol>()));
                }
            }
        }

        var parsed = new ParsedProject(
            ProjectName: projName ?? Path.GetFileNameWithoutExtension(fullSourcePath),
            Format: $"Siemens-S7-200-SMART-{format}",
            Symbols: Array.Empty<EngineeringSymbol>(),
            Pous: pous,
            SourceSha256: snapshot.Sha256);

        return new EngineeringExecutionResult(
            JobId: job.JobId,
            Success: true,
            Status: CapabilityStatus.Supported,
            Message: $"Project inspected successfully. Format: {format}, Symbol count: {symCount}, POU count: {pous.Count}.",
            ParsedProject: parsed,
            Details: $"Limitations: {limitations}; SourceSha256: {snapshot.Sha256}");
    }

    private async Task<EngineeringExecutionResult> HandleExportPouAsync(
        EngineeringJobRequest job,
        string fullSourcePath,
        CancellationToken cancellationToken)
    {
        string? blockName = job.Options != null && job.Options.TryGetValue("blockName", out var bn) ? bn?.Trim() : null;
        if (string.IsNullOrWhiteSpace(blockName))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Option 'blockName' is required for ExportPou job.");
        }

        if (!IsValidBlockName(blockName))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Invalid blockName '{blockName}'. Only alphanumeric characters, Chinese characters, underscore, hyphen, and spaces are allowed.");
        }

        // Isolated working copy
        var snapshot = _workspaceManager.CreateWorkingCopy(fullSourcePath, readOnly: false);
        string exportOutDir = Path.Combine(Path.GetDirectoryName(snapshot.WorkingPath)!, "export_out");
        Directory.CreateDirectory(exportOutDir);
        string targetAwlPath = Path.Combine(exportOutDir, $"{blockName}.awl");

        try
        {
            targetAwlPath = ProjectWorkspaceManager.SanitizePath(targetAwlPath, exportOutDir);
        }
        catch (Exception ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Target AWL path validation failed: {ex.Message}");
        }

        var namesToPaths = new Dictionary<string, string>
        {
            [blockName] = targetAwlPath
        };

        var args = new Dictionary<string, string>
        {
            ["project_path"] = snapshot.WorkingPath,
            ["names_to_paths"] = JsonSerializer.Serialize(namesToPaths)
        };

        var timeout = TimeSpan.FromSeconds(_config.StepTimeoutSeconds);
        string responseJson = await _bridge.ExecuteCommandAsync("export_blocks", args, timeout, cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        if (!success || !File.Exists(targetAwlPath))
        {
            string err = root.TryGetProperty("error", out var e) ? e.GetString() ?? "Export failed" : "Export failed";
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"Export POU failed: {err}");
        }

        // Run AWL analysis and STL check on the exported file
        var analyzeArgs = new Dictionary<string, string> { ["awl_path"] = targetAwlPath };
        string analyzeJson = await _bridge.ExecuteCommandAsync("analyze_awl", analyzeArgs, timeout, cancellationToken);

        var outputs = new Dictionary<string, string>
        {
            [blockName] = targetAwlPath
        };

        return new EngineeringExecutionResult(
            JobId: job.JobId,
            Success: true,
            Status: CapabilityStatus.Supported,
            Message: $"Successfully exported POU '{blockName}' to {targetAwlPath}",
            ExportedOutputs: outputs,
            Details: analyzeJson);
    }

    private async Task<EngineeringExecutionResult> HandleValidatePouAsync(
        EngineeringJobRequest job,
        string fullSourcePath,
        CancellationToken cancellationToken)
    {
        string? blockNamesStr = job.Options != null && job.Options.TryGetValue("blockNames", out var bns) ? bns : null;
        if (string.IsNullOrWhiteSpace(blockNamesStr))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Option 'blockNames' (comma separated) is required for ValidatePou job.");
        }

        var blockNames = blockNamesStr.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (blockNames.Length == 0)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Option 'blockNames' contains no valid block names.");
        }

        foreach (var bn in blockNames)
        {
            if (!IsValidBlockName(bn))
            {
                return new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Unsupported,
                    Message: $"Invalid block name '{bn}'. Only alphanumeric characters, Chinese characters, underscore, hyphen, and spaces are allowed.");
            }
        }

        string normalizedBlockNamesStr = string.Join(',', blockNames);

        // Isolated working copy
        var snapshot = _workspaceManager.CreateWorkingCopy(fullSourcePath, readOnly: true);

        var args = new Dictionary<string, string>
        {
            ["project_path"] = snapshot.WorkingPath,
            ["block_names"] = normalizedBlockNamesStr
        };

        var timeout = TimeSpan.FromSeconds(_config.StepTimeoutSeconds);
        string responseJson = await _bridge.ExecuteCommandAsync("validate_project", args, timeout, cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        if (!success)
        {
            string err = root.TryGetProperty("error", out var e) ? e.GetString() ?? "Validation failed" : "Validation failed";
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"Engine validation failed: {err}");
        }

        bool allValid = root.TryGetProperty("all_valid", out var av) && av.GetBoolean();
        bool completed = root.TryGetProperty("completed", out var cp) && cp.GetBoolean();

        var diagnostics = new List<StaticDiagnostic>();

        if (!completed)
        {
            diagnostics.Add(new StaticDiagnostic(
                Code: "SMART_VALIDATE_INCOMPLETE",
                Message: "Validation process did not complete successfully.",
                Severity: DiagnosticSeverity.Error,
                Line: 1,
                Column: 1,
                RuleName: "Siemens.MicroWinSmart.POU_IsValidNet"));
        }

        bool hasBlocks = false;
        if (root.TryGetProperty("blocks", out var blocksElem) && blocksElem.ValueKind == JsonValueKind.Object)
        {
            var props = blocksElem.EnumerateObject().ToList();
            if (props.Count > 0)
            {
                hasBlocks = true;
                foreach (var prop in props)
                {
                    string bName = prop.Name;
                    if (prop.Value.ValueKind != JsonValueKind.Object)
                    {
                        diagnostics.Add(new StaticDiagnostic(
                            Code: "SMART_VALIDATE_DATA_CORRUPT",
                            Message: $"POU '{bName}' block report is not a valid JSON object.",
                            Severity: DiagnosticSeverity.Error,
                            Line: 1,
                            Column: 1,
                            RuleName: "Siemens.MicroWinSmart.POU_IsValidNet"));
                        continue;
                    }

                    if (prop.Value.TryGetProperty("error", out var blockErr) &&
                        blockErr.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(blockErr.GetString()))
                    {
                        diagnostics.Add(new StaticDiagnostic(
                            Code: "SMART_BLOCK_ERROR",
                            Message: $"POU '{bName}' validation error: {blockErr.GetString()}",
                            Severity: DiagnosticSeverity.Error,
                            Line: 1,
                            Column: 1,
                            RuleName: "Siemens.MicroWinSmart.POU_IsValidNet"));
                    }

                    if (prop.Value.TryGetProperty("invalid", out var invArr))
                    {
                        if (invArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var invItem in invArr.EnumerateArray())
                            {
                                if (invItem.ValueKind == JsonValueKind.Number && invItem.TryGetInt32(out int netNum))
                                {
                                    diagnostics.Add(new StaticDiagnostic(
                                        Code: "SMART_INVALID_NET",
                                        Message: $"POU '{bName}' Network {netNum} was marked INVALID by Siemens POU_IsValidNet engine.",
                                        Severity: DiagnosticSeverity.Error,
                                        Line: netNum,
                                        Column: 1,
                                        RuleName: "Siemens.MicroWinSmart.POU_IsValidNet"));
                                }
                                else
                                {
                                    diagnostics.Add(new StaticDiagnostic(
                                        Code: "SMART_VALIDATE_INVALID_TYPE",
                                        Message: $"POU '{bName}' invalid network item is not an integer: '{invItem}'.",
                                        Severity: DiagnosticSeverity.Error,
                                        Line: 1,
                                        Column: 1,
                                        RuleName: "Siemens.MicroWinSmart.POU_IsValidNet"));
                                }
                            }
                        }
                        else
                        {
                            diagnostics.Add(new StaticDiagnostic(
                                Code: "SMART_VALIDATE_INVALID_TYPE",
                                Message: $"POU '{bName}' invalid field is not an array (type: {invArr.ValueKind}).",
                                Severity: DiagnosticSeverity.Error,
                                _line: 1, // Line
                                Column: 1,
                                RuleName: "Siemens.MicroWinSmart.POU_IsValidNet"));
                        }
                    }
                }
            }
        }

        if (!hasBlocks)
        {
            diagnostics.Add(new StaticDiagnostic(
                Code: "SMART_VALIDATE_NO_BLOCKS",
                Message: "Validation returned no block reports or blocks data was empty.",
                Severity: DiagnosticSeverity.Error,
                Line: 1,
                Column: 1,
                RuleName: "Siemens.MicroWinSmart.POU_IsValidNet"));
        }

        // Verify that each requested block was present in blocks output
        if (hasBlocks)
        {
            foreach (var reqBn in blockNames)
            {
                if (!blocksElem.TryGetProperty(reqBn, out _))
                {
                    diagnostics.Add(new StaticDiagnostic(
                        Code: "SMART_BLOCK_MISSING",
                        Message: $"Requested POU '{reqBn}' was not found in the validation block report.",
                        Severity: DiagnosticSeverity.Error,
                        Line: 1,
                        Column: 1,
                        RuleName: "Siemens.MicroWinSmart.POU_IsValidNet"));
                }
            }
        }

        bool passed = completed && allValid && hasBlocks && diagnostics.Count == 0;
        string summary = passed
            ? $"All specified blocks ({normalizedBlockNamesStr}) validated with 0 invalid networks."
            : $"Validation failed for blocks ({normalizedBlockNamesStr}): {diagnostics.Count} diagnostic issue(s) reported.";

        var staticCheck = new StaticCheckResult(
            Passed: passed,
            Diagnostics: diagnostics,
            Summary: summary,
            IsVendorCompiler: true,
            Disclaimer: "Authoritative validation directly from Siemens MicroWIN SMART engine (POU_IsValidNet).");

        return new EngineeringExecutionResult(
            JobId: job.JobId,
            Success: passed,
            Status: passed ? CapabilityStatus.Supported : CapabilityStatus.Experimental,
            Message: staticCheck.Summary,
            StaticCheck: staticCheck,
            Details: responseJson);
    }

    private async Task<EngineeringExecutionResult> HandleCompileProjectAsync(
        EngineeringJobRequest job,
        string fullSourcePath,
        CancellationToken cancellationToken)
    {
        // Compile verify MUST occur strictly on an isolated working copy
        var snapshot = _workspaceManager.CreateWorkingCopy(fullSourcePath, readOnly: false);
        string workingShaBefore = snapshot.Sha256;

        string? awlFilesStr = job.Options != null && job.Options.TryGetValue("awlFiles", out var af) ? af : null;
        string? symbolsJson = job.Options != null && job.Options.TryGetValue("symbols", out var sym) ? sym : "{}";

        if (string.IsNullOrWhiteSpace(awlFilesStr))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Option 'awlFiles' (| separated paths) is required for CompileProject verify flow.");
        }

        var rawAwlFiles = awlFilesStr.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (rawAwlFiles.Length == 0)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Option 'awlFiles' contains no valid file paths.");
        }

        var sanitizedAwlFiles = new List<string>();
        foreach (var awlFile in rawAwlFiles)
        {
            string sanitizedAwl;
            try
            {
                sanitizedAwl = ValidateAndSanitizePath(awlFile);
            }
            catch (Exception ex)
            {
                return new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Unsupported,
                    Message: $"AWL file path validation failed for '{awlFile}': {ex.Message}");
            }

            if (!File.Exists(sanitizedAwl))
            {
                return new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Unsupported,
                    Message: $"AWL file does not exist: '{sanitizedAwl}'");
            }

            sanitizedAwlFiles.Add(sanitizedAwl);
        }

        if (string.IsNullOrWhiteSpace(symbolsJson))
        {
            symbolsJson = "{}";
        }

        try
        {
            using var symDoc = JsonDocument.Parse(symbolsJson);
            if (symDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new EngineeringExecutionResult(
                    JobId: job.JobId,
                    Success: false,
                    Status: CapabilityStatus.Unsupported,
                    Message: "Option 'symbols' must be a JSON object.");
            }

            foreach (var prop in symDoc.RootElement.EnumerateObject())
            {
                string key = prop.Name;
                if (!IsValidSymbolName(key))
                {
                    return new EngineeringExecutionResult(
                        JobId: job.JobId,
                        Success: false,
                        Status: CapabilityStatus.Unsupported,
                        Message: $"Invalid symbol key '{key}'. Symbol names must be 1-128 characters without control characters or separators.");
                }

                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    return new EngineeringExecutionResult(
                        JobId: job.JobId,
                        Success: false,
                        Status: CapabilityStatus.Unsupported,
                        Message: $"Invalid symbol value for '{key}'. Symbol address must be a string.");
                }

                string val = prop.Value.GetString() ?? "";
                if (!IsValidSymbolAddress(val))
                {
                    return new EngineeringExecutionResult(
                        JobId: job.JobId,
                        Success: false,
                        Status: CapabilityStatus.Unsupported,
                        Message: $"Invalid symbol address '{val}' for '{key}'. Address must be a valid PLC address format.");
                }
            }
        }
        catch (JsonException ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Option 'symbols' is not valid JSON: {ex.Message}");
        }

        var args = new Dictionary<string, string>
        {
            ["project_path"] = snapshot.WorkingPath,
            ["awl_files"] = string.Join('|', sanitizedAwlFiles),
            ["symbols"] = symbolsJson
        };

        var timeout = TimeSpan.FromSeconds(_config.StepTimeoutSeconds);
        string responseJson = await _bridge.ExecuteCommandAsync("deploy_verify", args, timeout, cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        if (!success)
        {
            string err = root.TryGetProperty("error", out var e) ? e.GetString() ?? "Deploy failed" : "Deploy failed";
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"Compile and deploy verification failed: {err}");
        }

        var rep = root.GetProperty("report");
        string stage1 = rep.TryGetProperty("stage1_structure", out var s1) ? s1.GetString() ?? "FAIL" : "FAIL";
        string stage2 = rep.TryGetProperty("stage2_compile", out var s2) ? s2.GetString() ?? "FAIL" : "FAIL";
        string stage3 = rep.TryGetProperty("stage3_engine_validate", out var s3) ? s3.GetString() ?? "FAIL" : "FAIL";
        string stage4 = rep.TryGetProperty("stage4_roundtrip", out var s4) ? s4.GetString() ?? "FAIL" : "FAIL";
        string stage5 = rep.TryGetProperty("stage5_persisted", out var s5) ? s5.GetString() ?? "FAIL" : "FAIL";
        bool passed = rep.TryGetProperty("passed", out var p) && p.GetBoolean();

        // Working copy hash verification
        string workingShaAfter = _workspaceManager.ComputeSha256(snapshot.WorkingPath);

        // Crucial requirement: NEVER claim compile success unless invalid networks == 0,
        // compile passes, and roundtrip + persistence are confirmed!
        bool honestPassed = passed &&
                            stage1 == "PASS" &&
                            stage2 == "PASS" &&
                            stage3 == "PASS" &&
                            stage4 == "PASS" &&
                            stage5 == "PASS" &&
                            workingShaAfter != workingShaBefore;

        var diagnostics = new List<StaticDiagnostic>();
        if (!honestPassed)
        {
            diagnostics.Add(new StaticDiagnostic(
                Code: "SMART_VERIFY_FAILED",
                Message: $"5-stage verify: Stage1(Structure)={stage1}, Stage2(Compile)={stage2}, Stage3(POU_IsValidNet)={stage3}, Stage4(Roundtrip)={stage4}, Stage5(Persisted)={stage5}.",
                Severity: DiagnosticSeverity.Error,
                Line: 1,
                Column: 1,
                RuleName: "Siemens.MicroWinSmart.FiveStageVerification"));
        }

        var staticCheck = new StaticCheckResult(
            Passed: honestPassed,
            Diagnostics: diagnostics,
            Summary: honestPassed
                ? "5-stage verification PASSED: structure, compile, POU_IsValidNet (0 invalid networks), roundtrip, and disk persistence confirmed."
                : $"5-stage verification FAILED. S1:{stage1}, S2:{stage2}, S3:{stage3}, S4:{stage4}, S5:{stage5}.",
            IsVendorCompiler: true,
            Disclaimer: "Full 5-stage verification by Siemens STEP 7-MicroWIN SMART engine.");

        return new EngineeringExecutionResult(
            JobId: job.JobId,
            Success: honestPassed,
            Status: CapabilityStatus.Supported,
            Message: staticCheck.Summary,
            StaticCheck: staticCheck,
            Details: responseJson);
    }
}
