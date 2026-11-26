using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     What FCM made of one message.
/// </summary>
[Union]
internal partial record FcmSendResult
{
    /// <summary>
    ///     Accepted for delivery.
    /// </summary>
    public sealed partial record Sent;

    /// <summary>
    ///     The token is dead: the app was uninstalled or its registration expired (404 <c>UNREGISTERED</c>), or FCM
    ///     says the token itself is not a valid one (400 <c>INVALID_ARGUMENT</c> naming <c>message.token</c>). It is
    ///     cleared from its device, unless the device has registered another since.
    /// </summary>
    public sealed partial record Unregistered;

    /// <summary>
    ///     A failure that may pass: a 429 or 5xx answer, a timeout or a connection error, worth another attempt after
    ///     <paramref name="RetryAfter" /> when FCM gave one.
    /// </summary>
    public sealed partial record Transient(string Reason, TimeSpan? RetryAfter);

    /// <summary>
    ///     A refusal another attempt won't change, and that says nothing about the token: the server's credential or
    ///     project is wrong (401, 403 such as <c>SENDER_ID_MISMATCH</c>, a 404 other than <c>UNREGISTERED</c>) or FCM
    ///     refused the message itself. The token is kept.
    /// </summary>
    public sealed partial record Failed(string Reason);
}

internal interface IFcmSender
{
    /// <summary>
    ///     Sends <paramref name="message" /> to the app holding <paramref name="token" />, once.
    /// </summary>
    Task<FcmSendResult> SendAsync(string token, PushMessage message, CancellationToken cancellationToken);
}

/// <summary>
///     Where the FCM HTTP v1 API is. Nothing registers one in the API, so the sender uses Google's; the tests register
///     their own fake.
/// </summary>
internal sealed record FcmEndpoint(Uri BaseAddress)
{
    public static readonly FcmEndpoint Google = new(new Uri("https://fcm.googleapis.com/"));
}

/// <summary>
///     A thin client of the FCM HTTP v1 API: one <c>POST v1/projects/{project}/messages:send</c> per token, with a
///     bearer token from <see cref="IFcmAccessTokenSource" />, and the answer sorted into what the caller does next.
///     A typed <see cref="HttpClient" /> without the default resilience handler: <see cref="PushWorker" /> retries,
///     honouring <c>Retry-After</c>, so that one layer decides how often a message is sent. The push token and the
///     access token travel in the body and the Authorization header only, never in the URL, and neither is logged.
/// </summary>
internal sealed class FcmSender(
    HttpClient http,
    IFcmAccessTokenSource tokens,
    IOptions<PushOptions> options,
    TimeProvider time,
    FcmEndpoint? endpoint = null) : IFcmSender
{
    private const string FcmErrorType = "type.googleapis.com/google.firebase.fcm.v1.FcmError";
    private const string BadRequestType = "type.googleapis.com/google.rpc.BadRequest";
    private const string TokenField = "message.token";

    private readonly Uri _sendUri = new(endpoint?.BaseAddress ?? FcmEndpoint.Google.BaseAddress,
        $"v1/projects/{Uri.EscapeDataString(options.Value.Fcm.ProjectId ?? string.Empty)}/messages:send");

    public async Task<FcmSendResult> SendAsync(string token, PushMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await tokens.GetAccessTokenAsync(cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, _sendUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(RequestBody(token, message), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return Classify(response.StatusCode, body, RetryAfter(response.Headers.RetryAfter, time.GetUtcNow()));
        }
        catch (TokenResponseException exception)
        {
            // Google refused the credential itself: a deleted key, a disabled account. The error code names which
            return new FcmSendResult.Failed($"Google refused the credential an access token ({exception.Error?.Error})");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FcmSendResult.Transient("the request timed out", null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new FcmSendResult.Transient(exception.GetType().Name, null);
        }
    }

    /// <summary>
    ///     The <c>messages:send</c> body: a notification message with data, sent at high priority on the
    ///     <see cref="PushMessage.ChannelId" /> channel, tagged with the packet id, kept for a day.
    /// </summary>
    internal static string RequestBody(string token, PushMessage message)
    {
        var notification = new JsonObject
        {
            ["title"] = message.Title,
        };

        if (message.Body is not null)
            notification["body"] = message.Body;

        return new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["token"] = token,
                ["notification"] = notification,
                ["data"] = new JsonObject
                {
                    ["packetId"] = message.PacketId,
                    ["type"] = message.Type,
                },
                ["android"] = new JsonObject
                {
                    ["priority"] = "HIGH",
                    ["ttl"] = $"{(long)PushMessage.TimeToLive.TotalSeconds}s",
                    ["notification"] = new JsonObject
                    {
                        ["channel_id"] = PushMessage.ChannelId,
                        ["tag"] = message.PacketId,
                    },
                },
            },
        }.ToJsonString();
    }

    /// <summary>
    ///     Sorts an answer by its status and the FCM error in its body (<c>error.details</c>). Only an answer that
    ///     names the token clears it: a 404 whose <c>errorCode</c> is <c>UNREGISTERED</c>, or a 400 whose
    ///     <c>google.rpc.BadRequest</c> detail has a field violation on <c>message.token</c>. A 400 without one points
    ///     at the message, which is this server's bug and no reason to drop the user's token.
    /// </summary>
    internal static FcmSendResult Classify(HttpStatusCode status, string? body, TimeSpan? retryAfter)
    {
        var code = (int)status;

        if (code is >= 200 and < 300)
            return new FcmSendResult.Sent();

        if (code is 429 or >= 500)
            return new FcmSendResult.Transient($"FCM answered {code}", retryAfter);

        var (errorCode, tokenViolation) = ReadError(body);

        if (code == 404 && errorCode == "UNREGISTERED")
            return new FcmSendResult.Unregistered();

        if (code == 400 && errorCode is null or "INVALID_ARGUMENT" && tokenViolation)
            return new FcmSendResult.Unregistered();

        return new FcmSendResult.Failed($"FCM answered {code} {errorCode ?? "without an FCM error code"}");
    }

    /// <summary>
    ///     The FCM error code in an error body, and whether a bad-request detail names the token. Nothing else is read
    ///     from it: the message text is not logged.
    /// </summary>
    private static (string? ErrorCode, bool TokenViolation) ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, false);

        try
        {
            if (JsonNode.Parse(body)?["error"]?["details"] is not JsonArray details)
                return (null, false);

            string? errorCode = null;
            var tokenViolation = false;

            foreach (var detail in details.OfType<JsonObject>())
            {
                var type = StringOf(detail["@type"]);

                if (type == FcmErrorType)
                    errorCode ??= StringOf(detail["errorCode"]);

                if (type == BadRequestType && detail["fieldViolations"] is JsonArray violations)
                    tokenViolation |= violations
                        .OfType<JsonObject>()
                        .Any(violation => StringOf(violation["field"]) == TokenField);
            }

            return (errorCode, tokenViolation);
        }
        catch (JsonException)
        {
            return (null, false);
        }

        static string? StringOf(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : null;
    }

    /// <summary>
    ///     How long a <c>Retry-After</c> asks the sender to wait, from now; <c>null</c> without one.
    /// </summary>
    internal static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now) =>
        header switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date > now
                ? date - now
                : TimeSpan.Zero,
            _ => null,
        };
}
