using System;
using System.Collections.Generic;

namespace ConnectNet.Client;

public class CallOptions
{
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    public TimeSpan? Timeout { get; set; }
    public IDictionary<string, string> ResponseTrailers { get; } = new Dictionary<string, string>();
    public bool UseGet { get; set; } = false;
}
