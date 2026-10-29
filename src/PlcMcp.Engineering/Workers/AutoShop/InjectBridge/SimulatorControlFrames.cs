// G2 download-closed-loop: pure frame builders for the h5u simulator native
// protocol register chain (serverId = 0x01). These carry the RUN/STOP control
// word (command 0xFF000020) and the program-download status/load-poll command
// (0xFF000010) — the last two commands on the way "编译产物 → 下载 → 装载 → 运行".
//
// Every layout constant here was verified frame-for-frame against a locally
// started h5u_daemon.exe on 127.0.0.1:12939 on 2026-10-29 (see
// docs/inovance-autoshop-download-closedloop.md §4): the RUN writes answered
// [06] ACK, the STOP write answered [06], the load-trigger write answered the
// documented stub status [0F][00][0x82], and after RUN the Modbus readback of
// D100 moved 0 -> 4660 (registers alive). Reverse-engineered, no vendor
// endorsement, version-locked to the AutoShop 4.12.0.2 simulator binaries.
//
// Loopback red line: nothing in this file opens a socket; the frames are pure
// byte functions so they can be unit-tested offline. Callers must hard-check
// the destination address (127.0.0.0/8) exactly like DownloadProtocolClient.

using System.Buffers.Binary;
using PlcMcp.Engineering.Workers.AutoShop.DownloadProtocol;

namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

/// <summary>
/// Request/response codec for the register chain (serverId 1) commands that
/// drive the simulator run/stop and download-status state.
/// </summary>
public static class SimulatorControlFrames
{
    /// <summary>Register-chain payload sub-type byte for a READ command (observed working).</summary>
    public const byte ReadSubType = 0x01;

    /// <summary>Register-chain payload sub-type byte for a WRITE command (observed working).</summary>
    public const byte WriteSubType = 0x02;

    /// <summary>0xFF000010 — PLC program download status read / load trigger write (para_len 4).</summary>
    public const uint ProgramDownCommand = 0xFF000010;

    /// <summary>0xFF000020 — simulator RUN/STOP control word write (para_len 4).</summary>
    public const uint SimulatorControlCommand = 0xFF000020;

    /// <summary>Control word values as written to 0xFF000020 (daemon stores them verbatim).</summary>
    public const uint ControlStop = 0;
    public const uint ControlRun = 1;

    /// <summary>
    /// Builds a complete register-chain request frame: 8-byte header + payload
    /// <c>[sub][0][command LE][paraLen LE][para LE]</c>. All parameters use para_len = 4
    /// (the daemon handlers reject anything else with status 0x40).
    /// </summary>
    public static byte[] BuildRegisterCommand(byte subType, uint command, uint para)
    {
        if (subType != ReadSubType && subType != WriteSubType)
        {
            throw new ArgumentOutOfRangeException(nameof(subType),
                "register-chain sub-type must be 1 (read) or 2 (write)");
        }

        var payload = new byte[14];
        payload[0] = subType;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), command);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(10), para);
        return DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.Registers, payload);
    }

    /// <summary>Builds the RUN write frame (0xFF000020, control word 1).</summary>
    public static byte[] BuildRun() => BuildRegisterCommand(WriteSubType, SimulatorControlCommand, ControlRun);

    /// <summary>Builds the STOP write frame (0xFF000020, control word 0).</summary>
    public static byte[] BuildStop() => BuildRegisterCommand(WriteSubType, SimulatorControlCommand, ControlStop);

    /// <summary>Builds the load-status read frame (0xFF000010 read, para 0).</summary>
    public static byte[] BuildLoadPoll() => BuildRegisterCommand(ReadSubType, ProgramDownCommand, 0);

    /// <summary>Builds the load-trigger write frame (0xFF000010 write, para 1).</summary>
    public static byte[] BuildLoadTrigger() => BuildRegisterCommand(WriteSubType, ProgramDownCommand, 1);

    /// <summary>
    /// Classifies a register-chain answer payload (post-header). Observed shapes:
    /// <c>[06]</c> ACK; <c>[05][4B]</c> read value; <c>[0F][00][status]</c> error status.
    /// </summary>
    public static SimulatorControlReply ParseReply(ReadOnlySpan<byte> answerPayload)
    {
        if (answerPayload.Length >= 1 && answerPayload[0] == 0x06)
        {
            return new SimulatorControlReply(true, SimulatorControlKind.Ack, null, null);
        }

        if (answerPayload.Length >= 5 && answerPayload[0] == 0x05)
        {
            return new SimulatorControlReply(true, SimulatorControlKind.Value,
                BinaryPrimitives.ReadUInt32LittleEndian(answerPayload.Slice(1, 4)), null);
        }

        if (answerPayload.Length >= 3 && answerPayload[0] == 0x0F)
        {
            ushort status = BinaryPrimitives.ReadUInt16LittleEndian(answerPayload.Slice(2, 2));
            return new SimulatorControlReply(false, SimulatorControlKind.Error, null, status);
        }

        return new SimulatorControlReply(false, SimulatorControlKind.Unknown, null, null);
    }
}

public enum SimulatorControlKind
{
    Ack,
    Value,
    Error,
    Unknown,
}

/// <summary>Parsed register-chain reply for a control/load command.</summary>
/// <param name="Accepted">True when the daemon acked ([06]) or returned a value ([05]).</param>
/// <param name="Kind">Reply shape.</param>
/// <param name="Value">Read-back value for kind <see cref="SimulatorControlKind.Value"/> (load status).</param>
/// <param name="Status">Daemon status code for kind <see cref="SimulatorControlKind.Error"/>
/// (e.g. 0x82 LoadRestart — the documented load_down_file_start stub signature).</param>
public readonly record struct SimulatorControlReply(
    bool Accepted,
    SimulatorControlKind Kind,
    uint? Value,
    ushort? Status);
