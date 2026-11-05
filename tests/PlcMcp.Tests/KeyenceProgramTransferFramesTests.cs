using System.Buffers.Binary;
using PlcMcp.Adapters.Protocols;

namespace PlcMcp.Tests;

// Byte-level verification of the reverse-engineered Keyence program-transfer command
// frames. Every assertion pins an [F] byte from kvrev/PROGRAM_AREA_AND_PROJECT_FORMAT.md
// and docs/keyence-acl-session-transfer-calibration-2026-11-05.md:
// opcodes, sub-opcodes, and provable constants are exact.
public sealed class KeyenceProgramTransferFramesTests
{
    private static string Hex(ReadOnlySpan<byte> frame) => Convert.ToHexString(frame);

    // ---- 1. session / connect family ----
    [Fact]
    public void RequestConnect_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.RequestConnect();
        Assert.Equal(11, frame.Length);
        Assert.Equal("05", Hex(frame.AsSpan(10)));
    }

    [Fact]
    public void Disconnect_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.Disconnect();
        Assert.Equal(11, frame.Length);
        Assert.Equal("06", Hex(frame.AsSpan(10)));
    }

    [Fact]
    public void GetReadSession_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.GetReadSession();
        Assert.Equal(11, frame.Length);
        Assert.Equal("02", Hex(frame.AsSpan(10)));
    }

    [Fact]
    public void RequestType_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.RequestType();
        Assert.Equal(11, frame.Length);
        Assert.Equal("03", Hex(frame.AsSpan(10)));
    }

    // [F] 0xB2 00 00 00 RequestPLCType / GetModuleTypeList
    [Fact]
    public void RequestPlcType_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.RequestPlcType();
        Assert.Equal(14, frame.Length);
        Assert.Equal((uint)14, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4)));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4, 2)));
        Assert.Equal("B2000000", Hex(frame.AsSpan(10)));
    }

    // ---- 2. user authentication family ----
    // [F] 0x2D user-authentication legacy shorthand
    [Fact]
    public void UserAuthentication_DefaultsToPlcTransferBit()
    {
        var frame = KeyenceProgramTransferFrames.UserAuthentication();
        Assert.Equal("2D04", Hex(frame.AsSpan(10)));
        var custom = KeyenceProgramTransferFrames.UserAuthentication(0x10);
        Assert.Equal("2D10", Hex(custom.AsSpan(10)));
    }

    [Fact]
    public void UserAuthConfirmAuthority_BuildsExpectedFrame()
    {
        byte[] sessionId = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        var frame = KeyenceProgramTransferFrames.UserAuthConfirmAuthority(sessionId);
        Assert.Equal(20, frame.Length);
        Assert.Equal("2D020102030405060708", Hex(frame.AsSpan(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyenceProgramTransferFrames.UserAuthConfirmAuthority([1, 2, 3]));
    }

    [Fact]
    public void UserAuthWrapCommand_BuildsExpectedFrame()
    {
        byte[] sessionId = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];
        byte[] innerCmd = [0x46, 0x21, 0x12, 0x34, 0x00, 0x00];
        var frame = KeyenceProgramTransferFrames.UserAuthWrapCommand(sessionId, innerCmd);
        Assert.Equal(26, frame.Length);
        Assert.Equal("2D001122334455667788462112340000", Hex(frame.AsSpan(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyenceProgramTransferFrames.UserAuthWrapCommand([1], innerCmd));
    }

    [Fact]
    public void UserAuthLogin_BuildsExpectedFrame()
    {
        byte[] authData = [0xDE, 0xAD, 0xBE, 0xEF];
        var frame = KeyenceProgramTransferFrames.UserAuthLogin(authData);
        Assert.Equal(16, frame.Length);
        Assert.Equal("2D01DEADBEEF", Hex(frame.AsSpan(10)));
    }

    // ---- 3. program read & write family ----
    // [F] kvs1 0x39 sub 0x06, legacy 9-byte descriptor (OFF 19296)
    [Fact]
    public void LoadProgram_LegacyDescriptor_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.LoadProgram([0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]);
        Assert.Equal(19, frame.Length);
        Assert.Equal("391106223344556677", Hex(frame.AsSpan(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyenceProgramTransferFrames.LoadProgram([0x01]));
    }

    [Fact]
    public void LoadProgram_CalibratedOverload_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.LoadProgram(offset: 0x00010000, length: 0x0200, blockKind: 0x01);
        Assert.Equal(19, frame.Length);
        Assert.Equal("390106000100000200", Hex(frame.AsSpan(10)));

        var defaultKindFrame = KeyenceProgramTransferFrames.LoadProgram(offset: 0x00000100, length: 0x0080);
        Assert.Equal("390006000001000080", Hex(defaultKindFrame.AsSpan(10)));
    }

    // [F] kvs1 0x38 + 8 x BE32 = 33-byte body (OFF 18880)
    [Fact]
    public void SaveProgram_HeaderOnly_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.SaveProgram([0x00000001, 0x00000002, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(43, frame.Length);
        Assert.Equal("380000000100000002000000030000000400000005000000060000000700000008", Hex(frame.AsSpan(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyenceProgramTransferFrames.SaveProgram([1, 2]));
    }

    [Fact]
    public void SaveProgram_WithPayload_FrameMatchesRecoveredBytes()
    {
        uint[] header = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] payload = [0xAA, 0xBB, 0xCC, 0xDD];
        var frame = KeyenceProgramTransferFrames.SaveProgram(header, payload);
        Assert.Equal(47, frame.Length);
        Assert.Equal("380000000100000002000000030000000400000005000000060000000700000008AABBCCDD", Hex(frame.AsSpan(10)));
    }

    // [F] 0x3B xx check-prog-data, 0x50 xx begin-diff, 0x51 end-diff
    [Theory]
    [InlineData("3B00")]
    [InlineData("5000")]
    public void TwoByteFrames_CarryOpcodeAndArgumentByte(string bodyHex)
    {
        var body = Convert.FromHexString(bodyHex);
        var frame = Array.Empty<byte>();
        if (body[0] == 0x3B) frame = KeyenceProgramTransferFrames.CheckProgData(body[1]);
        if (body[0] == 0x50) frame = KeyenceProgramTransferFrames.BeginDiffTrans(body[1]);
        Assert.Equal(bodyHex, Hex(frame.AsSpan(10)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4, 2)));
    }

    [Fact]
    public void EndDiffTrans_FrameMatchesRecoveredBytes()
    {
        Assert.Equal("51", Hex(KeyenceProgramTransferFrames.EndDiffTrans().AsSpan(10)));
    }

    // [F] 0x52 ... 9-byte body (kvs1 OFF 22592)
    [Fact]
    public void AddProgFromCpuProg_LegacyDescriptor_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.AddProgFromCpuProg([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal("520102030405060708", Hex(frame.AsSpan(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyenceProgramTransferFrames.AddProgFromCpuProg([1, 2]));
    }

    [Fact]
    public void AddProgFromCpuProg_CalibratedOverload_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.AddProgFromCpuProg(offset: 0x12345678, length: 0x00000400);
        Assert.Equal("521234567800000400", Hex(frame.AsSpan(10)));
    }

    [Fact]
    public void AddProgFromThis_FrameMatchesRecoveredBytes()
    {
        var frameWithPayload = KeyenceProgramTransferFrames.AddProgFromThis([0xAA, 0xBB, 0xCC]);
        Assert.Equal("53AABBCC", Hex(frameWithPayload.AsSpan(10)));

        var frameEmpty = KeyenceProgramTransferFrames.AddProgFromThis();
        Assert.Equal("53", Hex(frameEmpty.AsSpan(10)));
    }

    // ---- 4. POSIX family ----
    [Fact]
    public void PosixOpen_Legacy_FrameMatchesRecoveredBytes()
    {
        Assert.Equal("460400", Hex(KeyenceProgramTransferFrames.PxOpen().AsSpan(10)));
        Assert.Equal("460C", Hex(KeyenceProgramTransferFrames.PxClose().AsSpan(10)));
        Assert.Equal("460D", Hex(KeyenceProgramTransferFrames.PxRead().AsSpan(10)));
    }

    [Fact]
    public void PxOpen_Calibrated_BuildsExpectedFrame()
    {
        // Path "A" -> UTF-16BE 00 41 (2 bytes)
        var frame = KeyenceProgramTransferFrames.PxOpen(
            path: "A",
            openMode: KeyenceProgramTransferFrames.FileOpenModeReadOnly,
            lockAuthCode: 0x1234,
            isAwait: true);

        // Header: 10B, Body: 12 + 2 = 14B -> Total 24B
        Assert.Equal(24, frame.Length);
        // 46 04 1234 02 00 01 00 01 00 0002 0041
        Assert.Equal("4604123402000100010000020041", Hex(frame.AsSpan(10)));

        // Empty path: 12B body
        var emptyPathFrame = KeyenceProgramTransferFrames.PxOpen(
            path: "",
            openMode: KeyenceProgramTransferFrames.FileOpenModeWriteOnly,
            lockAuthCode: 0,
            isAwait: false);
        Assert.Equal(22, emptyPathFrame.Length);
        Assert.Equal("460400000000020001000000", Hex(emptyPathFrame.AsSpan(10)));

        // Path over 512 bytes throws
        string tooLongPath = new string('x', 257); // 257 chars * 2 = 514 bytes
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyenceProgramTransferFrames.PxOpen(tooLongPath));
    }

    [Fact]
    public void PxClose_Calibrated_BuildsExpectedFrame()
    {
        var frame = KeyenceProgramTransferFrames.PxClose(fileDescriptor: 0x0005, lockAuthCode: 0x1234, isAwait: true);
        Assert.Equal(18, frame.Length);
        Assert.Equal("460C123402000005", Hex(frame.AsSpan(10)));
    }

    [Fact]
    public void PxRead_Calibrated_BuildsExpectedFrame()
    {
        var frame = KeyenceProgramTransferFrames.PxRead(
            fileDescriptor: 2,
            offset: 0x1000,
            length: 1000,
            lockAuthCode: 0x5678,
            isAwait: false);
        Assert.Equal(26, frame.Length);
        Assert.Equal("460D56780000000200001000000003E8", Hex(frame.AsSpan(10)));

        // Clamped at MaxPosixReadLength (2032 = 0x7F0)
        var clampedFrame = KeyenceProgramTransferFrames.PxRead(fileDescriptor: 2, offset: 0, length: 5000);
        Assert.Equal("460D00000000000200000000000007F0", Hex(clampedFrame.AsSpan(10)));
    }

    [Theory]
    [InlineData(1, "462201")]
    [InlineData(0x41, "462241")]
    public void CommitStart_Legacy_CarriesCommitExecKind(byte kind, string expectedBodyHex)
    {
        var frame = KeyenceProgramTransferFrames.CommitStart(kind);
        Assert.Equal(expectedBodyHex, Hex(frame.AsSpan(10)));
        Assert.Equal("4623", Hex(KeyenceProgramTransferFrames.CommitEnd().AsSpan(10)));
    }

    [Fact]
    public void CommitStart_Calibrated_BuildsExpectedFrame()
    {
        // Path "X" -> UTF-16BE 00 58 (2 bytes)
        var frame = KeyenceProgramTransferFrames.CommitStart(
            commitExecKind: KeyenceProgramTransferFrames.CommitKindProgramRead,
            path: "X",
            lockAuthCode: 0x1122,
            isRead: true,
            isAwait: false);

        // Header: 10B, Body: 12 + 2 = 14B -> Total 24B
        // 46 22 1122 00 40 0041 01 00 0002 0058
        Assert.Equal(24, frame.Length);
        Assert.Equal("4622112200400041010000020058", Hex(frame.AsSpan(10)));
    }

    [Fact]
    public void CommitEnd_Calibrated_BuildsExpectedFrame()
    {
        var frame = KeyenceProgramTransferFrames.CommitEnd(
            commitId: 0x000A,
            commitType: KeyenceProgramTransferFrames.CommitTypeCommit,
            lockAuthCode: 0x3344,
            isAwait: true);
        Assert.Equal(18, frame.Length);
        Assert.Equal("462333440200000A", Hex(frame.AsSpan(10)));

        var discardFrame = KeyenceProgramTransferFrames.CommitEnd(
            commitId: 0x000B,
            commitType: KeyenceProgramTransferFrames.CommitTypeDiscard,
            lockAuthCode: 0,
            isAwait: false);
        Assert.Equal("462300000001000B", Hex(discardFrame.AsSpan(10)));
    }

    [Fact]
    public void PosixUnlock_BuildsExpectedFrame()
    {
        var frame = KeyenceProgramTransferFrames.PosixUnlock(0x1234);
        Assert.Equal(16, frame.Length);
        Assert.Equal("462112340000", Hex(frame.AsSpan(10)));
    }

    [Fact]
    public void PosixLockTimeoutExtend_BuildsExpectedFrame()
    {
        var frame = KeyenceProgramTransferFrames.PosixLockTimeoutExtend(0x1234, isAwait: true);
        Assert.Equal(15, frame.Length);
        Assert.Equal("4627123402", Hex(frame.AsSpan(10)));
    }

    // ---- 5. 0x63 write opcode ----
    // [F] 0x63 PxWrite variant (KVComm builder 0x1F360, appendmap 77-79)
    [Fact]
    public void PxWrite_AppendsDataVerbatim()
    {
        var frame = KeyenceProgramTransferFrames.PxWrite([0xDE, 0xAD, 0xBE, 0xEF]);
        Assert.Equal("63DEADBEEF", Hex(frame.AsSpan(10)));
    }

    // ---- 6. simulator control ----
    [Fact]
    public void SimulatorGetInitializationState_BuildsExpectedFrame()
    {
        var frame = KeyenceProgramTransferFrames.SimulatorGetInitializationState();
        Assert.Equal(14, frame.Length);
        Assert.Equal("3F064100", Hex(frame.AsSpan(10)));
    }

    // Every transfer frame is a valid ACL/TCP frame: header checksum must verify.
    [Fact]
    public void AllFrames_PassAclHeaderChecksum()
    {
        byte[][] frames =
        [
            KeyenceProgramTransferFrames.RequestConnect(),
            KeyenceProgramTransferFrames.Disconnect(),
            KeyenceProgramTransferFrames.GetReadSession(),
            KeyenceProgramTransferFrames.RequestType(),
            KeyenceProgramTransferFrames.RequestPlcType(),
            KeyenceProgramTransferFrames.UserAuthentication(),
            KeyenceProgramTransferFrames.UserAuthConfirmAuthority([1, 2, 3, 4, 5, 6, 7, 8]),
            KeyenceProgramTransferFrames.UserAuthWrapCommand([1, 2, 3, 4, 5, 6, 7, 8], [0x46, 0x21, 0, 0, 0, 0]),
            KeyenceProgramTransferFrames.UserAuthLogin([0x01, 0x02]),
            KeyenceProgramTransferFrames.LoadProgram([0, 0, 0, 0, 0, 0, 0]),
            KeyenceProgramTransferFrames.LoadProgram(0x1000, 0x200, 0x01),
            KeyenceProgramTransferFrames.SaveProgram([0, 0, 0, 0, 0, 0, 0, 0]),
            KeyenceProgramTransferFrames.SaveProgram([0, 0, 0, 0, 0, 0, 0, 0], [1, 2, 3, 4]),
            KeyenceProgramTransferFrames.CheckProgData(),
            KeyenceProgramTransferFrames.BeginDiffTrans(),
            KeyenceProgramTransferFrames.EndDiffTrans(),
            KeyenceProgramTransferFrames.AddProgFromCpuProg([1, 2, 3, 4, 5, 6, 7, 8]),
            KeyenceProgramTransferFrames.AddProgFromCpuProg(0x100, 0x200),
            KeyenceProgramTransferFrames.AddProgFromThis([1, 2]),
            KeyenceProgramTransferFrames.PxOpen(),
            KeyenceProgramTransferFrames.PxOpen("/ROM/test", KeyenceProgramTransferFrames.FileOpenModeReadOnly, 0x1234, true),
            KeyenceProgramTransferFrames.PxClose(),
            KeyenceProgramTransferFrames.PxClose(1, 0x1234, true),
            KeyenceProgramTransferFrames.PxRead(),
            KeyenceProgramTransferFrames.PxRead(1, 0x100, 500, 0x1234, false),
            KeyenceProgramTransferFrames.CommitStart(),
            KeyenceProgramTransferFrames.CommitStart(KeyenceProgramTransferFrames.CommitKindProgram, "/ROM/test", 0x1234, false, true),
            KeyenceProgramTransferFrames.CommitEnd(),
            KeyenceProgramTransferFrames.CommitEnd(1, KeyenceProgramTransferFrames.CommitTypeCommit, 0x1234, false),
            KeyenceProgramTransferFrames.PosixUnlock(0x1234),
            KeyenceProgramTransferFrames.PosixLockTimeoutExtend(0x1234, true),
            KeyenceProgramTransferFrames.PxWrite([0x00]),
            KeyenceProgramTransferFrames.SimulatorGetInitializationState()
        ];
        foreach (var frame in frames)
        {
            Assert.True(KeyenceAclTcpAdapter.VerifyChecksum(frame.AsSpan(0, 10), frame.AsSpan(10)),
                $"checksum failed for {Hex(frame.AsSpan(10))}");
            Assert.Equal(frame.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4)));
        }
    }
}