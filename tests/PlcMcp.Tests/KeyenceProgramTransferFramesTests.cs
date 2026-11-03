using System.Buffers.Binary;
using PlcMcp.Adapters.Protocols;

namespace PlcMcp.Tests;

// Byte-level verification of the reverse-engineered Keyence program-transfer command
// frames. Every assertion pins an [F] byte from kvrev/PROGRAM_AREA_AND_PROJECT_FORMAT.md:
// opcodes and provable constants are exact; [U] parameter areas are asserted only as their
// documented placeholder shape (zero-filled or caller-provided). These tests prove the
// builder is byte-auditable - not that download/upload works on hardware.
public sealed class KeyenceProgramTransferFramesTests
{
    private static string Hex(ReadOnlySpan<byte> frame) => Convert.ToHexString(frame);

    // [F] 0xB2 00 00 00 RequestPLCType / GetModuleTypeList
    [Fact]
    public void RequestPlcType_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.RequestPlcType();
        // 10-byte header + 4-byte body
        Assert.Equal(14, frame.Length);
        Assert.Equal((uint)14, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4)));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4, 2)));
        Assert.Equal("B2000000", Hex(frame.AsSpan(10)));
    }

    // [F] 0x2D user-authentication, AuthenticationFunction.PlcTransfer = 0x04
    [Fact]
    public void UserAuthentication_DefaultsToPlcTransferBit()
    {
        var frame = KeyenceProgramTransferFrames.UserAuthentication();
        Assert.Equal("2D04", Hex(frame.AsSpan(10)));
        var custom = KeyenceProgramTransferFrames.UserAuthentication(0x10);
        Assert.Equal("2D10", Hex(custom.AsSpan(10)));
    }

    // [F] kvs1 0x39 sub 0x06, 9-byte descriptor (OFF 19296)
    [Fact]
    public void LoadProgram_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.LoadProgram([0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]);
        Assert.Equal(19, frame.Length);
        Assert.Equal("391106223344556677", Hex(frame.AsSpan(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyenceProgramTransferFrames.LoadProgram([0x01]));
    }

    // [F] kvs1 0x38 + 8 x BE32 = 33-byte body (OFF 18880)
    [Fact]
    public void SaveProgram_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.SaveProgram([0x00000001, 0x00000002, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(43, frame.Length);
        Assert.Equal("380000000100000002000000030000000400000005000000060000000700000008", Hex(frame.AsSpan(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyenceProgramTransferFrames.SaveProgram([1, 2]));
    }

    // [F] 0x3B xx check-prog-data, 0x50 xx begin-diff, 0x51 end-diff
    [Theory]
    [InlineData("3B00")]
    [InlineData("5000")]
    public void TwoByteFrames_CarryOpcodeAndArgumentByte(string bodyHex)
    {
        var body = Convert.FromHexString(bodyHex);
        var frame = new byte[0];
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
    public void AddProgFromCpuProg_FrameMatchesRecoveredBytes()
    {
        var frame = KeyenceProgramTransferFrames.AddProgFromCpuProg([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal("520102030405060708", Hex(frame.AsSpan(10)));
    }

    // [F] POSIX family: 0x46 sub, BinaryCommandDefine KV2_SUB_PX* (PROGRAM_AREA §5.6)
    [Fact]
    public void PosixOpen_FrameMatchesRecoveredBytes()
    {
        Assert.Equal("460400", Hex(KeyenceProgramTransferFrames.PxOpen().AsSpan(10)));
        Assert.Equal("460C", Hex(KeyenceProgramTransferFrames.PxClose().AsSpan(10)));
        Assert.Equal("460D", Hex(KeyenceProgramTransferFrames.PxRead().AsSpan(10)));
    }

    // [F] CommitStart carries CommitExecKind.REQ_PROGRAM=0x01 (download) / 0x41 (upload)
    [Theory]
    [InlineData(1, "462201")]
    [InlineData(0x41, "462241")]
    public void CommitStart_CarriesCommitExecKind(byte kind, string expectedBodyHex)
    {
        var frame = KeyenceProgramTransferFrames.CommitStart(kind);
        Assert.Equal(expectedBodyHex, Hex(frame.AsSpan(10)));
        Assert.Equal("4623", Hex(KeyenceProgramTransferFrames.CommitEnd().AsSpan(10)));
    }

    // [F] 0x63 PxWrite variant (KVComm builder 0x1F360)
    [Fact]
    public void PxWrite_AppendsDataVerbatim()
    {
        var frame = KeyenceProgramTransferFrames.PxWrite([0xDE, 0xAD, 0xBE, 0xEF]);
        Assert.Equal("63DEADBEEF", Hex(frame.AsSpan(10)));
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
            KeyenceProgramTransferFrames.RequestPlcType(),
            KeyenceProgramTransferFrames.UserAuthentication(),
            KeyenceProgramTransferFrames.LoadProgram([0, 0, 0, 0, 0, 0, 0]),
            KeyenceProgramTransferFrames.SaveProgram([0, 0, 0, 0, 0, 0, 0, 0]),
            KeyenceProgramTransferFrames.CheckProgData(),
            KeyenceProgramTransferFrames.BeginDiffTrans(),
            KeyenceProgramTransferFrames.EndDiffTrans(),
            KeyenceProgramTransferFrames.PxOpen(),
            KeyenceProgramTransferFrames.PxClose(),
            KeyenceProgramTransferFrames.PxRead(),
            KeyenceProgramTransferFrames.CommitStart(),
            KeyenceProgramTransferFrames.CommitEnd(),
            KeyenceProgramTransferFrames.PxWrite([0x00])
        ];
        foreach (var frame in frames)
        {
            Assert.True(KeyenceAclTcpAdapter.VerifyChecksum(frame.AsSpan(0, 10), frame.AsSpan(10)),
                $"checksum failed for {Hex(frame.AsSpan(10))}");
            Assert.Equal(frame.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4)));
        }
    }
}