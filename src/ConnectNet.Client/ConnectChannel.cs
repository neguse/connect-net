using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace ConnectNet.Client;

public class ConnectChannel
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly ICodec _codec;

    public ConnectChannel(HttpClient httpClient, string baseUri, ICodec? codec = null)
    {
        _httpClient = httpClient;
        _baseUri = new Uri(baseUri.TrimEnd('/'));
        _codec = codec ?? new ProtobufCodec();
    }

    public async Task<TRes> UnaryAsync<TReq, TRes>(
        string procedure,
        TReq request,
        CallOptions? options = null,
        CancellationToken ct = default)
        where TReq : IMessage<TReq>
        where TRes : IMessage<TRes>, new()
    {
        var body = _codec.Serialize(request);
        var uri = new Uri(_baseUri, procedure);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Content = new ByteArrayContent(body);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue($"application/{_codec.Name}");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        if (options?.Timeout is TimeSpan timeout)
        {
            httpRequest.Headers.Add("Connect-Timeout-Ms", ((long)timeout.TotalMilliseconds).ToString());
        }

        if (options?.Headers != null)
        {
            foreach (var header in options.Headers)
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        var httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw ConnectException.FromJson(errorBody);
        }

        var responseBytes = await httpResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var result = _codec.Deserialize<TRes>(responseBytes);

        // Extract Trailer-* headers
        if (options != null)
        {
            foreach (var header in httpResponse.Headers)
            {
                if (header.Key.StartsWith("Trailer-", StringComparison.OrdinalIgnoreCase))
                {
                    var trailerName = header.Key.Substring("Trailer-".Length);
                    options.ResponseTrailers[trailerName] = string.Join(",", header.Value);
                }
            }
        }

        return result;
    }
}
