using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace ConnectNet.Server;

/// <summary>
/// Shared protocol-level helpers for the Connect server handlers: strict content-type
/// parsing, Accept-Encoding negotiation with q-values, and bounded timeout handling.
/// </summary>
internal static class ConnectServerProtocol
{
    private const string UnaryPrefix = "application/";
    private const string StreamingPrefix = "application/connect+";

    /// <summary>
    /// Returns the media type portion of a Content-Type header (parameters such as
    /// <c>; charset=utf-8</c> stripped), trimmed.
    /// </summary>
    private static ReadOnlySpan<char> MediaTypeOf(string contentType)
    {
        var span = contentType.AsSpan();
        var semi = span.IndexOf(';');
        return (semi >= 0 ? span.Slice(0, semi) : span).Trim();
    }

    /// <summary>
    /// Strict Content-Type match: the media type must equal <paramref name="expected"/>
    /// exactly (ignoring case and optional parameters). Unlike a StartsWith check this
    /// rejects e.g. <c>application/protobuf</c> when <c>application/proto</c> is expected.
    /// </summary>
    internal static bool MatchesContentType(string? contentType, string expected)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;
        return MediaTypeOf(contentType!).Equals(expected.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the codec for a request by strictly parsing the Content-Type header.
    /// Returns null when the content type is missing, malformed, uses the wrong framing
    /// for the method type, or names a codec that is not registered. Callers must
    /// respond with 415 in that case — never fall back to a default codec.
    /// </summary>
    internal static ICodec? ResolveCodec(string? contentType, ConnectCodecRegistry registry, ConnectMethodType methodType)
    {
        if (string.IsNullOrEmpty(contentType))
            return null;

        var mediaType = MediaTypeOf(contentType!).ToString();
        var streaming = methodType != ConnectMethodType.Unary;

        if (streaming)
        {
            if (!mediaType.StartsWith(StreamingPrefix, StringComparison.OrdinalIgnoreCase))
                return null;
            return registry.Get(mediaType.Substring(StreamingPrefix.Length));
        }

        if (!mediaType.StartsWith(UnaryPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var codecName = mediaType.Substring(UnaryPrefix.Length);
        // A streaming content type on a unary method is invalid framing.
        if (codecName.StartsWith("connect+", StringComparison.OrdinalIgnoreCase))
            return null;
        return registry.Get(codecName);
    }

    /// <summary>
    /// Builds the Accept-Post header value advertising the content types this server
    /// accepts for the given method type.
    /// </summary>
    internal static string BuildAcceptPost(ConnectCodecRegistry registry, ConnectMethodType methodType)
    {
        var prefix = methodType != ConnectMethodType.Unary ? StreamingPrefix : UnaryPrefix;
        return string.Join(", ", registry.Names.OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => prefix + name));
    }

    /// <summary>
    /// Joins the compressor registry's supported names for use in Accept-Encoding /
    /// Connect-Accept-Encoding response headers.
    /// </summary>
    internal static string SupportedEncodings(ConnectCompressorRegistry? registry)
        => registry == null ? "identity" : string.Join(", ", registry.SupportedNames);

    /// <summary>
    /// Selects a response compressor from an Accept-Encoding style header, honoring
    /// q-values: entries with q=0 are explicitly unacceptable and must not be chosen.
    /// </summary>
    internal static ICompressor? NegotiateCompression(StringValues acceptEncoding, ConnectCompressorRegistry? registry)
    {
        if (registry == null || acceptEncoding.Count == 0)
            return null;

        HashSet<string>? accepted = null;
        var wildcard = false;

        foreach (var headerValue in acceptEncoding)
        {
            if (string.IsNullOrEmpty(headerValue))
                continue;
            foreach (var entry in headerValue!.Split(','))
            {
                var token = entry;
                var q = 1.0;
                var semi = entry.IndexOf(';');
                if (semi >= 0)
                {
                    token = entry.Substring(0, semi);
                    foreach (var param in entry.Substring(semi + 1).Split(';'))
                    {
                        var trimmed = param.Trim();
                        if (trimmed.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!double.TryParse(trimmed.Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out q))
                                q = 0; // malformed q-value: treat as unacceptable
                            break;
                        }
                    }
                }

                var name = token.Trim();
                if (name.Length == 0 || q <= 0)
                    continue;
                if (name == "*")
                {
                    wildcard = true;
                    continue;
                }
                accepted ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                accepted.Add(name);
            }
        }

        foreach (var name in registry.SupportedNames)
        {
            if (wildcard || (accepted != null && accepted.Contains(name)))
                return registry.Get(name);
        }
        return null;
    }

    /// <summary>
    /// Returns true when the Connect-Protocol-Version requirement is satisfied. The
    /// header is only enforced when <see cref="ConnectServerOptions.RequireConnectProtocolHeader"/>
    /// is enabled, matching connect-go's opt-in behavior.
    /// </summary>
    internal static bool ProtocolVersionSatisfied(HttpRequest request, ConnectServerOptions? options)
    {
        if (options == null || !options.RequireConnectProtocolHeader)
            return true;
        return request.Headers.TryGetValue("Connect-Protocol-Version", out var version) && version == "1";
    }

    /// <summary>
    /// Largest value <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts,
    /// in milliseconds. Larger client-supplied timeouts are clamped rather than allowed
    /// to throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    private const long MaxCancelAfterMs = uint.MaxValue - 2;

    /// <summary>
    /// Parses Connect-Timeout-Ms and, when present, starts a linked timeout CTS. The
    /// value is clamped to <see cref="ConnectServerOptions.MaxTimeoutMs"/> (when set) and
    /// always to the maximum value the timer infrastructure supports, so an attacker
    /// cannot crash the request with a huge header value.
    /// Returns null (and leaves <paramref name="ct"/> untouched) when no timeout applies.
    /// </summary>
    internal static CancellationTokenSource? StartTimeout(
        HttpContext httpContext,
        ConnectServerOptions? options,
        ref CancellationToken ct)
    {
        if (!httpContext.Request.Headers.TryGetValue("Connect-Timeout-Ms", out var timeoutStr) ||
            !long.TryParse(timeoutStr, out var timeoutMs) || timeoutMs <= 0)
        {
            return null;
        }

        var maxTimeout = options?.MaxTimeoutMs ?? 0;
        if (maxTimeout > 0 && timeoutMs > maxTimeout)
            timeoutMs = maxTimeout;
        if (timeoutMs > MaxCancelAfterMs)
            timeoutMs = MaxCancelAfterMs;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        ct = cts.Token;
        return cts;
    }

    /// <summary>
    /// Reads the request body into a byte array, refusing bodies larger than
    /// <paramref name="maxBytes"/>. Returns null when the limit is exceeded.
    /// Used by the hand-rolled health and reflection endpoints.
    /// </summary>
    internal static async System.Threading.Tasks.Task<byte[]?> ReadBodyWithLimitAsync(
        HttpRequest request, int maxBytes, CancellationToken ct)
    {
        using var ms = new System.IO.MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            if (ms.Length + read > maxBytes)
                return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
