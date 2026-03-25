using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Connectrpc.Conformance.V1;
using ConnectNet;
using ConnectNet.Client;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace ConnectNet.Conformance;

internal static class ClientHarness
{
    public static async Task RunAsync()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        while (true)
        {
            var request = StdioProtobuf.Read<ClientCompatRequest>(stdin);
            if (request == null)
                break; // EOF

            ClientCompatResponse response;
            try
            {
                response = await ExecuteRequestAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response = new ClientCompatResponse
                {
                    TestName = request.TestName,
                    Error = new ClientErrorResult { Message = ex.ToString() },
                };
            }

            StdioProtobuf.Write(stdout, response);
        }
    }

    private static async Task<ClientCompatResponse> ExecuteRequestAsync(ClientCompatRequest request)
    {
        // Reject raw_request -- only reference client supports that
        if (request.RawRequest != null)
        {
            return new ClientCompatResponse
            {
                TestName = request.TestName,
                Error = new ClientErrorResult { Message = "raw_request is not supported by this client" },
            };
        }

        // Build HttpClient
        HttpClient httpClient;
        try
        {
            httpClient = CreateHttpClient(request);
        }
        catch (Exception ex)
        {
            return new ClientCompatResponse
            {
                TestName = request.TestName,
                Error = new ClientErrorResult { Message = $"Failed to create HttpClient: {ex.Message}" },
            };
        }

        // Build codec
        ICodec codec;
        if (request.Codec == Codec.Json)
        {
            var typeRegistry = TypeRegistry.FromFiles(
                ServiceReflection.Descriptor,
                ConfigReflection.Descriptor);
            codec = new JsonCodec(typeRegistry);
        }
        else
        {
            codec = new ProtobufCodec();
        }

        // Build channel options
        var channelOptions = new ConnectChannelOptions();
        switch (request.Compression)
        {
            case Compression.Gzip:
                channelOptions.RequestCompressor = new GzipCompressor();
                break;
            case Compression.Deflate:
                channelOptions.RequestCompressor = new DeflateCompressor();
                break;
            case Compression.Identity:
            case Compression.Unspecified:
                // No compression
                break;
            default:
                return new ClientCompatResponse
                {
                    TestName = request.TestName,
                    Error = new ClientErrorResult { Message = $"Unsupported compression: {request.Compression}" },
                };
        }

        // Build base URI
        var scheme = request.ServerTlsCert.IsEmpty ? "http" : "https";
        var baseUri = $"{scheme}://{request.Host}:{request.Port}";

        var channel = new ConnectChannel(httpClient, baseUri, codec, channelOptions);

        // Build CallOptions
        var callOptions = new CallOptions();

        if (request.HasTimeoutMs)
        {
            callOptions.Timeout = TimeSpan.FromMilliseconds(request.TimeoutMs);
        }

        foreach (var header in request.RequestHeaders)
        {
            // Headers can have multiple values; join them
            callOptions.Headers[header.Name] = string.Join(",", header.Value);
        }

        if (request.UseGetHttpMethod)
        {
            callOptions.UseGet = true;
        }

        // Resolve the procedure path
        var service = request.HasService ? request.Service : "connectrpc.conformance.v1.ConformanceService";
        var method = request.HasMethod ? request.Method : GetDefaultMethod(request.StreamType);
        var procedure = $"/{service}/{method}";

        // Set up cancellation
        using var cts = new CancellationTokenSource();
        var cancelSpec = request.Cancel;

        try
        {
            var result = request.StreamType switch
            {
                StreamType.Unary => await ExecuteUnaryAsync(channel, procedure, request, callOptions, cancelSpec, cts),
                StreamType.ServerStream => await ExecuteServerStreamAsync(channel, procedure, request, callOptions, cancelSpec, cts),
                StreamType.ClientStream => await ExecuteClientStreamAsync(channel, procedure, request, callOptions, cancelSpec, cts),
                StreamType.HalfDuplexBidiStream => await ExecuteBidiStreamAsync(channel, procedure, request, callOptions, cancelSpec, cts),
                StreamType.FullDuplexBidiStream => await ExecuteBidiStreamAsync(channel, procedure, request, callOptions, cancelSpec, cts),
                _ => throw new NotSupportedException($"Unsupported stream type: {request.StreamType}"),
            };

            return new ClientCompatResponse
            {
                TestName = request.TestName,
                Response = result,
            };
        }
        catch (NotSupportedException ex)
        {
            return new ClientCompatResponse
            {
                TestName = request.TestName,
                Error = new ClientErrorResult { Message = ex.Message },
            };
        }
    }

    private static string GetDefaultMethod(StreamType streamType) => streamType switch
    {
        StreamType.Unary => "Unary",
        StreamType.ServerStream => "ServerStream",
        StreamType.ClientStream => "ClientStream",
        StreamType.HalfDuplexBidiStream => "BidiStream",
        StreamType.FullDuplexBidiStream => "BidiStream",
        _ => "Unary",
    };

    private static HttpClient CreateHttpClient(ClientCompatRequest request)
    {
        var httpVersion = request.HttpVersion == Connectrpc.Conformance.V1.HTTPVersion._2
            ? System.Net.HttpVersion.Version20
            : System.Net.HttpVersion.Version11;

        if (!request.ServerTlsCert.IsEmpty)
        {
            var serverCertBytes = request.ServerTlsCert.ToByteArray();
            var handler = new SocketsHttpHandler();

            handler.SslOptions.RemoteCertificateValidationCallback = (message, cert, chain, errors) =>
            {
                if (errors == SslPolicyErrors.None)
                    return true;

                // Trust the specific server certificate
                var trustedCert = X509CertificateLoader.LoadCertificate(serverCertBytes);
                var cert2 = cert != null ? new X509Certificate2(cert) : null;
                if (cert2 != null && cert2.Thumbprint == trustedCert.Thumbprint)
                    return true;

                // Try chain validation with the trusted cert
                if (chain != null && cert2 != null)
                {
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(trustedCert);
                    return chain.Build(cert2);
                }

                return false;
            };

            if (request.ClientTlsCreds != null)
            {
                var clientCert = X509Certificate2.CreateFromPem(
                    Encoding.UTF8.GetString(request.ClientTlsCreds.Cert.Span),
                    Encoding.UTF8.GetString(request.ClientTlsCreds.Key.Span));
                clientCert = X509CertificateLoader.LoadPkcs12(clientCert.Export(X509ContentType.Pkcs12), null);
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCert };
            }

            return new HttpClient(handler)
            {
                DefaultRequestVersion = httpVersion,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
        }

        var defaultHandler = new SocketsHttpHandler();
        return new HttpClient(defaultHandler)
        {
            DefaultRequestVersion = httpVersion,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    private static async Task<ClientResponseResult> ExecuteUnaryAsync(
        ConnectChannel channel,
        string procedure,
        ClientCompatRequest request,
        CallOptions callOptions,
        ClientCompatRequest.Types.Cancel? cancelSpec,
        CancellationTokenSource cts)
    {
        if (request.RequestMessages.Count != 1)
        {
            throw new NotSupportedException($"Unary RPC requires exactly 1 request message, got {request.RequestMessages.Count}");
        }

        var requestMsg = request.RequestMessages[0];

        // Schedule cancellation if needed
        if (cancelSpec != null && cancelSpec.CancelTimingCase == ClientCompatRequest.Types.Cancel.CancelTimingOneofCase.AfterCloseSendMs)
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(cancelSpec.AfterCloseSendMs));
        }

        var result = new ClientResponseResult();
        var payloads = new List<ConformancePayload>();

        try
        {
            // Determine which unary method to call based on the procedure
            if (procedure.EndsWith("/IdempotentUnary"))
            {
                var typedRequest = requestMsg.Unpack<IdempotentUnaryRequest>();
                var response = await channel.UnaryAsync<IdempotentUnaryRequest, IdempotentUnaryResponse>(
                    procedure, typedRequest, callOptions, cts.Token).ConfigureAwait(false);
                if (response.Payload != null)
                    payloads.Add(response.Payload);
            }
            else if (procedure.EndsWith("/Unimplemented"))
            {
                var typedRequest = requestMsg.Unpack<UnimplementedRequest>();
                await channel.UnaryAsync<UnimplementedRequest, UnimplementedResponse>(
                    procedure, typedRequest, callOptions, cts.Token).ConfigureAwait(false);
            }
            else
            {
                var typedRequest = requestMsg.Unpack<UnaryRequest>();
                var response = await channel.UnaryAsync<UnaryRequest, UnaryResponse>(
                    procedure, typedRequest, callOptions, cts.Token).ConfigureAwait(false);
                if (response.Payload != null)
                    payloads.Add(response.Payload);
            }
        }
        catch (ConnectException ex)
        {
            result.Error = ConvertError(ex);
        }
        catch (OperationCanceledException)
        {
            result.Error = new Error
            {
                Code = Code.Canceled,
                Message = "canceled",
            };
        }

        result.Payloads.AddRange(payloads);
        PopulateHeadersAndTrailers(result, callOptions);

        return result;
    }

    private static async Task<ClientResponseResult> ExecuteServerStreamAsync(
        ConnectChannel channel,
        string procedure,
        ClientCompatRequest request,
        CallOptions callOptions,
        ClientCompatRequest.Types.Cancel? cancelSpec,
        CancellationTokenSource cts)
    {
        if (request.RequestMessages.Count != 1)
        {
            throw new NotSupportedException($"Server stream RPC requires exactly 1 request message, got {request.RequestMessages.Count}");
        }

        var requestMsg = request.RequestMessages[0].Unpack<ServerStreamRequest>();
        var result = new ClientResponseResult();
        var payloads = new List<ConformancePayload>();

        // Schedule cancellation after close send
        if (cancelSpec != null && cancelSpec.CancelTimingCase == ClientCompatRequest.Types.Cancel.CancelTimingOneofCase.AfterCloseSendMs)
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(cancelSpec.AfterCloseSendMs));
        }

        var afterNumResponses = cancelSpec?.CancelTimingCase == ClientCompatRequest.Types.Cancel.CancelTimingOneofCase.AfterNumResponses
            ? (int?)cancelSpec.AfterNumResponses
            : null;

        try
        {
            await foreach (var response in channel.ServerStreamAsync<ServerStreamRequest, ServerStreamResponse>(
                procedure, requestMsg, callOptions, cts.Token).ConfigureAwait(false))
            {
                if (response.Payload != null)
                    payloads.Add(response.Payload);

                if (afterNumResponses.HasValue && payloads.Count >= afterNumResponses.Value)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
            }
        }
        catch (ConnectException ex)
        {
            result.Error = ConvertError(ex);
        }
        catch (OperationCanceledException)
        {
            result.Error = new Error
            {
                Code = Code.Canceled,
                Message = "canceled",
            };
        }

        result.Payloads.AddRange(payloads);
        PopulateHeadersAndTrailers(result, callOptions);

        return result;
    }

    private static async Task<ClientResponseResult> ExecuteClientStreamAsync(
        ConnectChannel channel,
        string procedure,
        ClientCompatRequest request,
        CallOptions callOptions,
        ClientCompatRequest.Types.Cancel? cancelSpec,
        CancellationTokenSource cts)
    {
        var result = new ClientResponseResult();
        var payloads = new List<ConformancePayload>();
        var numUnsent = 0;

        try
        {
            using var call = channel.ClientStreamAsync<ClientStreamRequest, ClientStreamResponse>(
                procedure, callOptions, cts.Token);

            var cancelBeforeClose = cancelSpec?.CancelTimingCase == ClientCompatRequest.Types.Cancel.CancelTimingOneofCase.BeforeCloseSend;

            for (int i = 0; i < request.RequestMessages.Count; i++)
            {
                if (request.RequestDelayMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(request.RequestDelayMs), cts.Token).ConfigureAwait(false);
                }

                try
                {
                    var msg = request.RequestMessages[i].Unpack<ClientStreamRequest>();
                    await call.SendAsync(msg).ConfigureAwait(false);
                }
                catch
                {
                    numUnsent = request.RequestMessages.Count - i;
                    throw;
                }
            }

            if (cancelBeforeClose)
            {
                cts.Cancel();
                throw new OperationCanceledException();
            }

            // Schedule cancellation after close send
            if (cancelSpec != null)
            {
                if (cancelSpec.CancelTimingCase == ClientCompatRequest.Types.Cancel.CancelTimingOneofCase.AfterCloseSendMs)
                {
                    cts.CancelAfter(TimeSpan.FromMilliseconds(cancelSpec.AfterCloseSendMs));
                }
                else if (cancelSpec.CancelTimingCase == ClientCompatRequest.Types.Cancel.CancelTimingOneofCase.None)
                {
                    // Cancel immediately after close send
                    cts.Cancel();
                    throw new OperationCanceledException();
                }
            }

            var response = await call.CloseAndReceiveAsync().ConfigureAwait(false);
            if (response.Payload != null)
                payloads.Add(response.Payload);
        }
        catch (ConnectException ex)
        {
            result.Error = ConvertError(ex);
        }
        catch (OperationCanceledException)
        {
            result.Error = new Error
            {
                Code = Code.Canceled,
                Message = "canceled",
            };
        }

        result.NumUnsentRequests = numUnsent;
        result.Payloads.AddRange(payloads);
        PopulateHeadersAndTrailers(result, callOptions);

        return result;
    }

    private static async Task<ClientResponseResult> ExecuteBidiStreamAsync(
        ConnectChannel channel,
        string procedure,
        ClientCompatRequest request,
        CallOptions callOptions,
        ClientCompatRequest.Types.Cancel? cancelSpec,
        CancellationTokenSource cts)
    {
        var result = new ClientResponseResult();
        var payloads = new List<ConformancePayload>();
        var numUnsent = 0;

        try
        {
            using var call = channel.BidiStreamAsync<BidiStreamRequest, BidiStreamResponse>(
                procedure, callOptions, cts.Token);

            var cancelBeforeClose = cancelSpec?.CancelTimingCase == ClientCompatRequest.Types.Cancel.CancelTimingOneofCase.BeforeCloseSend;

            // For half-duplex bidi (and our implementation which buffers all sends before receiving),
            // send all messages first, then receive all responses
            for (int i = 0; i < request.RequestMessages.Count; i++)
            {
                if (request.RequestDelayMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(request.RequestDelayMs), cts.Token).ConfigureAwait(false);
                }

                try
                {
                    var msg = request.RequestMessages[i].Unpack<BidiStreamRequest>();
                    await call.SendAsync(msg).ConfigureAwait(false);
                }
                catch
                {
                    numUnsent = request.RequestMessages.Count - i;
                    throw;
                }
            }

            if (cancelBeforeClose)
            {
                cts.Cancel();
                throw new OperationCanceledException();
            }

            // Schedule cancellation after close send
            if (cancelSpec != null)
            {
                if (cancelSpec.CancelTimingCase == ClientCompatRequest.Types.Cancel.CancelTimingOneofCase.AfterCloseSendMs)
                {
                    cts.CancelAfter(TimeSpan.FromMilliseconds(cancelSpec.AfterCloseSendMs));
                }
                else if (cancelSpec.CancelTimingCase == ClientCompatRequest.Types.Cancel.CancelTimingOneofCase.None)
                {
                    // Cancel immediately after close send
                    cts.Cancel();
                    throw new OperationCanceledException();
                }
            }

            var afterNumResponses = cancelSpec?.CancelTimingCase == ClientCompatRequest.Types.Cancel.CancelTimingOneofCase.AfterNumResponses
                ? (int?)cancelSpec.AfterNumResponses
                : null;

            await foreach (var response in call.CompleteAndReadAsync(cts.Token).ConfigureAwait(false))
            {
                if (response.Payload != null)
                    payloads.Add(response.Payload);

                if (afterNumResponses.HasValue && payloads.Count >= afterNumResponses.Value)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
            }
        }
        catch (ConnectException ex)
        {
            result.Error = ConvertError(ex);
        }
        catch (OperationCanceledException)
        {
            result.Error = new Error
            {
                Code = Code.Canceled,
                Message = "canceled",
            };
        }

        result.NumUnsentRequests = numUnsent;
        result.Payloads.AddRange(payloads);
        PopulateHeadersAndTrailers(result, callOptions);

        return result;
    }

    private static Error ConvertError(ConnectException ex)
    {
        var error = new Error
        {
            Code = MapConnectCode(ex.Code),
            Message = ex.Message,
        };

        foreach (var detail in ex.Details)
        {
            error.Details.Add(new Any
            {
                TypeUrl = detail.Type.StartsWith("type.googleapis.com/")
                    ? detail.Type
                    : $"type.googleapis.com/{detail.Type}",
                Value = ByteString.CopyFrom(detail.Value),
            });
        }

        return error;
    }

    private static Code MapConnectCode(ConnectCode code) => code switch
    {
        ConnectCode.Canceled => Code.Canceled,
        ConnectCode.Unknown => Code.Unknown,
        ConnectCode.InvalidArgument => Code.InvalidArgument,
        ConnectCode.DeadlineExceeded => Code.DeadlineExceeded,
        ConnectCode.NotFound => Code.NotFound,
        ConnectCode.AlreadyExists => Code.AlreadyExists,
        ConnectCode.PermissionDenied => Code.PermissionDenied,
        ConnectCode.ResourceExhausted => Code.ResourceExhausted,
        ConnectCode.FailedPrecondition => Code.FailedPrecondition,
        ConnectCode.Aborted => Code.Aborted,
        ConnectCode.OutOfRange => Code.OutOfRange,
        ConnectCode.Unimplemented => Code.Unimplemented,
        ConnectCode.Internal => Code.Internal,
        ConnectCode.Unavailable => Code.Unavailable,
        ConnectCode.DataLoss => Code.DataLoss,
        ConnectCode.Unauthenticated => Code.Unauthenticated,
        _ => Code.Unknown,
    };

    private static void PopulateHeadersAndTrailers(ClientResponseResult result, CallOptions callOptions)
    {
        foreach (var kvp in callOptions.ResponseHeaders)
        {
            var header = new Header { Name = kvp.Key };
            // Split multiple values back out
            foreach (var v in kvp.Value.Split(','))
            {
                header.Value.Add(v.Trim());
            }
            result.ResponseHeaders.Add(header);
        }

        foreach (var kvp in callOptions.ResponseTrailers)
        {
            var trailer = new Header { Name = kvp.Key };
            foreach (var v in kvp.Value.Split(','))
            {
                trailer.Value.Add(v.Trim());
            }
            result.ResponseTrailers.Add(trailer);
        }
    }
}
