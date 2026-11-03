using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PlcMcp.Adapters;
using PlcMcp.Adapters.Protocols;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Tests;

// Loopback verification of the reverse-engineered Keyence ACL/TCP wire format. The
// fixture speaks the recovered spec byte-for-byte; every client-side verify (checksum,
// echo opcode, status, length) is exercised. These tests prove the frame implementation
// round-trips against the documented format - never hardware behaviour.
public sealed class KeyenceAclLoopbackTests
{
    private static TargetProfile Target(string family, int port, int timeout = 2000) =>
        new("fixture", PlcVendor.Keyence, family, null, null,
            new("127.0.0.1", port, TransportKind.KvCommTcp, TimeoutMs: timeout),
            [TransportKind.KvCommTcp], null, new([]), "physical-readonly");

    private static TagDefinition WordTag(string address, PlcDataType type = PlcDataType.Int16) =>
        new("word", address, type, CanWrite: false);

    // Independently builds a reply frame per the documented spec (checksum computed by a
    // local loop, not via the adapter's own builder) so the client's verify is meaningful.
    private static byte[] Reply(byte[] body)
    {
        var header = new byte[10];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length + 10);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)body.Length);
        ushort sum = 0;
        for (int i = 0; i < 8; i++) sum += header[i];
        foreach (var b in body) sum += b;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)(0 - sum));
        var frame = new byte[10 + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, 10);
        return frame;
    }

    private static async Task<byte[]> Read(NetworkStream stream, int count, CancellationToken ct)
    {
        var data = new byte[count];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }

    private static async Task WithServer(Func<NetworkStream, CancellationToken, Task> server, Func<int, Task> client)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var task = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await server(connection.GetStream(), timeout.Token);
        });
        try
        {
            await client(((IPEndPoint)listener.LocalEndpoint).Port);
            await task;
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await task; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Keyence_ReadsDmWordThroughConfiguredRuntime()
    {
        await WithServer(async (stream, ct) =>
        {
            // D100, count 1 -> 13 00 64 00 01 behind the 10-byte header
            Assert.Equal(Convert.FromHexString("0000000F00050000FF741300640001"), await Read(stream, 15, ct));
            await stream.WriteAsync(Reply(Convert.FromHexString("1300FF85")), ct); // -123
        }, async port =>
        {
            var host = ConfiguredPlcComposition.Create(new(1,
                [new("fixture", PlcVendor.Keyence, "KV-8000", new("127.0.0.1", port, TransportKind.KvCommTcp),
                    [WordTag("D100")])]));
            Assert.Equal(CapabilityStatus.Experimental, host.Service.GetTarget("fixture").Capabilities.GetStatus("read_tags"));
            var values = await host.Service.ReadAsync("fixture", ["word"]);

            Assert.Equal((short)-123, Assert.IsType<short>(Assert.Single(values).Value));
            Assert.Equal(QualityCode.Good, Assert.Single(values).Quality);
        });
    }

    [Fact]
    public async Task Keyence_ReadsUnsignedWord()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 15, ct);
            await stream.WriteAsync(Reply(Convert.FromHexString("1300FF85")), ct);
        }, async port =>
        {
            var result = await new KeyenceAclTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D100", PlcDataType.UInt16)]);
            Assert.Equal(65413, Assert.IsType<ushort>(Assert.Single(result).Value));
        });
    }

    [Fact]
    public async Task Keyence_RejectsNonWordTypeBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new KeyenceAclTcpAdapter().ReadAsync(
            Target("KV-8000", port: 1), [WordTag("D100", PlcDataType.Real)]));
    }

    [Fact]
    public async Task Keyence_RejectsBadDmAddressBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new KeyenceAclTcpAdapter().ReadAsync(
            Target("KV-8000", port: 1), [WordTag("Q100")]));
    }

    [Theory]
    [InlineData("1305FF85", "status")]        // non-zero status byte
    [InlineData("50 00 FF 85", "opcode")]     // wrong echo opcode
    public async Task Keyence_RejectsMalformedReplies(string bodyHex, string expectedError)
    {
        var body = bodyHex.Replace(" ", "");
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 15, ct);
            await stream.WriteAsync(Reply(Convert.FromHexString(body)), ct);
        }, async port =>
        {
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new KeyenceAclTcpAdapter().ReadAsync(
                Target("KV-8000", port), [WordTag("D100")]));
            Assert.Contains(expectedError, ex.Message);
        });
    }

    [Fact]
    public async Task Keyence_RejectsChecksumMismatchReply()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 15, ct);
            var reply = Reply(Convert.FromHexString("1300FF85"));
            reply[9] ^= 0x01; // corrupt the low checksum byte
            await stream.WriteAsync(reply, ct);
        }, async port =>
        {
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new KeyenceAclTcpAdapter().ReadAsync(
                Target("KV-8000", port), [WordTag("D100")]));
            Assert.Contains("checksum", ex.Message);
        });
    }

    [Fact]
    public async Task Keyence_ReadDeadlineClosesStalledConnection()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 15, ct);
            var oneByte = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(oneByte, ct));
        }, async port => await Assert.ThrowsAsync<TimeoutException>(() => new KeyenceAclTcpAdapter().ReadAsync(
            Target("KV-8000", port, timeout: 150), [WordTag("D100")])));
    }

    [Fact]
    public async Task Keyence_Probe_AcknowledgesTypeQuery()
    {
        await WithServer(async (stream, ct) =>
        {
            // type query frame = 10-byte header + single byte 0x03
            var request = await Read(stream, 11, ct);
            Assert.Equal((byte)0x03, request[10]);
            Assert.Equal(11u, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(0, 4)));
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4, 2)));
            await stream.WriteAsync(Reply(Convert.FromHexString("030000")), ct); // echo 03, status 00, payload words
        }, async port =>
        {
            var probe = await new KeyenceAclTcpAdapter().ProbeAsync(Target("KV-8000", port));
            Assert.True(probe.Reachable);
            Assert.False(probe.IsSimulation);
            Assert.Equal("ACL_CONNECTED", probe.State);
        });
    }

    [Fact]
    public void Keyence_BuildHeader_MatchesGoldenVector()
    {
        // body 13 00 64 00 01: total = 15 -> hdr[0..3]=0000000F, len=0005, route=0000,
        // sum(hdr[0..7]+body) = (15+5) + (0x13+0+0x64+0+1=120) = 140 -> checksum = -140 & 0xFFFF = 0xFF74
        var frame = KeyenceAclTcpAdapter.BuildReadFrame(100, 1);
        Assert.Equal("0000000F00050000FF741300640001", Convert.ToHexString(frame));
    }

    [Fact]
    public void Keyence_BuildHeader_HonorsStationRouteBytes()
    {
        var frame = KeyenceAclTcpAdapter.BuildReadFrame(100, 1, stationRoute: 0x0102);
        Assert.Equal((byte)0x01, frame[6]);
        Assert.Equal((byte)0x02, frame[7]);
        // sum now 140 + 3 = 143 -> checksum = -143 & 0xFFFF = 0xFF71
        Assert.Equal("0000000F00050102FF711300640001", Convert.ToHexString(frame));
    }

    [Fact]
    public void Keyence_VerifyChecksum_ValidatesGoldenVector()
    {
        var frame = KeyenceAclTcpAdapter.BuildReadFrame(300, 1);
        Assert.True(KeyenceAclTcpAdapter.VerifyChecksum(frame.AsSpan(0, 10), frame.AsSpan(10)));
        frame[9] ^= 0x01;
        Assert.False(KeyenceAclTcpAdapter.VerifyChecksum(frame.AsSpan(0, 10), frame.AsSpan(10)));
    }

    [Fact]
    public void KeyenceExampleProfile_LoadsAsReadOnlyTarget()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "profiles", "keyence.example.json");
        Assert.True(File.Exists(path), $"Missing example configuration: {path}");
        var host = ConfiguredPlcComposition.Load(path);

        var target = Assert.Single(host.Service.ListTargets());
        Assert.Equal(PlcVendor.Keyence, target.Vendor);
        Assert.Equal(TransportKind.KvCommTcp, target.Endpoint.Transport);
        Assert.Equal(8500, target.Endpoint.Port);
        Assert.Equal(CapabilityStatus.Experimental, target.Capabilities.GetStatus("read_tags"));
        Assert.Equal(CapabilityStatus.Unsupported, target.Capabilities.GetStatus("write_tags"));
        Assert.Equal(3, host.Service.ListTags(target.Id).Count);
        Assert.All(host.Service.ListTags(target.Id), tag => Assert.False(tag.CanWrite));
    }


    [Fact]
    public async Task Keyence_ReadsConsecutiveWordsInSingleBatchedFrame()
    {
        await WithServer(async (stream, ct) =>
        {
            // D100+D101 consecutive -> one frame, count=2: 13 00 64 00 02
            Assert.Equal(Convert.FromHexString("0000000F00050000FF731300640002"), await Read(stream, 15, ct));
            await stream.WriteAsync(Reply(Convert.FromHexString("130000640065")), ct); // 100, 101
        }, async port =>
        {
            var result = await new KeyenceAclTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D100"), WordTag("D101")]);
            Assert.Equal(2, result.Count);
            Assert.Equal((short)100, Assert.IsType<short>(result[0].Value));
            Assert.Equal((short)101, Assert.IsType<short>(result[1].Value));
        });
    }

    [Fact]
    public async Task Keyence_SplitsGappedAddressesIntoMultipleFrames()
    {
        int frames = 0;
        await WithServer(async (stream, ct) =>
        {
            Assert.Equal(Convert.FromHexString("0000000F00050000FF741300640001"), await Read(stream, 15, ct)); frames++;
            await stream.WriteAsync(Reply(Convert.FromHexString("13000064")), ct);
            Assert.Equal(Convert.FromHexString("0000000F00050000FF721300660001"), await Read(stream, 15, ct)); frames++;
            await stream.WriteAsync(Reply(Convert.FromHexString("13000066")), ct);
        }, async port =>
        {
            var result = await new KeyenceAclTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D100"), WordTag("D102")]);
            Assert.Equal(2, frames);
            Assert.Equal((short)100, Assert.IsType<short>(result[0].Value));
            Assert.Equal((short)102, Assert.IsType<short>(result[1].Value));
        });
    }

    [Fact]
    public async Task Keyence_KeepsInputOrderForUnsortedBatchedTags()
    {
        await WithServer(async (stream, ct) =>
        {
            // requested D101 then D100 -> merged into one frame starting at D100
            Assert.Equal(Convert.FromHexString("0000000F00050000FF731300640002"), await Read(stream, 15, ct));
            await stream.WriteAsync(Reply(Convert.FromHexString("130000640065")), ct);
        }, async port =>
        {
            var result = await new KeyenceAclTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D101"), WordTag("D100")]);
            Assert.Equal(2, result.Count);
            Assert.Equal((short)101, Assert.IsType<short>(result[0].Value));
            Assert.Equal((short)100, Assert.IsType<short>(result[1].Value));
        });
    }

    [Fact]
    public async Task Keyence_BatchedFrame_SupportsMixedSignedUnsigned()
    {
        await WithServer(async (stream, ct) =>
        {
            Assert.Equal(Convert.FromHexString("0000000F00050000FF731300640002"), await Read(stream, 15, ct));
            await stream.WriteAsync(Reply(Convert.FromHexString("1300FF85007B")), ct); // -123, 123
        }, async port =>
        {
            var result = await new KeyenceAclTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D100", PlcDataType.Int16), WordTag("D101", PlcDataType.UInt16)]);
            Assert.Equal((short)-123, Assert.IsType<short>(result[0].Value));
            Assert.Equal((ushort)123, Assert.IsType<ushort>(result[1].Value));
        });
    }
}