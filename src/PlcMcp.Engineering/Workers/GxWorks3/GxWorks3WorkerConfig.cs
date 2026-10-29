using PlcMcp.Contracts.Models;

namespace PlcMcp.Engineering.Workers.GxWorks3;

/// <summary>
/// Configuration for the Mitsubishi GX Works3 engineering worker.
/// </summary>
public sealed class GxWorks3WorkerConfig
{
    /// <summary>
    /// Absolute path to GXW3.exe (e.g. D:\gwork2\GPPW3\GXW3.exe).
    /// </summary>
    public string Gxw3ExePath { get; set; } = string.Empty;

    /// <summary>
    /// Installation root directory of GX Works3 or MELSOFT (e.g. D:\gwork2).
    /// </summary>
    public string InstallationRoot { get; set; } = string.Empty;

    /// <summary>
    /// Directory containing managed assemblies/plugins (e.g. D:\gwork2\GPPW3).
    /// </summary>
    public string ManagedDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Expected exact 4-part product or file version string of GX Works3 (e.g. "1.128.04519").
    /// </summary>
    public string? Gxw3Version { get; set; }

    /// <summary>
    /// Optional executable SHA-256 hash pin for GXW3.exe.
    /// </summary>
    public string? Gxw3ExeSha256 { get; set; }

    /// <summary>
    /// Optional absolute project root.
    /// </summary>
    public string? ProjectRoot { get; set; }

    /// <summary>
    /// Timeout in seconds for operations. Defaults to 60.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Hard cap on output bytes. Defaults to 10 MB.
    /// </summary>
    public long MaxOutputBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Target PLC vendor.
    /// </summary>
    public PlcVendor Vendor => PlcVendor.Mitsubishi;
}
