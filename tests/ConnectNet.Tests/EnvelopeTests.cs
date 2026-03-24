using System.IO;
using System.Threading.Tasks;
using ConnectNet;
using Xunit;

namespace ConnectNet.Tests;

public class EnvelopeTests
{
    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream();
        await Envelope.WriteAsync(stream, 0x00, data);
        stream.Position = 0;
        var result = await Envelope.ReadAsync(stream);
        Assert.NotNull(result);
        Assert.Equal(0x00, result!.Value.flags);
        Assert.Equal(data, result.Value.data);
    }

    [Fact]
    public async Task WriteAndRead_EndStreamFlag()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("{\"metadata\":{}}");
        using var stream = new MemoryStream();
        await Envelope.WriteAsync(stream, Envelope.FlagEndStream, data);
        stream.Position = 0;
        var result = await Envelope.ReadAsync(stream);
        Assert.Equal(Envelope.FlagEndStream, result!.Value.flags);
    }

    [Fact]
    public async Task WriteAndRead_EmptyData()
    {
        using var stream = new MemoryStream();
        await Envelope.WriteAsync(stream, 0x00, System.Array.Empty<byte>());
        stream.Position = 0;
        var result = await Envelope.ReadAsync(stream);
        Assert.NotNull(result);
        Assert.Empty(result!.Value.data);
    }

    [Fact]
    public async Task Read_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();
        var result = await Envelope.ReadAsync(stream);
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAndRead_MultipleMessages()
    {
        using var stream = new MemoryStream();
        await Envelope.WriteAsync(stream, 0x00, new byte[] { 1 });
        await Envelope.WriteAsync(stream, 0x00, new byte[] { 2 });
        await Envelope.WriteAsync(stream, Envelope.FlagEndStream, new byte[] { 3 });
        stream.Position = 0;

        var r1 = await Envelope.ReadAsync(stream);
        Assert.Equal(new byte[] { 1 }, r1!.Value.data);
        var r2 = await Envelope.ReadAsync(stream);
        Assert.Equal(new byte[] { 2 }, r2!.Value.data);
        var r3 = await Envelope.ReadAsync(stream);
        Assert.Equal(Envelope.FlagEndStream, r3!.Value.flags);
        var r4 = await Envelope.ReadAsync(stream);
        Assert.Null(r4);
    }

    [Fact]
    public void EnvelopeHeader_BigEndian()
    {
        var stream = new MemoryStream();
        Envelope.WriteAsync(stream, 0x00, new byte[256]).Wait();
        var bytes = stream.ToArray();
        Assert.Equal(0x00, bytes[0]); // flags
        Assert.Equal(0x00, bytes[1]); // length MSB
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x01, bytes[3]);
        Assert.Equal(0x00, bytes[4]); // length LSB
        Assert.Equal(261, bytes.Length); // 5 header + 256 data
    }
}
