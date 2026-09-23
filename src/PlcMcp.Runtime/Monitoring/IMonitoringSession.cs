using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime.Monitoring;

public interface IMonitoringSession : IDisposable, IAsyncDisposable
{
    string SessionId { get; }

    MonitoringSessionInfo Info { get; }

    MonitoringStatus Status { get; }

    IReadOnlyList<MonitoringSample> GetBufferedSamples();

    MonitoringSample? GetLatestSample();

    void Stop();

    Task Completion { get; }
}
