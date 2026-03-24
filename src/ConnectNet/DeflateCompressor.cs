using System.IO;
using System.IO.Compression;

namespace ConnectNet;

public class DeflateCompressor : ICompressor
{
    public string Name => "deflate";

    public byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest))
        {
            deflate.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    public byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
