using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Connectrpc.Conformance.V1;
using ConnectNet;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace ConnectNet.Conformance;

internal class ConformanceServiceImpl : ConformanceServiceBase
{
    public override async Task<UnaryResponse> Unary(UnaryRequest request, ConnectContext context)
    {
        var def = request.ResponseDefinition;
        ApplyHeaders(def?.ResponseHeaders, context);
        ApplyTrailers(def?.ResponseTrailers, context);

        if (def?.ResponseDelayMs > 0)
            await Task.Delay((int)def.ResponseDelayMs);

        var payload = new ConformancePayload
        {
            RequestInfo = BuildRequestInfo(context, Any.Pack(request)),
        };

        if (def != null)
        {
            if (def.ResponseCase == UnaryResponseDefinition.ResponseOneofCase.Error)
            {
                throw BuildError(def.Error, payload);
            }
            if (def.ResponseData != null && !def.ResponseData.IsEmpty)
            {
                payload.Data = def.ResponseData;
            }
        }

        return new UnaryResponse { Payload = payload };
    }

    public override async Task<IdempotentUnaryResponse> IdempotentUnary(IdempotentUnaryRequest request, ConnectContext context)
    {
        var def = request.ResponseDefinition;
        ApplyHeaders(def?.ResponseHeaders, context);
        ApplyTrailers(def?.ResponseTrailers, context);

        if (def?.ResponseDelayMs > 0)
            await Task.Delay((int)def.ResponseDelayMs);

        var payload = new ConformancePayload
        {
            RequestInfo = BuildRequestInfo(context, Any.Pack(request)),
        };

        if (def != null)
        {
            if (def.ResponseCase == UnaryResponseDefinition.ResponseOneofCase.Error)
            {
                throw BuildError(def.Error, payload);
            }
            if (def.ResponseData != null && !def.ResponseData.IsEmpty)
            {
                payload.Data = def.ResponseData;
            }
        }

        return new IdempotentUnaryResponse { Payload = payload };
    }

    public override async IAsyncEnumerable<ServerStreamResponse> ServerStream(
        ServerStreamRequest request, ConnectContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var def = request.ResponseDefinition;
        ApplyHeaders(def?.ResponseHeaders, context);
        ApplyTrailers(def?.ResponseTrailers, context);

        var requestInfo = BuildRequestInfo(context, Any.Pack(request));

        if (def == null || def.ResponseData.Count == 0)
        {
            if (def?.Error != null)
            {
                throw BuildError(def.Error, new ConformancePayload { RequestInfo = requestInfo });
            }
            yield break;
        }

        for (int i = 0; i < def.ResponseData.Count; i++)
        {
            if (def.ResponseDelayMs > 0)
                await Task.Delay((int)def.ResponseDelayMs, ct);

            var payload = new ConformancePayload
            {
                Data = def.ResponseData[i],
            };
            if (i == 0)
            {
                payload.RequestInfo = requestInfo;
            }
            yield return new ServerStreamResponse { Payload = payload };
        }

        if (def.Error != null)
        {
            throw BuildError(def.Error);
        }
    }

    public override async Task<ClientStreamResponse> ClientStream(
        IAsyncEnumerable<ClientStreamRequest> requests, ConnectContext context)
    {
        UnaryResponseDefinition? def = null;
        var requestMessages = new List<Any>();

        await foreach (var req in requests)
        {
            def ??= req.ResponseDefinition;
            requestMessages.Add(Any.Pack(req));
        }

        ApplyHeaders(def?.ResponseHeaders, context);
        ApplyTrailers(def?.ResponseTrailers, context);

        if (def?.ResponseDelayMs > 0)
            await Task.Delay((int)def.ResponseDelayMs);

        var payload = new ConformancePayload
        {
            RequestInfo = BuildRequestInfo(context, requestMessages.ToArray()),
        };

        if (def != null)
        {
            if (def.ResponseCase == UnaryResponseDefinition.ResponseOneofCase.Error)
            {
                throw BuildError(def.Error, payload);
            }
            if (def.ResponseData != null && !def.ResponseData.IsEmpty)
            {
                payload.Data = def.ResponseData;
            }
        }

        return new ClientStreamResponse { Payload = payload };
    }

    public override async IAsyncEnumerable<BidiStreamResponse> BidiStream(
        IAsyncEnumerable<BidiStreamRequest> requests, ConnectContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        StreamResponseDefinition? def = null;
        bool fullDuplex = false;
        var allRequests = new List<Any>();
        int responseIndex = 0;

        await foreach (var req in requests.WithCancellation(ct))
        {
            if (def == null)
            {
                def = req.ResponseDefinition;
                fullDuplex = req.FullDuplex;
                ApplyHeaders(def?.ResponseHeaders, context);
                ApplyTrailers(def?.ResponseTrailers, context);
            }

            allRequests.Add(Any.Pack(req));

            if (fullDuplex)
            {
                // Full-duplex: respond after each request
                if (def == null || responseIndex >= def.ResponseData.Count)
                {
                    if (def?.Error != null)
                    {
                        throw BuildError(def.Error);
                    }
                    continue;
                }

                if (def.ResponseDelayMs > 0)
                    await Task.Delay((int)def.ResponseDelayMs, ct);

                var payload = new ConformancePayload
                {
                    Data = def.ResponseData[responseIndex],
                };
                if (responseIndex == 0)
                {
                    payload.RequestInfo = BuildRequestInfo(context, allRequests.ToArray());
                }
                else
                {
                    // Subsequent responses echo back only the last received request
                    payload.RequestInfo = new ConformancePayload.Types.RequestInfo();
                    payload.RequestInfo.Requests.Add(allRequests.Last());
                }
                responseIndex++;
                yield return new BidiStreamResponse { Payload = payload };
            }
        }

        if (!fullDuplex)
        {
            // Half-duplex: send all responses after reading all requests
            if (def != null)
            {
                for (int i = 0; i < def.ResponseData.Count; i++)
                {
                    if (def.ResponseDelayMs > 0)
                        await Task.Delay((int)def.ResponseDelayMs, ct);

                    var payload = new ConformancePayload
                    {
                        Data = def.ResponseData[i],
                    };
                    if (i == 0)
                    {
                        payload.RequestInfo = BuildRequestInfo(context, allRequests.ToArray());
                    }
                    yield return new BidiStreamResponse { Payload = payload };
                }
            }
        }

        if (def?.Error != null)
        {
            throw BuildError(def.Error);
        }
    }

    public override Task<UnimplementedResponse> Unimplemented(UnimplementedRequest request, ConnectContext context)
    {
        throw new ConnectException(ConnectCode.Unimplemented);
    }

    // --- Helpers ---

    private static void ApplyHeaders(
        IEnumerable<Connectrpc.Conformance.V1.Header>? headers, ConnectContext context)
    {
        if (headers == null) return;
        foreach (var h in headers)
        {
            // Join multi-values with comma (HTTP standard)
            context.ResponseHeaders[h.Name] = string.Join(", ", h.Value);
        }
    }

    private static void ApplyTrailers(
        IEnumerable<Connectrpc.Conformance.V1.Header>? trailers, ConnectContext context)
    {
        if (trailers == null) return;
        foreach (var t in trailers)
        {
            context.ResponseTrailers[t.Name] = string.Join(", ", t.Value);
        }
    }

    private static ConformancePayload.Types.RequestInfo BuildRequestInfo(
        ConnectContext context, params Any[] requestMessages)
    {
        var info = new ConformancePayload.Types.RequestInfo();
        foreach (var kv in context.RequestHeaders)
        {
            var header = new Connectrpc.Conformance.V1.Header { Name = kv.Key };
            // Split comma-joined values back into repeated values
            foreach (var v in kv.Value.Split(',').Select(s => s.Trim()))
            {
                header.Value.Add(v);
            }
            info.RequestHeaders.Add(header);
        }
        info.Requests.AddRange(requestMessages);
        return info;
    }

    private static ConnectCode ConvertCode(Code code) => code switch
    {
        Code.Canceled => ConnectCode.Canceled,
        Code.Unknown => ConnectCode.Unknown,
        Code.InvalidArgument => ConnectCode.InvalidArgument,
        Code.DeadlineExceeded => ConnectCode.DeadlineExceeded,
        Code.NotFound => ConnectCode.NotFound,
        Code.AlreadyExists => ConnectCode.AlreadyExists,
        Code.PermissionDenied => ConnectCode.PermissionDenied,
        Code.ResourceExhausted => ConnectCode.ResourceExhausted,
        Code.FailedPrecondition => ConnectCode.FailedPrecondition,
        Code.Aborted => ConnectCode.Aborted,
        Code.OutOfRange => ConnectCode.OutOfRange,
        Code.Unimplemented => ConnectCode.Unimplemented,
        Code.Internal => ConnectCode.Internal,
        Code.Unavailable => ConnectCode.Unavailable,
        Code.DataLoss => ConnectCode.DataLoss,
        Code.Unauthenticated => ConnectCode.Unauthenticated,
        _ => ConnectCode.Unknown,
    };

    private static ConnectException BuildError(Error error, ConformancePayload? payload = null)
    {
        var details = new List<ConnectErrorDetail>();
        foreach (var d in error.Details)
        {
            details.Add(new ConnectErrorDetail(d.TypeUrl, d.Value.ToByteArray()));
        }
        // Append RequestInfo to error details if payload is provided
        if (payload != null)
        {
            var requestInfoAny = Any.Pack(payload.RequestInfo);
            details.Add(new ConnectErrorDetail(requestInfoAny.TypeUrl, requestInfoAny.Value.ToByteArray()));
        }
        return new ConnectException(
            ConvertCode(error.Code),
            error.HasMessage ? error.Message : null,
            details);
    }
}
