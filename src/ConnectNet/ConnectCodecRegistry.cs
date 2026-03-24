using System.Collections.Generic;
using System.Linq;

namespace ConnectNet;

public class ConnectCodecRegistry
{
    private readonly Dictionary<string, ICodec> _codecs = new();

    public void Register(ICodec codec) => _codecs[codec.Name] = codec;

    public ICodec? Get(string name) => _codecs.TryGetValue(name, out var c) ? c : null;

    public ICodec Default => _codecs.Values.First();
}
