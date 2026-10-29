using System.Diagnostics;

namespace PlcMcp.Engineering.Workers.AutoShop.Simulator;

/// <summary>
/// Process abstraction used by <see cref="SimulatorLauncher"/> so start/stop logic can be
/// tested offline with fakes. Implementations must expose identity (PID, name, start time)
/// that later verifies ownership before any kill is attempted.
/// </summary>
public interface ISimulatorProcess : IDisposable
{
    int Id { get; }
    string ProcessName { get; }
    DateTime StartTimeUtc { get; }
    bool HasExited { get; }
    void Kill(bool entireProcessTree);
    bool WaitForExit(int milliseconds);
}

/// <summary>Start request handed to the injectable process starter.</summary>
public sealed record SimulatorProcessStartSpec(string ExecutablePath, string WorkingDirectory);

/// <summary>
/// Real process wrapper. Every property degrades safely on a dead/invalid handle: an
/// unreadable name or start time can never match the ownership record, so a stale or
/// PID-reused process is refused by the launcher instead of killed.
/// </summary>
internal sealed class SystemSimulatorProcess : ISimulatorProcess
{
    private readonly Process _process;

    public SystemSimulatorProcess(Process process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
    }

    public int Id
    {
        get { try { return _process.Id; } catch { return -1; } }
    }

    public string ProcessName
    {
        get { try { return _process.ProcessName; } catch { return string.Empty; } }
    }

    public DateTime StartTimeUtc
    {
        get
        {
            try
            {
                return _process.StartTime.ToUniversalTime();
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }

    public bool HasExited
    {
        get { try { return _process.HasExited; } catch { return true; } }
    }

    public void Kill(bool entireProcessTree)
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree);
        }
    }

    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

    public void Dispose() => _process.Dispose();
}
