using System.Buffers.Binary;

namespace PlcMcp.Engineering.Workers.AutoShop.DownloadProtocol;

/// <summary>
/// Wire-frame model of the AutoShop H5U simulator native protocol served on TCP 127.0.0.1:12939
/// (h5u_daemon.exe, monitor.dll monitor_tcp_server + mp_core.dll mp_input).
///
/// Everything here was reverse-engineered from the shipped binaries and verified against a
/// locally started h5u_daemon.exe instance (loopback only). See
/// docs/inovance-autoshop-download-protocol.md for the evidence tables. No vendor endorsement;
/// format is version-locked to the AutoShop 4.12.0.2 simulator binaries.
///
/// Frame layout (little-endian):
/// <code>
///   offset 0 .. 2   ushort TotalLength   — includes this 8-byte header; response frames
///                                          carry payloadLength + 8
///   offset 2        byte  Version        — must be &lt;= 2 to reach a handler; values &gt; 2 get
///                                          a "probe" answer (8 bytes, [3] = 0xFF)
///   offset 3        byte  ServerId       — selects the handler chain inside mp_input
///   offset 4 .. 8   4 bytes reserved     — observed zero; echoed back
///   offset 8 ..     payload
/// </code>
/// </summary>
public static class DownloadFrame
{
    public const int HeaderLength = 8;
    public const byte ProtocolVersion = 2;
    public const byte ProbeVersion = 0xFF;

    public const ushort DefaultPort = 12939;

    /// <summary>Server (handler-chain) ids inside mp_input's dispatcher.</summary>
    public static class ServerIds
    {
        /// <summary>Register/memory read-write commands (0xFF000010..0xFF001002 command words).</summary>
        public const byte Registers = 0x01;

        /// <summary>Element/parameter block read-write (parse_cmd_para chain).</summary>
        public const byte Elements = 0x03;

        /// <summary>File transfer: download/upload sessions, data chunks, finish.</summary>
        public const byte FileTransfer = 0x04;

        /// <summary>Login/logout with password (check_userlogin_data chain).</summary>
        public const byte Login = 0x80;

        /// <summary>Monitor login-state query chain.</summary>
        public const byte Monitor = 0x81;
    }

    /// <summary>
    /// Payload-type byte for the file-transfer chain (server id 4). Observed dispatch table has
    /// 16 slots; only these values reach a handler, every other value is ignored (no response).
    /// </summary>
    public static class FilePayloadTypes
    {
        /// <summary>Open an upload session (daemon reads the file and sends it to the client).</summary>
        public const byte UploadSession = 0x01;

        /// <summary>Open a download session (daemon receives the file from the client).</summary>
        public const byte DownloadSession = 0x02;

        /// <summary>Data chunk (client to daemon, or the daemon's answer to an upload read).</summary>
        public const byte DataChunk = 0x05;

        /// <summary>Request the next chunk from an upload session.</summary>
        public const byte UploadRead = 0x06;

        /// <summary>Finish/close the current session.</summary>
        public const byte Finish = 0x0F;

        /// <summary>Error marker: daemon turns a failed payload into [0x0F][status].</summary>
        public const byte Error = 0x0F;
    }

    /// <summary>Sub-type byte for the login chain (server id 0x80).</summary>
    public static class LoginSubTypes
    {
        /// <summary>Login with password: [1][0][user 32B][password 32B].</summary>
        public const byte Login = 0x01;

        /// <summary>Logout: clears the per-connection authorized flag.</summary>
        public const byte Logout = 0x02;
    }

    /// <summary>Sub-type byte for the monitor chain (server id 0x81).</summary>
    public static class MonitorSubTypes
    {
        /// <summary>
        /// Daemon-side logout: clears the per-connection authorized flag; answer payload [0x06].
        /// (Verified on the daemon: this exchange fires the "login event:2" logout callback.)
        /// </summary>
        public const byte Logout = 0x02;

        /// <summary>
        /// Login-state query; answer payload [0x03][result] with result 1 when the connection is
        /// authorized and 0 when not. Verified on the daemon.
        /// </summary>
        public const byte QueryLoginState = 0x03;
    }

    /// <summary>Command words observed in the register chain (server id 1) registration table.</summary>
    public static class RegisterCommands
    {
        /// <summary>PLC/module version info read; payload length must be 0xF0 (240 bytes).</summary>
        public const uint VersionInfoRead = 0xFF100000;

        /// <summary>PLC program download status poll (project/PLCProgram.down|.updown).</summary>
        public const uint ProgramDownStatus = 0xFF000010;

        /// <summary>Simulator control word write (RUN/STOP) — deliberately NOT implemented by the client.</summary>
        public const uint SimulatorControl = 0xFF000020;
    }

    /// <summary>
    /// Daemon status codes observed in error/ack payloads. Codes 0x80..0x87 come from the
    /// load_down_file_start result map {0→0x00, 1→0x82, 2→0x80, 3→0x83, 4→0x41, 5→0x84}.
    /// </summary>
    public static class StatusCodes
    {
        public const uint Ok = 0x00;
        public const uint BadLength = 0x20;
        public const uint ProbeAnswer = 0x21;
        public const uint BadParaLength = 0x40;
        public const uint NotAuthorized = 0x41;
        public const uint WrongPassword = 0x43;
        public const uint ChunkOutOfOrder = 0x60;
        public const uint SessionNotFound = 0x61;
        public const uint LoadRestart = 0x82;
        public const uint SessionBusy = 0x83;
        public const uint OpenFailed = 0x84;
        public const uint AlreadyComplete = 0x85;
        public const uint AllocFailed = 0x86;
        public const uint LoadStatusOutOfRange = 0x87;
    }

    /// <summary>Max frame size enforced by the daemon's per-connection loop (0x5B4 = 1460).</summary>
    public const int MaxFrameLength = 0x5B4;

    /// <summary>Max payload size enforced by the daemon's parameter parser (0x3FF = 1023).</summary>
    public const int MaxPayloadLength = 0x3FF;

    /// <summary>Builds a request frame: 8-byte header + payload, with TotalLength = 8 + payload.Length.</summary>
    public static byte[] Build(byte version, byte serverId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxFrameLength - HeaderLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length,
                $"Payload exceeds the daemon's frame limit of {MaxFrameLength - HeaderLength} bytes.");
        }

        var frame = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)frame.Length);
        frame[2] = version;
        frame[3] = serverId;
        // frame[4..8) reserved, stays zero
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    /// <summary>
    /// Parses a received frame. Returns false when the buffer is shorter than the header or when
    /// TotalLength does not match the buffer length (the daemon keeps the length word stale on
    /// probe answers, so callers must pass exactly the received byte count).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, out DownloadFrameView view)
    {
        view = default;
        if (frame.Length < HeaderLength)
        {
            return false;
        }

        ushort total = BinaryPrimitives.ReadUInt16LittleEndian(frame);
        if (total != frame.Length)
        {
            return false;
        }

        view = new DownloadFrameView(
            total,
            frame[2],
            frame[3],
            frame.Slice(4, 4).ToArray(),
            frame.Slice(HeaderLength).ToArray());
        return true;
    }
}

/// <summary>Read-only view of a parsed frame.</summary>
public readonly record struct DownloadFrameView(
    ushort TotalLength,
    byte Version,
    byte ServerId,
    byte[] Reserved,
    byte[] Payload)
{
    /// <summary>First payload byte (payload type / sub-type), or null for an empty payload.</summary>
    public byte? PayloadType => Payload.Length > 0 ? Payload[0] : null;

    /// <summary>Status dword carried by an error payload ([0x0F][0][status]), or null.</summary>
    public uint? ErrorStatus =>
        Payload.Length >= 6 && Payload[0] == DownloadFrame.FilePayloadTypes.Error
            ? BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(2, 4))
            : null;
}
