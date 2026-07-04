using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ConnectNet;

public class ConnectCodecRegistry
{
    // ConcurrentDictionary so Register/Get can be called from multiple threads without
    // tripping the classic Dictionary multithreaded-corruption bug.
    private readonly ConcurrentDictionary<string, ICodec> _codecs = new();
    private volatile ICodec? _default;

    public void Register(ICodec codec)
    {
        _codecs[codec.Name] = codec;
        _default ??= codec;
    }

    public ICodec? Get(string name) => _codecs.TryGetValue(name, out var c) ? c : null;

    public ICodec Default => _default ?? _codecs.Values.First();

    /// <summary>Names of all registered codecs, e.g. for advertising supported content types.</summary>
    public IReadOnlyCollection<string> Names => _codecs.Keys.ToArray();
}
