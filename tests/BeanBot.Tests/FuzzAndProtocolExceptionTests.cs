using BeanBot.Protocol;
using BeanBot.Protocol.Hazel;
using BeanBot.Transport;
using Xunit;

namespace BeanBot.Tests;

public sealed class FuzzAndProtocolExceptionTests
{
    [Fact]
    public void Fuzz_TruncatedAndGarbageRootPackets_ThrowProtocolExceptionWithHexDump()
    {
        var registry = ProtocolRegistry.Default;
        var rng = new Random(1337);

        for (var i = 0; i < 300; i++)
        {
            var len = rng.Next(0, 64);
            var bytes = new byte[len];
            rng.NextBytes(bytes);

            var ex = Record.Exception(() => registry.DecodeRoot(bytes));
            if (ex is not null)
            {
                var pex = Assert.IsType<ProtocolException>(ex);
                Assert.False(string.IsNullOrWhiteSpace(pex.HexDump));
            }
        }
    }

    [Fact]
    public void Fuzz_TruncatedAndGarbageRpcs_ThrowProtocolExceptionWithHexDump()
    {
        var registry = ProtocolRegistry.Default;
        var rng = new Random(9001);

        for (var i = 0; i < 300; i++)
        {
            var len = rng.Next(0, 48);
            var bytes = new byte[len];
            rng.NextBytes(bytes);

            var ex = Record.Exception(() => registry.DecodeRpc(bytes));
            if (ex is not null)
            {
                var pex = Assert.IsType<ProtocolException>(ex);
                Assert.False(string.IsNullOrWhiteSpace(pex.HexDump));
            }
        }
    }

    [Fact]
    public async Task Fuzz_HazelFramer_TruncatedDatagrams_NeverCrash()
    {
        var transport = new UdpTransport();
        var framer = new HazelFramer(transport);
        var rng = new Random(4242);

        for (var i = 0; i < 200; i++)
        {
            var len = rng.Next(0, 80);
            var bytes = new byte[len];
            rng.NextBytes(bytes);

            // Avoid sending ACKs on an unconnected transport in fuzz loop unless Reliable/Hello/Ping triggers send.
            if (len > 0 && bytes[0] is (byte)UdpSendOption.Reliable or (byte)UdpSendOption.Hello or (byte)UdpSendOption.Ping)
            {
                bytes[0] = (byte)UdpSendOption.Normal;
            }

            var ex = await Record.ExceptionAsync(() => framer.ProcessDatagramAsync(bytes));
            if (ex is not null)
            {
                var pex = Assert.IsType<ProtocolException>(ex);
                Assert.False(string.IsNullOrWhiteSpace(pex.HexDump));
            }
        }
    }

    [Fact]
    public void MessageReader_OversizedVarint_ThrowsProtocolException()
    {
        byte[] badVarint = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80];
        using var reader = MessageReader.Get(badVarint);
        var ex = Assert.Throws<ProtocolException>(() => reader.ReadPackedUInt32());
        Assert.Contains("5 bytes", ex.Message);
    }
}
