using System.Net.Sockets;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Adapters.Protocols;

public abstract class ReadOnlyTcpAdapter : IPlcProtocolAdapter
{
    public abstract string AdapterId { get; }
    public abstract PlcVendor Vendor { get; }
    public abstract IReadOnlyList<TransportKind> Transports { get; }
    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; } =
    [
        new("read_tags", CapabilityStatus.Experimental, "Read-only protocol implementation tested with loopback fixtures; hardware qualification pending."),
        new("browse_symbols", CapabilityStatus.Supported, "Configured manifest only; not online symbol discovery."),
        new("diagnostics", CapabilityStatus.Experimental, "TCP reachability only; does not report CPU RUN/STOP."),
        new("write_tags", CapabilityStatus.Unsupported, "Physical writes are not implemented."),
        new("compile", CapabilityStatus.Unsupported, "Vendor engineering worker is not installed."),
        new("download", CapabilityStatus.Unsupported, "Vendor engineering worker is not installed.")
    ];

    public async Task<ProbeResult> ProbeAsync(TargetProfile target, CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(target.Endpoint.TimeoutMs);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(target.Endpoint.Host, target.Endpoint.Port, deadline.Token).ConfigureAwait(false);
            return new(target.Id, true, false, "TCP_CONNECTED", "TCP reachable; PLC identity, protocol and CPU mode have not been verified.", DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(target.Id, false, false, "TIMEOUT", "TCP connection timed out.", DateTimeOffset.UtcNow);
        }
        catch (SocketException ex)
        {
            return new(target.Id, false, false, "CONNECTION_FAILED", ex.SocketErrorCode.ToString(), DateTimeOffset.UtcNow);
        }
    }

    public async Task<IReadOnlyList<TagValue>> ReadAsync(TargetProfile target, IReadOnlyList<TagDefinition> tags,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        if (tags.Count is < 1 or > 128) throw new ArgumentException("Read batches must contain 1 to 128 tags.");
        foreach (var tag in tags)
        {
            if (!tag.CanRead) throw new InvalidOperationException($"Tag '{tag.Name}' is not readable.");
            ValidateTag(tag);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(target.Endpoint.TimeoutMs);
        using var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(target.Endpoint.Host, target.Endpoint.Port, deadline.Token).ConfigureAwait(false);
            return await ReadConnectedAsync(client.GetStream(), target, tags, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("PLC read timed out; connection was closed. No write was attempted.");
        }
    }

    protected virtual void ValidateTag(TagDefinition tag) => WireValue.Size(tag.DataType);
    protected abstract Task<IReadOnlyList<TagValue>> ReadConnectedAsync(NetworkStream stream, TargetProfile target,
        IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken);

    protected void ValidateTarget(TargetProfile target)
    {
        if (target.IsSimulation || !Transports.Contains(target.Endpoint.Transport))
            throw new ArgumentException("Target transport does not match this physical adapter.");
        if (string.IsNullOrWhiteSpace(target.Endpoint.Host) || target.Endpoint.Port is < 1 or > 65535 ||
            target.Endpoint.TimeoutMs is < 100 or > 30000)
            throw new ArgumentException("Invalid endpoint or timeout (100 to 30000 ms required).");
    }

    protected static async Task<byte[]> ReceiveAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
    {
        if (count is < 1 or > 65536) throw new InvalidDataException("Response frame length is outside the allowed range.");
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    protected static TagValue Value(TagDefinition tag, object value) =>
        new(tag.Name, value, tag.DataType, tag.Unit, QualityCode.Good, DateTimeOffset.UtcNow, tag.NativeAddress);
}
