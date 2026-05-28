using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ConnectNet;

public class ConnectCompressorRegistry
{
    private readonly ConcurrentDictionary<string, ICompressor> _compressors = new();
    private volatile ICompressor? _default;

    public void Register(ICompressor compressor)
    {
        _compressors[compressor.Name] = compressor;
        _default ??= compressor;
    }

    public ICompressor? Get(string name) => _compressors.TryGetValue(name, out var c) ? c : null;

    public ICompressor? Default => _default;

    public IEnumerable<string> SupportedNames => _compressors.Keys;
}
