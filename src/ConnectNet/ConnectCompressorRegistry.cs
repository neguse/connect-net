using System.Collections.Generic;
using System.Linq;

namespace ConnectNet;

public class ConnectCompressorRegistry
{
    private readonly Dictionary<string, ICompressor> _compressors = new();
    private ICompressor? _default;

    public void Register(ICompressor compressor)
    {
        _compressors[compressor.Name] = compressor;
        if (_default == null)
            _default = compressor;
    }

    public ICompressor? Get(string name) => _compressors.TryGetValue(name, out var c) ? c : null;

    public ICompressor? Default => _default;

    public IEnumerable<string> SupportedNames => _compressors.Keys;
}
