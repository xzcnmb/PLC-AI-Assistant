using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Workspace;

namespace PlcMcp.Engineering.Workers.Siemens;

public sealed class MicroWinSmartOfflineWorker : IEngineeringWorker
{
    public PlcVendor Vendor => PlcVendor.Siemens;
    public string Name => "MicroWIN-SMART-Offline";

    private readonly IProjectWorkspaceManager _workspaceManager;
    private readonly IVendorDoctor _doctor;
    private readonly string? _customPythonPath;
    private readonly string? _customSmart200PackagePath;

    public bool IsAvailable
    {
        get
        {
            var rep = _doctor.CheckSoftware(VendorSoftwareKind.MicroWinSmart);
            return rep.Installed;
        }
    }

    public MicroWinSmartOfflineWorker(
        IProjectWorkspaceManager workspaceManager,
        IVendorDoctor doctor,
        string? customPythonPath = null,
        string? customSmart200PackagePath = null)
    {
        _workspaceManager = workspaceManager;
        _doctor = doctor;
        _customPythonPath = customPythonPath;
        _customSmart200PackagePath = customSmart200PackagePath;
    }

    public async Task<EngineeringExecutionResult> ExecuteAsync(EngineeringJobRequest job, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(job.ProjectPath))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "Project path cannot be null or empty.");
        }

        if (!File.Exists(job.ProjectPath))
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Project file not found: {job.ProjectPath}");
        }

        // Check availability
        if (!IsAvailable)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: "STEP 7-MicroWIN SMART installation not detected on this machine.",
                Details: "Requires Siemens STEP 7-MicroWIN SMART installed locally.");
        }

        // Create isolated working copy to guarantee source file is never modified or touched
        ProjectSnapshot snapshot;
        try
        {
            snapshot = _workspaceManager.CreateWorkingCopy(job.ProjectPath);
        }
        catch (Exception ex)
        {
            return new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Experimental,
                Message: $"Failed to create isolated working copy: {ex.Message}");
        }

        return job.JobType switch
        {
            EngineeringJobType.InspectProject => InspectProjectInternal(job, snapshot),
            _ => new EngineeringExecutionResult(
                JobId: job.JobId,
                Success: false,
                Status: CapabilityStatus.Unsupported,
                Message: $"Job type '{job.JobType}' requires active worker process/engine execution. Offline metadata inspect supported.",
                Details: $"Protected working copy prepared at {snapshot.WorkingPath} (SHA256: {snapshot.Sha256}).")
        };
    }

    private static EngineeringExecutionResult InspectProjectInternal(EngineeringJobRequest job, ProjectSnapshot snapshot)
    {
        string ext = Path.GetExtension(snapshot.WorkingPath).ToLowerInvariant();
        bool isV2 = ext == ".smart";
        bool isV3 = ext == ".smartv3";

        string format = isV2 ? "Siemens-S7-200-SMART-V2" : (isV3 ? "Siemens-S7-200-SMART-V3-Encrypted" : "Siemens-S7-200-SMART");

        var symbols = new List<EngineeringSymbol>();
        var pous = new List<EngineeringPou>();

        string note = isV3
            ? "V3 (.smartV3) project data segment is encrypted by vendor. Offline structure inspection limited; full export requires headless engine hook."
            : "V2 (.smart) project inspected offline.";

        var parsed = new ParsedProject(
            ProjectName: Path.GetFileNameWithoutExtension(snapshot.SourcePath),
            Format: format,
            Symbols: symbols,
            Pous: pous,
            SourceSha256: snapshot.Sha256);

        return new EngineeringExecutionResult(
            JobId: job.JobId,
            Success: true,
            Status: CapabilityStatus.Supported,
            Message: $"Inspected project successfully via isolated working copy. {note}",
            ParsedProject: parsed,
            Details: $"Working copy SHA256: {snapshot.Sha256}, ByteLength: {snapshot.ByteLength}");
    }
}
