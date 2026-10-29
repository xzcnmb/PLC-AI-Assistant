// G2 download-closed-loop: the minimal, evidence-backed "编译产物 → 下载 →
// 装载触发 → 运行" call sequence against the loopback h5u simulator.
//
// Proven on 2026-10-29 (docs/inovance-autoshop-download-closedloop.md §4):
//   1. login + file-transfer of the repackaged project.hcpp (PPCH container)
//      under the remote name "project\project.hcpp" — landed byte-exact
//      (md5 f463cbaa… == source), daemon logged "Download … finished."
//   2. load trigger write 0xFF000010←1 answered the documented stub status
//      0x82 (load_down_file_start is a stub in the simulator — the downloaded
//      program is NOT activated; the running program is the factory default).
//   3. RUN write 0xFF000020←1 answered [06] and moved Modbus D100 0 → 4660,
//      i.e. the register view went LIVE (default app executing). STOP
//      (0xFF000020←0) is acked; retained values are not cleared (honest note).
//
// The vendor in-process MFC Download export (CCommunicateEth::Download) was
// proven a dead transport for this loop by the G2 standalone harness (rc=0
// accepted, ZERO file-transfer frames emitted, sentinel file unchanged —
// §3), so this sequence deliberately uses the D1 protocol layer instead.
//
// Red line: every socket here is created through the loopback-hard-checked
// clients (DownloadProtocolClient.Create / SimulatorRegisterClient.Create).
// Reverse-engineered, no vendor endorsement, version-locked to 4.12.0.2.

using PlcMcp.Engineering.Workers.AutoShop.DownloadProtocol;

namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

/// <summary>Result of the download+run closed-loop attempt (per-step receipts, never a fake success).</summary>
/// <param name="Downloaded">File transfer completed (session+chunks accepted; finish is one-way).</param>
/// <param name="DownloadDetail">Transport receipt text.</param>
/// <param name="LoadTrigger">Reply of the 0xFF000010 load-trigger write (simulator: 0x82 stub status).</param>
/// <param name="Run">Reply of the 0xFF000020 RUN write.</param>
/// <param name="Stop">Reply of the follow-up STOP write (may be null when skipped).</param>
/// <param name="Steps">Human-readable ordered step log.</param>
public readonly record struct DownloadClosedLoopResult(
    bool Downloaded,
    string DownloadDetail,
    SimulatorControlReply? LoadTrigger,
    SimulatorControlReply? Run,
    SimulatorControlReply? Stop,
    IReadOnlyList<string> Steps);

/// <summary>
/// Orchestrates the loopback download→run sequence. Testable offline: the
/// register channel and the download step are injected, so unit tests can
/// replay the exact bytes/responses captured against the real daemon.
/// </summary>
public static class AutoShopDownloadClosedLoop
{
    /// <summary>
    /// Executes: download(plan file 0) → trigger load → RUN → optional STOP.
    /// </summary>
    /// <param name="downloadAsync">Transfer step (in production: DownloadProtocolClient.LoginAsync + DownloadFileAsync).</param>
    /// <param name="control">Register channel (in production: SimulatorRegisterClient).</param>
    /// <param name="plan">Download plan; the first entry is transferred.</param>
    /// <param name="includeStop">Issue a STOP after RUN (daemon retains last values — evidence, not a reset).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<DownloadClosedLoopResult> RunAsync(
        Func<DownloadPayloadFile, CancellationToken, Task<(bool Ok, string Detail)>> downloadAsync,
        ISimulatorControlChannel control,
        IReadOnlyList<DownloadPayloadFile> plan,
        bool includeStop = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloadAsync);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Count == 0)
        {
            throw new ArgumentException("download plan is empty", nameof(plan));
        }

        var steps = new List<string>();
        var first = plan[0];
        var (ok, detail) = await downloadAsync(first, cancellationToken).ConfigureAwait(false);
        steps.Add($"download {first.RemoteName}: {(ok ? "ok" : "failed")} — {detail}");

        SimulatorControlReply? load = null, run = null, stop = null;
        if (ok)
        {
            load = await control.SendAsync(SimulatorControlFrames.BuildLoadTrigger(), cancellationToken).ConfigureAwait(false);
            steps.Add($"load trigger (0xFF000010←1): {Describe(load)}");
            run = await control.SendAsync(SimulatorControlFrames.BuildRun(), cancellationToken).ConfigureAwait(false);
            steps.Add($"RUN (0xFF000020←1): {Describe(run)}");
            if (includeStop)
            {
                stop = await control.SendAsync(SimulatorControlFrames.BuildStop(), cancellationToken).ConfigureAwait(false);
                steps.Add($"STOP (0xFF000020←0): {Describe(stop)}");
            }
        }
        return new DownloadClosedLoopResult(ok, detail, load, run, stop, steps);
    }

    /// <summary>
    /// Production adapter: wires DownloadProtocolClient (loopback-checked file
    /// transport) with SimulatorRegisterClient (loopback-checked control channel).
    /// Both hard-refuse non-loopback endpoints at construction.
    /// </summary>
    public static async Task<DownloadClosedLoopResult> RunAgainstDaemonAsync(
        DownloadProtocolClient downloadClient,
        SimulatorRegisterClient controlClient,
        IReadOnlyList<DownloadPayloadFile> plan,
        bool includeStop = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloadClient);
        ArgumentNullException.ThrowIfNull(controlClient);
        return await RunAsync(
            async (file, ct) =>
            {
                await downloadClient.ConnectAsync(ct).ConfigureAwait(false);
                await downloadClient.LoginAsync("plcmcp", "0", ct).ConfigureAwait(false);
                var r = await downloadClient.DownloadFileAsync(file.RemoteName, file.Bytes, 256, ct).ConfigureAwait(false);
                return (r.Ok, r.Detail);
            },
            controlClient, plan, includeStop, cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(SimulatorControlReply? reply) => reply?.Kind switch
    {
        SimulatorControlKind.Ack => "acked [06]",
        SimulatorControlKind.Value => $"value={reply!.Value.Value}",
        SimulatorControlKind.Error => $"daemon status 0x{reply!.Value.Status:X2}",
        _ => "no/unknown answer",
    };
}
