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

            // Must run on Unity main thread
            var tcs = new TaskCompletionSource<HttpResponseMessage>();

            // Use UnitySynchronizationContext to post to main thread
            var syncContext = SynchronizationContext.Current;
            if (syncContext != null)
            {
                syncContext.Post(_ => ExecuteRequest(url, method, body, contentType, request.Headers, tcs, cancellationToken), null);
            }
            else
            {
                // If no sync context (e.g., called from background), execute directly
                // This may fail in WebGL but works in Editor
                ExecuteRequest(url, method, body, contentType, request.Headers, tcs, cancellationToken);
            }

            return await tcs.Task.ConfigureAwait(false);
        }

        private static async void ExecuteRequest(
            string url, string method, byte[]? body, string? contentType,
            HttpRequestHeaders requestHeaders,
            TaskCompletionSource<HttpResponseMessage> tcs,
            CancellationToken ct)
        {
            UnityWebRequest uwr;
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

                // Set headers
                foreach (var header in requestHeaders)
                {
                    uwr.SetRequestHeader(header.Key, string.Join(",", header.Value));
                }
                if (contentType != null)
                {
                    uwr.SetRequestHeader("Content-Type", contentType);
                }

                // Register cancellation
                using var registration = ct.Register(() => uwr.Abort());

                // Send and wait
                var op = uwr.SendWebRequest();
                while (!op.isDone)
                {
                    await Task.Yield();
                }

                if (ct.IsCancellationRequested)
                {
                    uwr.Dispose();
                    tcs.TrySetCanceled(ct);
                    return;
                }

                // Build HttpResponseMessage
                var response = new HttpResponseMessage((HttpStatusCode)uwr.responseCode);
                var responseBody = uwr.downloadHandler?.data ?? Array.Empty<byte>();
                response.Content = new ByteArrayContent(responseBody);

                // Copy response headers
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
                            response.Content.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                        }
                        else
                        {
                            response.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                        }
                    }
                }

                uwr.Dispose();
                tcs.TrySetResult(response);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
    }
}
#endif
