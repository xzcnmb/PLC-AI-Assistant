using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime.Monitoring;

public interface IMonitoringService : IDisposable, IAsyncDisposable
{
    Task<MonitoringWindowResult> SampleWindowAsync(
        MonitoringRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<MonitoringSample> StreamSamplesAsync(
        MonitoringRequest request,
        CancellationToken cancellationToken = default);

    IMonitoringSession StartSession(
        MonitoringRequest request);

    IMonitoringSession? GetSession(string sessionId);

    IReadOnlyList<MonitoringSessionInfo> ListSessions();

    bool StopSession(string sessionId);
}
