using System.Collections.Generic;
using System.Threading;

namespace ConnectNet;

public class ConnectContext
{
    public IDictionary<string, string> RequestHeaders { get; }
    public IDictionary<string, string> ResponseHeaders { get; }
    public IDictionary<string, string> ResponseTrailers { get; }
    public CancellationToken CancellationToken { get; }

    public ConnectContext(
        IDictionary<string, string>? requestHeaders = null,
        CancellationToken cancellationToken = default)
    {
        RequestHeaders = requestHeaders ?? new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        ResponseHeaders = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        ResponseTrailers = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        CancellationToken = cancellationToken;
    }
}
