#if UNITY_2021_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ConnectNet.Client
{
    /// <summary>
    /// HttpMessageHandler implementation backed by UnityWebRequest.
    /// Use this for Unity WebGL builds where System.Net.Http.SocketsHttpHandler is not available.
    /// Supports Unary and Server Streaming RPCs. Client Streaming and Bidi Streaming require HTTP/2
    /// (use YetAnotherHttpHandler on non-WebGL platforms instead).
    /// </summary>
    public class UnityWebRequestHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.Method.Method;
            var url = request.RequestUri?.ToString() ?? throw new ArgumentNullException(nameof(request.RequestUri));

            byte[]? body = null;
            string? contentType = null;
            if (request.Content != null)
            {
                body = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                contentType = request.Content.Headers.ContentType?.ToString();
            }

            var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Hook cancellation outside the executor so a cancel that arrives before the
            // request is even posted still fails the awaiter instead of hanging forever.
            using var ctRegistration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            // Must run on Unity main thread.
            var syncContext = SynchronizationContext.Current;
            if (syncContext != null)
            {
                syncContext.Post(_ =>
                {
                    // Fire-and-forget by design (must originate from the main thread). We never
                    // throw out of this lambda — every path completes the tcs.
                    _ = ExecuteRequestAsync(url, method, body, contentType, request.Headers, tcs, cancellationToken);
                }, null);
            }
            else
            {
                // No sync context (e.g., background thread in Editor). Best effort.
                _ = ExecuteRequestAsync(url, method, body, contentType, request.Headers, tcs, cancellationToken);
            }

            return await tcs.Task.ConfigureAwait(false);
        }

        private static async Task ExecuteRequestAsync(
            string url, string method, byte[]? body, string? contentType,
            HttpRequestHeaders requestHeaders,
            TaskCompletionSource<HttpResponseMessage> tcs,
            CancellationToken ct)
        {
            UnityWebRequest? uwr = null;
            try
            {
                if (method == "GET")
                {
                    uwr = UnityWebRequest.Get(url);
                }
                else
                {
                    uwr = new UnityWebRequest(url, method);
                    if (body != null)
                    {
                        uwr.uploadHandler = new UploadHandlerRaw(body);
                    }
                    uwr.downloadHandler = new DownloadHandlerBuffer();
                }

                // Set headers. UnityWebRequest.SetRequestHeader throws on CR/LF in either name or
                // value; we keep that behavior intact (defends against header smuggling on Unity).
                foreach (var header in requestHeaders)
                {
                    uwr.SetRequestHeader(header.Key, string.Join(",", header.Value));
                }
                if (contentType != null)
                {
                    uwr.SetRequestHeader("Content-Type", contentType);
                }

                using var abortRegistration = ct.Register(static state => ((UnityWebRequest?)state)?.Abort(), uwr);

                var op = uwr.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested)
                    {
                        // Cancellation already aborted the request via the registration above;
                        // bail out of the wait loop so we don't spin.
                        break;
                    }
                    await Task.Yield();
                }

                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }

                var response = new HttpResponseMessage((HttpStatusCode)uwr.responseCode);
                var responseBody = uwr.downloadHandler?.data ?? Array.Empty<byte>();
                response.Content = new ByteArrayContent(responseBody);

                var responseHeaders = uwr.GetResponseHeaders();
                if (responseHeaders != null)
                {
                    foreach (var kvp in responseHeaders)
                    {
                        if (kvp.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            response.Content.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                        }
                        else if (kvp.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
                        {
                            // On WebGL the browser (and UnityWebRequest) silently decompress
                            // gzip/br response bodies. Forwarding the original Content-Encoding
                            // would cause the upper layer to try decompressing again — at best
                            // an InvalidDataException, at worst a second-stage zip bomb. Strip it.
#if UNITY_WEBGL && !UNITY_EDITOR
                            // intentionally skipped
#else
                            response.Content.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
#endif
                        }
                        else
                        {
                            response.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                        }
                    }
                }

                tcs.TrySetResult(response);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                // Always dispose the native handle, even on cancel / exception paths.
                uwr?.Dispose();
            }
        }
    }
}
#endif
