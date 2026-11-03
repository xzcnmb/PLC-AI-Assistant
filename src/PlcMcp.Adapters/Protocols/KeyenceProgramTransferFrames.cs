using PlcMcp.Adapters.Protocols;

namespace PlcMcp.Adapters.Protocols;

// ACL program-transfer / session command frame builder for KEYENCE KV-5000/7000/8000
// ("Hexin", PlcTypeCode KVHexin_A..D = 0x3B..0x3F).
//
// Every opcode and provable constant byte below is [F] - decoded byte-for-byte from the
// installed KV STUDIO 12G 12.42.0.0 binaries (kvs1.dll / KVComm.dll / LDKCOM.dll / managed
// enums in Kvs.BinaryCommand.dll). Evidence and offsets: kvrev/PROGRAM_AREA_AND_PROJECT_FORMAT.md
// (see also kvrev/ACL_WIRE_FORMAT.md for the 10-byte TCP header + checksum).
//
// HONESTY BOUNDARY (do not cross): fields marked [U] were NOT recovered statically - they
// are emitted as explicit parameters or zero placeholders and documented per method. Frames
// whose [U] parameters are not yet calibrated MUST NOT be sent against a real PLC or the
// Studio-hosted emulator until a runtime probe confirms the layout. The builder exists so
// the transfer protocol can be implemented, byte-audited, and later calibrated - it is not
// a claim that download/upload works end-to-end.
public static class KeyenceProgramTransferFrames
{
    // ---- [F] opcodes (kvs1.dll / KVComm.dll append map, PROGRAM_AREA §1/§2.4) ----
    public const byte OpRequestConnect = 0x05;     // KVComm builder 0x1FF60
    public const byte OpDisconnect = 0x06;         // 0x1FFE0
    public const byte OpGetReadSession = 0x02;     // 0x1FFF0
    public const byte OpRequestType = 0x03;        // 0x20030 (already used by the probe)
    public const byte OpRequestPlcType = 0xB2;     // 0x20000 -> B2 00 00 00
    public const byte OpUserAuthentication = 0x2D; // 0x211E0 (KV2_CMD_USERAUTHENTICATION)
    public const byte OpPosix = 0x46;              // KV2_CMD_POSIX (file ops)
    public const byte OpPxWrite = 0x63;            // 0x1F360 PxWrite variant
    public const byte OpLoadProgram = 0x39;        // kvs1 LoadProgram (CPU -> PC)
    public const byte OpSaveProgram = 0x38;        // kvs1 SaveProgram (PC -> CPU)
    public const byte OpCheckProgData = 0x3B;      // kvs1 CheckProgData (post-transfer check)
    public const byte OpBeginDiffTrans = 0x50;     // kvs1 BeginDiffTrans
    public const byte OpEndDiffTrans = 0x51;       // kvs1 EndDiffTrans
    public const byte OpAddProgFromCpu = 0x52;     // kvs1 AddProgFromCpuProg (9-byte desc)
    public const byte OpAddProgFromThis = 0x53;    // kvs1 AddProgFromThisCmd

    // ---- [F] sub-opcodes (BinaryCommandDefine, PROGRAM_AREA §5.6) ----
    public const byte SubPxOpen = 0x04;            // KV2_SUB_PXOPEN
    public const byte SubPxClose = 0x0C;           // KV2_SUB_PXCLOSE
    public const byte SubPxRead = 0x0D;            // KV2_SUB_PXREAD
    public const byte SubPxCommitStart = 0x22;     // KV2_SUB_PXCOMMITSTART
    public const byte SubPxCommitEnd = 0x23;       // KV2_SUB_PXCOMMITEND
    public const byte SubLoadProgram = 0x06;       // kvs1 LoadProgram sub-op at body[2]

    // ---- [F] enums (Kvs.BinaryCommand.dll metadata, PROGRAM_AREA §5) ----
    public const byte ResponseSuccess = 0x00;      // RESP_SUCCESS (reply status byte)
    public const byte ResponseErrorRespByte = 0x06;
    public const byte ResponseNoAuthority = 0x85;  // NO_AUTHORITY
    public const byte AuthPlcTransfer = 0x04;      // AuthenticationFunction.PlcTransfer
    public const byte CommitExecKindProgram = 0x01;// CommitExecKind.REQ_PROGRAM
    public const byte CommitExecKindProgramRead = 0x41; // REQ_PROGRAM_READ (upload direction)

    /// <summary>Wraps an ACL body into a full TCP frame (10-byte header + checksum).</summary>
    private static byte[] AclFrame(ReadOnlySpan<byte> body) => KeyenceAclTcpAdapter.BuildFrame(body);

    // ---- session / connect family ----
    // [F] opcode only; total body layout of 0x05/0x06/0x02 is [U] (Builder param marshalling
    // not decoded). Zero-byte payloads must be calibrated against a live session before use.
    public static byte[] RequestConnect() => AclFrame([OpRequestConnect]);
    public static byte[] Disconnect() => AclFrame([OpDisconnect]);
    public static byte[] GetReadSession() => AclFrame([OpGetReadSession]);

    /// <summary>0xB2 00 00 00 - module/PLC type list request. [F] all four bytes.</summary>
    public static byte[] RequestPlcType() => AclFrame([OpRequestPlcType, 0x00, 0x00, 0x00]);

    /// <summary>
    /// 0x2D user-authentication. [F] opcode + the PlcTransfer=0x04 bit from
    /// AuthenticationFunction. [U] the exact parameter layout - authFunction is emitted as a
    /// single byte by default; calibrate against a live session before sending.
    /// </summary>
    public static byte[] UserAuthentication(byte authFunction = AuthPlcTransfer) =>
        AclFrame([OpUserAuthentication, authFunction]);

    // ---- program read family (kvs1, CPU -> PC) ----
    /// <summary>
    /// 0x39 sub 0x06 - load program read request, 9-byte body. [F] body[0]=0x39 and
    /// body[2]=0x06 (kvs1 OFF 19296: C6 [ESP+14],39 / C6 [ESP+16],06 / PUSH 9).
    /// [U] the remaining 7 descriptor bytes - pass them explicitly once recovered.
    /// </summary>
    public static byte[] LoadProgram(ReadOnlySpan<byte> parameters7)
    {
        if (parameters7.Length != 7) throw new ArgumentOutOfRangeException(nameof(parameters7), "0x39 descriptor is 9 bytes: 0x39, reserved, 0x06, then 7 parameter bytes.");
        var body = new byte[9];
        body[0] = OpLoadProgram;
        body[2] = SubLoadProgram;
        body[1] = parameters7[0];
        parameters7.Slice(1).CopyTo(body.AsSpan(3));
        return AclFrame(body);
    }

    /// <summary>
    /// 0x38 save-program frame: opcode + 8 big-endian u32 words (33-byte body, kvs1 OFF 18880).
    /// [F] the 8-BE32 layout; [U] which of the words are offset/length/checksum - pass
    /// zero-filled words until the block-header meaning is calibrated.
    /// </summary>
    public static byte[] SaveProgram(ReadOnlySpan<uint> header8)
    {
        if (header8.Length != 8) throw new ArgumentOutOfRangeException(nameof(header8), "0x38 carries exactly 8 BE32 header words.");
        var body = new byte[33];
        body[0] = OpSaveProgram;
        for (int i = 0; i < 8; i++)
        {
            body[1 + i * 4] = (byte)(header8[i] >> 24);
            body[2 + i * 4] = (byte)(header8[i] >> 16);
            body[3 + i * 4] = (byte)(header8[i] >> 8);
            body[4 + i * 4] = (byte)header8[i];
        }

        return AclFrame(body);
    }

    /// <summary>0x3B xx - program-data verification after transfer. [F] opcode; [U] the argument byte.</summary>
    public static byte[] CheckProgData(byte argument = 0x00) => AclFrame([OpCheckProgData, argument]);

    /// <summary>0x50 xx - begin differential transfer. [F] opcode; [U] the argument byte.</summary>
    public static byte[] BeginDiffTrans(byte argument = 0x00) => AclFrame([OpBeginDiffTrans, argument]);

    /// <summary>0x51 - end differential transfer. [F] single-byte body.</summary>
    public static byte[] EndDiffTrans() => AclFrame([OpEndDiffTrans]);

    /// <summary>
    /// 0x52 ... - append another program block. [F] opcode + 9-byte body length (kvs1 OFF 22592).
    /// [U] descriptor contents.
    /// </summary>
    public static byte[] AddProgFromCpuProg(ReadOnlySpan<byte> descriptor8)
    {
        if (descriptor8.Length != 8) throw new ArgumentOutOfRangeException(nameof(descriptor8), "0x52 body is 9 bytes: opcode + 8 parameter bytes.");
        var body = new byte[9];
        body[0] = OpAddProgFromCpu;
        descriptor8.CopyTo(body.AsSpan(1));
        return AclFrame(body);
    }

    // ---- POSIX file/commit family (KVComm 0x46, PROGRAM_AREA §2.4/§5.6) ----
    /// <summary>
    /// 0x46 sub ... - POSIX-style command. [F] opcode + sub byte; [U] parameter marshalling
    /// beyond the first bytes. payload is appended verbatim for auditability.
    /// </summary>
    public static byte[] PosixCommand(byte subCommand, ReadOnlySpan<byte> payload = default)
    {
        if (payload == default) payload = [];
        var body = new byte[2 + payload.Length];
        body[0] = OpPosix;
        body[1] = subCommand;
        payload.CopyTo(body.AsSpan(2));
        return AclFrame(body);
    }

    /// <summary>0x46 0x04 - PxOpen. [F] opcode+sub; [U] open-mode/lock fields.</summary>
    public static byte[] PxOpen(byte openMode = 0x00) => PosixCommand(SubPxOpen, [openMode]);

    /// <summary>0x46 0x0C - PxClose. [F] opcode+sub; [U] handle fields.</summary>
    public static byte[] PxClose() => PosixCommand(SubPxClose);

    /// <summary>0x46 0x0D - PxRead. [F] opcode+sub; [U] handle/length fields.</summary>
    public static byte[] PxRead() => PosixCommand(SubPxRead);

    /// <summary>
    /// 0x46 0x22 - commit start carrying CommitExecKind (REQ_PROGRAM=0x01 for download,
    /// REQ_PROGRAM_READ=0x41 for upload). [F] opcode + sub + kind byte; [U] trailing fields.
    /// </summary>
    public static byte[] CommitStart(byte commitExecKind = CommitExecKindProgram) =>
        PosixCommand(SubPxCommitStart, [commitExecKind]);

    /// <summary>0x46 0x23 - commit end. [F] opcode + sub; [U] trailing fields.</summary>
    public static byte[] CommitEnd() => PosixCommand(SubPxCommitEnd);

    /// <summary>
    /// 0x63 - PxWrite variant (KVComm builder 0x1F360). [F] opcode; [U] handle/length framing -
    /// data is appended verbatim for auditability; do NOT send until calibrated.
    /// </summary>
    public static byte[] PxWrite(ReadOnlySpan<byte> data)
    {
        var body = new byte[1 + data.Length];
        body[0] = OpPxWrite;
        data.CopyTo(body.AsSpan(1));
        return AclFrame(body);
    }
}