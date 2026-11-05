using System.Buffers.Binary;
using System.Text;
using PlcMcp.Adapters.Protocols;

namespace PlcMcp.Adapters.Protocols;

// ACL program-transfer / session command frame builder for KEYENCE KV-5000/7000/8000
// ("Hexin", PlcTypeCode KVHexin_A..D = 0x3B..0x3F).
//
// Every opcode and provable constant byte below is [F] - decoded byte-for-byte from the
// installed KV STUDIO 12G 12.42.0.0 binaries (kvs1.dll / KVComm.dll / LDKCOM.dll / managed
// enums in Kvs.BinaryCommand.dll). Evidence and offsets: kvrev/PROGRAM_AREA_AND_PROJECT_FORMAT.md,
// kvrev/ACL_WIRE_FORMAT.md, and docs/keyence-acl-session-transfer-calibration-2026-11-05.md.
//
// HONESTY BOUNDARY:
// - Fields marked [F] are confirmed by static disassembly / IL instructions.
// - Fields marked [I] represent high-confidence inferences from structural analysis (e.g. SaveProgram 8-word header).
// - Fields marked [U] (such as framing beyond raw payload in 0x63 PxWrite) are reserved for runtime calibration.
public static class KeyenceProgramTransferFrames
{
    // ---- [F] opcodes (kvs1.dll / KVComm.dll append map, PROGRAM_AREA §1/§2.4, calibration §4.1/§4.4) ----
    public const byte OpRequestConnect = 0x05;     // KVComm builder 0x1FF60, calibration §4.1 [F]
    public const byte OpDisconnect = 0x06;         // 0x1FFE0, calibration §4.1 [F]
    public const byte OpGetReadSession = 0x02;     // 0x1FFF0, calibration §4.1 [F]
    public const byte OpRequestType = 0x03;        // 0x20030 (StatusLineProtocol), calibration §4.1 [F]
    public const byte OpRequestPlcType = 0xB2;     // 0x20000 -> B2 00 00 00, calibration §4.1 [F]
    public const byte OpUserAuthentication = 0x2D; // 0x211E0 (KV2_CMD_USERAUTHENTICATION), calibration §4.2 [F]
    public const byte OpPosix = 0x46;              // KV2_CMD_POSIX (file & commit ops), calibration §4.3 [F]
    public const byte OpPxWrite = 0x63;            // KV2_PXWRITE / Kv2ReadCommandCreate (kvcomm_appendmap.txt:77-79, 0x1F360), calibration §4.3 [F]
    public const byte OpLoadProgram = 0x39;        // kvs1 LoadProgram (CPU -> PC, 0x4B60), calibration §4.4 [F]
    public const byte OpSaveProgram = 0x38;        // kvs1 SaveProgram (PC -> CPU, 0x49C0), calibration §4.4 [F]
    public const byte OpCheckProgData = 0x3B;      // kvs1 CheckProgData (0x4DC0), calibration §4.4 [F]
    public const byte OpBeginDiffTrans = 0x50;     // kvs1 BeginDiffTrans (0x57A0), calibration §4.4 [F]
    public const byte OpEndDiffTrans = 0x51;       // kvs1 EndDiffTrans (0x57F0), calibration §4.4 [F]
    public const byte OpAddProgFromCpu = 0x52;     // kvs1 AddProgFromCpuProg (0x5840), calibration §4.4 [F]
    public const byte OpAddProgFromThis = 0x53;    // kvs1 AddProgFromThisCmd (0x58D0), calibration §4.4 [F]
    public const byte OpSimulatorControl = 0x3F;   // KV2_CMD_BREAKSTEP / SimulatorControl, calibration §4.5 [F]

    // ---- [F] sub-opcodes (BinaryCommandDefine, calibration §4.2/§4.3/§4.4/§4.5) ----
    public const byte SubAuthExecuteCommand = 0x00;// KV2_SUB_EXECUTECOMMAND, calibration §4.2 [F]
    public const byte SubAuthExecuteLogin = 0x01;  // KV2_SUB_EXECUTEUSERAUTHERNTICATION, calibration §4.2 [F]
    public const byte SubAuthConfirmAuth = 0x02;   // KV2_SUB_CONFIRMAUTHORITY, calibration §4.2 [F]

    public const byte SubPxOpen = 0x04;            // KV2_SUB_PXOPEN, calibration §4.3 [F]
    public const byte SubPxClose = 0x0C;           // KV2_SUB_PXCLOSE, calibration §4.3 [F]
    public const byte SubPxRead = 0x0D;            // KV2_SUB_PXREAD, calibration §4.3 [F]
    public const byte SubPxUnlock = 0x21;          // KV2_SUB_PXUNLOCK, calibration §4.3 [F]
    public const byte SubPxCommitStart = 0x22;     // KV2_SUB_PXCOMMITSTART, calibration §4.3 [F]
    public const byte SubPxCommitEnd = 0x23;       // KV2_SUB_PXCOMMITEND, calibration §4.3 [F]
    public const byte SubPxReqExtTimeout = 0x27;   // KV2_SUB_PXREQEXTTIMEOUT, calibration §4.3 [F]
    public const byte SubPxLock = 0x29;            // KV2_SUB_PXLOCK, calibration §4.3 [F]
    public const byte SubLoadProgram = 0x06;       // kvs1 LoadProgram sub-op at body[2], calibration §4.4 [F]

    public const byte SubSimCtrl = 0x06;           // KV2_SUB_SIM_CTRL, calibration §4.5 [F]
    public const byte Sub2SimTransferUnit = 0x40;  // KV2_SUB2_SIM_CTRL_TRAMS_UNIT_LIST, calibration §4.5 [F]
    public const byte Sub2SimGetInitState = 0x41;  // KV2_SUB2_SIM_CTRL_GET_INITIALIZE_CODE_TRANS, calibration §4.5 [F]

    // ---- [F] enums & constants (Kvs.BinaryCommand.dll metadata, calibration §4.2/§4.3) ----
    public const byte ResponseSuccess = 0x00;      // RESP_SUCCESS (reply status byte)
    public const byte ResponseErrorRespByte = 0x06;
    public const byte ResponseNoAuthority = 0x85;  // NO_AUTHORITY
    public const byte AuthPlcTransfer = 0x04;      // AuthenticationFunction.PlcTransfer
    public const byte AuthOnlineEdit = 0x08;       // AuthenticationFunction.OnlineEdit
    public const int UserAuthenticationSessionIdSize = 8; // 8-byte session ID, calibration §4.2 [F]

    public const byte FileOpenModeReadOnly = 0x01; // FileOpenMode.ReadOnly, calibration §4.3 [F]
    public const byte FileOpenModeWriteOnly = 0x02;// FileOpenMode.WriteOnly, calibration §4.3 [F]
    public const byte FileOpenModeReadWrite = 0x03;// FileOpenMode.ReadWrite, calibration §4.3 [F]

    public const byte CommitTypeCommit = 0x00;     // CommitType.Commit, calibration §4.3 [F]
    public const byte CommitTypeDiscard = 0x01;    // CommitType.Discard, calibration §4.3 [F]

    public const ushort CommitKindProgram = 0x0001;      // CommitExecKind.REQ_PROGRAM (download), calibration §4.3 [F]
    public const ushort CommitKindProgramRead = 0x0041;  // CommitExecKind.REQ_PROGRAM_READ (upload), calibration §4.3 [F]
    public const ushort CommitKindDiffProgram = 0x0008;  // CommitExecKind.REQ_DIFFPRG (diff transfer), calibration §4.3 [F]

    // Backward-compatible 1-byte aliases for commit kinds
    public const byte CommitExecKindProgram = 0x01;
    public const byte CommitExecKindProgramRead = 0x41;

    public const int MaxPosixReadLength = 2032;    // 0x7F0, calibration §4.3 [F]

    /// <summary>Wraps an ACL body into a full TCP frame (10-byte header + checksum).</summary>
    private static byte[] AclFrame(ReadOnlySpan<byte> body) => KeyenceAclTcpAdapter.BuildFrame(body);

    // ---- 1. session / connect family (calibration §4.1 [F]) ----
    /// <summary>0x05 - Request connect. Single-byte body. [F] (LDKCOM 0x41D0, KVComm 0x1FF60).</summary>
    public static byte[] RequestConnect() => AclFrame([OpRequestConnect]);

    /// <summary>0x06 - Disconnect. Single-byte body. [F] (LDKCOM 0x4210, KVComm 0x1FFE0).</summary>
    public static byte[] Disconnect() => AclFrame([OpDisconnect]);

    /// <summary>0x02 - Get read session. Single-byte body. [F] (KVComm 0x1FFF0).</summary>
    public static byte[] GetReadSession() => AclFrame([OpGetReadSession]);

    /// <summary>0x03 - Request PLC type. Single-byte body. [F] (KVComm 0x20030, StatusLineProtocol).</summary>
    public static byte[] RequestType() => AclFrame([OpRequestType]);

    /// <summary>0xB2 00 00 00 - Module/PLC type list request. [F] all four bytes (KVComm 0x20000).</summary>
    public static byte[] RequestPlcType() => AclFrame([OpRequestPlcType, 0x00, 0x00, 0x00]);

    // ---- 2. user authentication family (calibration §4.2 [F]) ----
    /// <summary>
    /// 0x2D [authFunction] - legacy user-authentication shorthand. [F] opcode + PlcTransfer bit.
    /// Retained for backward compatibility.
    /// </summary>
    public static byte[] UserAuthentication(byte authFunction = AuthPlcTransfer) =>
        AclFrame([OpUserAuthentication, authFunction]);

    /// <summary>
    /// 0x2D 0x02 &lt;SessionId 8B&gt; - Confirm user authentication / authority.
    /// [F] layout: 10-byte body (calibration §4.2, UserAuthenticationProtocol::ConfirmAuthority).
    /// </summary>
    public static byte[] UserAuthConfirmAuthority(ReadOnlySpan<byte> sessionId8)
    {
        if (sessionId8.Length != UserAuthenticationSessionIdSize)
            throw new ArgumentOutOfRangeException(nameof(sessionId8), $"SessionId must be exactly {UserAuthenticationSessionIdSize} bytes.");
        Span<byte> body = stackalloc byte[10];
        body[0] = OpUserAuthentication;
        body[1] = SubAuthConfirmAuth;
        sessionId8.CopyTo(body[2..]);
        return AclFrame(body);
    }

    /// <summary>
    /// 0x2D 0x00 &lt;SessionId 8B&gt; &lt;InnerCommand...&gt; - Execute authenticated command wrapper.
    /// [F] layout: 10 + N bytes (calibration §4.2, UserAuthenticationProtocol::ExecuteCommand).
    /// </summary>
    public static byte[] UserAuthWrapCommand(ReadOnlySpan<byte> sessionId8, ReadOnlySpan<byte> innerCommand)
    {
        if (sessionId8.Length != UserAuthenticationSessionIdSize)
            throw new ArgumentOutOfRangeException(nameof(sessionId8), $"SessionId must be exactly {UserAuthenticationSessionIdSize} bytes.");
        var body = new byte[10 + innerCommand.Length];
        body[0] = OpUserAuthentication;
        body[1] = SubAuthExecuteCommand;
        sessionId8.CopyTo(body.AsSpan(2, 8));
        innerCommand.CopyTo(body.AsSpan(10));
        return AclFrame(body);
    }

    /// <summary>
    /// 0x2D 0x01 &lt;AuthData...&gt; - Execute user authentication login.
    /// [F] layout: 2 + N bytes (calibration §4.2, UserAuthenticationProtocol::ExecuteUserAuthentication).
    /// </summary>
    public static byte[] UserAuthLogin(ReadOnlySpan<byte> authData)
    {
        var body = new byte[2 + authData.Length];
        body[0] = OpUserAuthentication;
        body[1] = SubAuthExecuteLogin;
        authData.CopyTo(body.AsSpan(2));
        return AclFrame(body);
    }

    // ---- 3. program read & write family (kvs1, CPU &lt;-&gt; PC, calibration §4.4 [F]/[I]) ----
    /// <summary>
    /// 0x39 sub 0x06 - load program read request, 9-byte body. [F] body[0]=0x39 and body[2]=0x06 (kvs1 OFF 19296 / 0x4B60).
    /// Retained for backward compatibility.
    /// </summary>
    public static byte[] LoadProgram(ReadOnlySpan<byte> parameters7)
    {
        if (parameters7.Length != 7)
            throw new ArgumentOutOfRangeException(nameof(parameters7), "0x39 descriptor is 9 bytes: 0x39, reserved, 0x06, then 7 parameter bytes.");
        var body = new byte[9];
        body[0] = OpLoadProgram;
        body[2] = SubLoadProgram;
        body[1] = parameters7[0];
        parameters7.Slice(1).CopyTo(body.AsSpan(3));
        return AclFrame(body);
    }

    /// <summary>
    /// 0x39 &lt;kind 1B&gt; 06 &lt;offset BE32&gt; &lt;len BE16&gt; - calibrated LoadProgram (CPU -> PC).
    /// [F] layout: 9-byte body (calibration §4.4, Kv2LoadProgramCreate).
    /// </summary>
    public static byte[] LoadProgram(uint offset, ushort length, byte blockKind = 0x00)
    {
        Span<byte> body = stackalloc byte[9];
        body[0] = OpLoadProgram;
        body[1] = blockKind;
        body[2] = SubLoadProgram;
        BinaryPrimitives.WriteUInt32BigEndian(body[3..7], offset);
        BinaryPrimitives.WriteUInt16BigEndian(body[7..9], length);
        return AclFrame(body);
    }

    /// <summary>
    /// 0x38 save-program frame header: opcode + 8 big-endian u32 words (33-byte body, kvs1 OFF 18880 / 0x49C0).
    /// [F] the 8-BE32 header layout; [I] block header word semantics.
    /// </summary>
    public static byte[] SaveProgram(ReadOnlySpan<uint> header8) => SaveProgram(header8, []);

    /// <summary>
    /// 0x38 save-program frame: opcode + 8 big-endian u32 words header + trailing payload data.
    /// [F] 33-byte header layout + code payload copy (kvs1 0x49C0, calibration §4.4); [I] header word semantics.
    /// </summary>
    public static byte[] SaveProgram(ReadOnlySpan<uint> header8, ReadOnlySpan<byte> payload)
    {
        if (header8.Length != 8)
            throw new ArgumentOutOfRangeException(nameof(header8), "0x38 carries exactly 8 BE32 header words.");
        var body = new byte[33 + payload.Length];
        body[0] = OpSaveProgram;
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1 + i * 4, 4), header8[i]);
        }
        if (!payload.IsEmpty)
        {
            payload.CopyTo(body.AsSpan(33));
        }
        return AclFrame(body);
    }

    /// <summary>
    /// 0x3B &lt;target 1B&gt; - program-data verification after transfer (0x00 = full project area).
    /// [F] opcode and 2-byte body (kvs1 0x4DC0, calibration §4.4).
    /// </summary>
    public static byte[] CheckProgData(byte target = 0x00) => AclFrame([OpCheckProgData, target]);

    /// <summary>
    /// 0x50 &lt;diffKind 1B&gt; - begin differential transfer (default 0x00).
    /// [F] opcode and 2-byte body (kvs1 0x57A0, calibration §4.4).
    /// </summary>
    public static byte[] BeginDiffTrans(byte diffKind = 0x00) => AclFrame([OpBeginDiffTrans, diffKind]);

    /// <summary>0x51 - end differential transfer. [F] single-byte body (kvs1 0x57F0, calibration §4.4).</summary>
    public static byte[] EndDiffTrans() => AclFrame([OpEndDiffTrans]);

    /// <summary>
    /// 0x52 ... - append program block from CPU. 9-byte body (kvs1 OFF 22592 / 0x5840).
    /// Retained for backward compatibility.
    /// </summary>
    public static byte[] AddProgFromCpuProg(ReadOnlySpan<byte> descriptor8)
    {
        if (descriptor8.Length != 8)
            throw new ArgumentOutOfRangeException(nameof(descriptor8), "0x52 body is 9 bytes: opcode + 8 parameter bytes.");
        var body = new byte[9];
        body[0] = OpAddProgFromCpu;
        descriptor8.CopyTo(body.AsSpan(1));
        return AclFrame(body);
    }

    /// <summary>
    /// 0x52 &lt;offset BE32&gt; &lt;len BE32&gt; - append program block from CPU.
    /// [F] layout: 9-byte body (kvs1 0x5840, calibration §4.4).
    /// </summary>
    public static byte[] AddProgFromCpuProg(uint offset, uint length)
    {
        Span<byte> body = stackalloc byte[9];
        body[0] = OpAddProgFromCpu;
        BinaryPrimitives.WriteUInt32BigEndian(body[1..5], offset);
        BinaryPrimitives.WriteUInt32BigEndian(body[5..9], length);
        return AclFrame(body);
    }

    /// <summary>
    /// 0x53 &lt;payload...&gt; - append current program block data.
    /// [F] opcode and body layout (kvs1 0x58D0, calibration §4.4).
    /// </summary>
    public static byte[] AddProgFromThis(ReadOnlySpan<byte> payload = default)
    {
        if (payload == default) payload = [];
        var body = new byte[1 + payload.Length];
        body[0] = OpAddProgFromThis;
        if (!payload.IsEmpty)
        {
            payload.CopyTo(body.AsSpan(1));
        }
        return AclFrame(body);
    }

    // ---- 4. POSIX file/commit family (KVComm 0x46, PROGRAM_AREA §2.4/§5.6, calibration §4.3 [F]) ----
    /// <summary>
    /// 0x46 sub ... - POSIX-style command wrapper. [F] opcode + sub byte.
    /// </summary>
    public static byte[] PosixCommand(byte subCommand, ReadOnlySpan<byte> payload = default)
    {
        if (payload == default) payload = [];
        var body = new byte[2 + payload.Length];
        body[0] = OpPosix;
        body[1] = subCommand;
        if (!payload.IsEmpty)
        {
            payload.CopyTo(body.AsSpan(2));
        }
        return AclFrame(body);
    }

    /// <summary>0x46 0x04 - legacy PxOpen single-mode frame. Retained for backward compatibility.</summary>
    public static byte[] PxOpen(byte openMode = 0x00) => PosixCommand(SubPxOpen, [openMode]);

    /// <summary>
    /// 0x46 0x04 - PxOpen file request with UTF-16BE path.
    /// [F] layout: 12-byte header + path bytes (PosixOpenCommand::WriteCommand, calibration §4.3).
    /// </summary>
    public static byte[] PxOpen(string path, byte openMode = FileOpenModeReadOnly, short lockAuthCode = 0, bool isAwait = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        int pathByteLen = path.Length * 2;
        if (pathByteLen > 512)
            throw new ArgumentOutOfRangeException(nameof(path), "POSIX path byte length must not exceed 512 bytes.");
        var body = new byte[12 + pathByteLen];
        body[0] = OpPosix;
        body[1] = SubPxOpen;
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(2, 2), lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        body[5] = 0x00;
        body[6] = openMode;
        body[7] = 0x00;
        body[8] = 0x01; // UTF-16
        body[9] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10, 2), (ushort)pathByteLen);
        if (pathByteLen > 0)
        {
            Encoding.BigEndianUnicode.GetBytes(path, body.AsSpan(12));
        }
        return AclFrame(body);
    }

    /// <summary>0x46 0x0C - legacy PxClose without descriptor. Retained for backward compatibility.</summary>
    public static byte[] PxClose() => PosixCommand(SubPxClose);

    /// <summary>
    /// 0x46 0x0C - PxClose file descriptor.
    /// [F] layout: 8-byte body (PosixCloseCommand::WriteCommand, calibration §4.3).
    /// </summary>
    public static byte[] PxClose(short fileDescriptor, short lockAuthCode = 0, bool isAwait = false)
    {
        Span<byte> body = stackalloc byte[8];
        body[0] = OpPosix;
        body[1] = SubPxClose;
        BinaryPrimitives.WriteInt16BigEndian(body[2..4], lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        body[5] = 0x00;
        BinaryPrimitives.WriteInt16BigEndian(body[6..8], fileDescriptor);
        return AclFrame(body);
    }

    /// <summary>0x46 0x0D - legacy PxRead without parameters. Retained for backward compatibility.</summary>
    public static byte[] PxRead() => PosixCommand(SubPxRead);

    /// <summary>
    /// 0x46 0x0D - PxRead file block. Hard length cap at MaxPosixReadLength (2032 / 0x7F0 bytes).
    /// [F] layout: 16-byte body (PosixReadCommand::WriteCommand, calibration §4.3).
    /// </summary>
    public static byte[] PxRead(short fileDescriptor, uint offset, uint length, short lockAuthCode = 0, bool isAwait = false)
    {
        uint clampedLen = Math.Min(length, (uint)MaxPosixReadLength);
        Span<byte> body = stackalloc byte[16];
        body[0] = OpPosix;
        body[1] = SubPxRead;
        BinaryPrimitives.WriteInt16BigEndian(body[2..4], lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        body[5] = 0x00;
        BinaryPrimitives.WriteInt16BigEndian(body[6..8], fileDescriptor);
        BinaryPrimitives.WriteUInt32BigEndian(body[8..12], offset);
        BinaryPrimitives.WriteUInt32BigEndian(body[12..16], clampedLen);
        return AclFrame(body);
    }

    /// <summary>
    /// 0x46 0x22 - legacy CommitStart carrying 1-byte kind. Retained for backward compatibility.
    /// </summary>
    public static byte[] CommitStart(byte commitExecKind = CommitExecKindProgram) =>
        PosixCommand(SubPxCommitStart, [commitExecKind]);

    /// <summary>
    /// 0x46 0x22 - CommitStart starting a transfer transaction.
    /// [F] layout: 12-byte header + UTF-16BE path (PosixCommitStartCommand::WriteCommand, calibration §4.3).
    /// </summary>
    public static byte[] CommitStart(ushort commitExecKind, string path = "", short lockAuthCode = 0, bool isRead = false, bool isAwait = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        int pathByteLen = path.Length * 2;
        if (pathByteLen > 512)
            throw new ArgumentOutOfRangeException(nameof(path), "Commit path byte length must not exceed 512 bytes.");
        var body = new byte[12 + pathByteLen];
        body[0] = OpPosix;
        body[1] = SubPxCommitStart;
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(2, 2), lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        body[5] = (byte)(isRead ? 0x40 : 0x00);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6, 2), commitExecKind);
        body[8] = 0x01; // UTF-16
        body[9] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10, 2), (ushort)pathByteLen);
        if (pathByteLen > 0)
        {
            Encoding.BigEndianUnicode.GetBytes(path, body.AsSpan(12));
        }
        return AclFrame(body);
    }

    /// <summary>0x46 0x23 - legacy CommitEnd without transaction ID. Retained for backward compatibility.</summary>
    public static byte[] CommitEnd() => PosixCommand(SubPxCommitEnd);

    /// <summary>
    /// 0x46 0x23 - CommitEnd ending a transaction.
    /// [F] layout: 8-byte body (PosixCommitEndCommand::WriteCommand, calibration §4.3).
    /// </summary>
    public static byte[] CommitEnd(ushort commitId, byte commitType = CommitTypeCommit, short lockAuthCode = 0, bool isAwait = false)
    {
        Span<byte> body = stackalloc byte[8];
        body[0] = OpPosix;
        body[1] = SubPxCommitEnd;
        BinaryPrimitives.WriteInt16BigEndian(body[2..4], lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        body[5] = commitType;
        BinaryPrimitives.WriteUInt16BigEndian(body[6..8], commitId);
        return AclFrame(body);
    }

    /// <summary>
    /// 0x46 0x21 - Release POSIX lock.
    /// [F] layout: 6-byte body (PosixUnlockCommand::WriteCommand, calibration §4.3).
    /// </summary>
    public static byte[] PosixUnlock(short lockAuthCode)
    {
        Span<byte> body = stackalloc byte[6];
        body[0] = OpPosix;
        body[1] = SubPxUnlock;
        BinaryPrimitives.WriteInt16BigEndian(body[2..4], lockAuthCode);
        body[4] = 0x00;
        body[5] = 0x00;
        return AclFrame(body);
    }

    /// <summary>
    /// 0x46 0x27 - Extend POSIX lock timeout.
    /// [F] layout: 5-byte body (PosixLockTimeoutExtendCommand::WriteCommand, calibration §4.3).
    /// </summary>
    public static byte[] PosixLockTimeoutExtend(short lockAuthCode, bool isAwait = false)
    {
        Span<byte> body = stackalloc byte[5];
        body[0] = OpPosix;
        body[1] = SubPxReqExtTimeout;
        BinaryPrimitives.WriteInt16BigEndian(body[2..4], lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        return AclFrame(body);
    }

    // ---- 5. 0x63 write family (kvcomm_appendmap.txt:77-79) ----
    /// <summary>
    /// 0x63 - PxWrite / ReadCommand shared write opcode (KVComm builder 0x1F360, kvcomm_appendmap.txt:77-79).
    /// [F] opcode 0x63 confirmed from Kv2PxWriteCreate / Kv2ReadCommandCreate.
    /// [U] framing beyond raw data is reserved for runtime calibration. Data is appended verbatim for auditability.
    /// </summary>
    public static byte[] PxWrite(ReadOnlySpan<byte> data)
    {
        var body = new byte[1 + data.Length];
        body[0] = OpPxWrite;
        data.CopyTo(body.AsSpan(1));
        return AclFrame(body);
    }

    // ---- 6. simulator control family (calibration §4.5 [F]) ----
    /// <summary>
    /// 0x3F 0x06 0x41 0x00 - Get simulator initialization state.
    /// [F] layout: 4-byte body (GetInitializationStateCommand, calibration §4.5).
    /// </summary>
    public static byte[] SimulatorGetInitializationState() =>
        AclFrame([OpSimulatorControl, SubSimCtrl, Sub2SimGetInitState, 0x00]);
}