namespace PlcMcp.Runtime.Monitoring;

public static class TimeProviderExtensions
{
    public static async Task DelayAsync(
        this TimeProvider timeProvider,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        using var timer = timeProvider.CreateTimer(_ => tcs.TrySetResult(), null, delay, Timeout.InfiniteTimeSpan);

        await tcs.Task.ConfigureAwait(false);
    }
}
