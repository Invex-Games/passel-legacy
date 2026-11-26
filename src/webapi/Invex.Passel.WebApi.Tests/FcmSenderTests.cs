using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     The FCM client against a stub of the HTTP v1 API: where it sends and with what, and how it reads the answers.
///     Only an answer that names the token kills it; a credential or project problem keeps it, and so does a 400 that
///     points at the message.
/// </summary>
public sealed class FcmSenderTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static readonly PushMessage Message = new()
    {
        Title = "Link from Chrome",
        Body = "example.com",
        PacketId = "qrjMmYBVnK8m3coMo0hiMeN",
        Type = "Link",
    };

    private sealed class StubTokens : IFcmAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("access-token");
    }

    /// <summary>
    ///     Answers every request with one response and keeps what it was sent.
    /// </summary>
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));

            return respond();
        }
    }

    private static FcmSender Sender(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            new StubTokens(),
            Microsoft.Extensions.Options.Options.Create(new PushOptions
            {
                Fcm = new() { ProjectId = "passel-test", CredentialFile = "unused.json" },
            }),
            TimeProvider.System);

    private static string Error(int code, string status, params string[] details) =>
        $$$"""{"error":{"code":{{{code}}},"message":"Some message","status":"{{{status}}}","details":[{{{string.Join(",", details)}}}]}}""";

    private static string FcmError(string errorCode) =>
        $$"""{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"{{errorCode}}"}""";

    private static string BadRequest(string field) =>
        $$"""{"@type":"type.googleapis.com/google.rpc.BadRequest","fieldViolations":[{"field":"{{field}}","description":"Invalid"}]}""";

    [Fact]
    public async Task A_message_is_posted_to_the_projects_send_endpoint_with_a_bearer_token_and_the_device_token_in_the_body()
    {
        var handler = new StubHandler(() => new(HttpStatusCode.OK) { Content = new StringContent("""{"name":"projects/passel-test/messages/1"}""") });

        var result = await Sender(handler).SendAsync("device-token", Message, CancellationToken);

        Assert.IsType<FcmSendResult.Sent>(result);

        var (request, body) = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://fcm.googleapis.com/v1/projects/passel-test/messages:send", request.RequestUri!.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "access-token"), request.Headers.Authorization);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("device-token", JsonNode.Parse(body)!["message"]!["token"]!.GetValue<string>());

        // Neither token is anywhere in the URL
        Assert.DoesNotContain("token", request.RequestUri.AbsoluteUri);
    }

    [Fact]
    public void A_404_unregistered_kills_the_token() =>
        Assert.IsType<FcmSendResult.Unregistered>(
            FcmSender.Classify(HttpStatusCode.NotFound, Error(404, "NOT_FOUND", FcmError("UNREGISTERED")), null));

    [Fact]
    public void A_400_invalid_argument_naming_the_token_kills_it() =>
        Assert.IsType<FcmSendResult.Unregistered>(FcmSender.Classify(HttpStatusCode.BadRequest,
            Error(400, "INVALID_ARGUMENT", FcmError("INVALID_ARGUMENT"), BadRequest("message.token")),
            null));

    [Theory]
    [InlineData("message.android.ttl")]
    [InlineData("message.data")]
    public void A_400_invalid_argument_naming_the_message_keeps_the_token(string field) =>
        Assert.IsType<FcmSendResult.Failed>(FcmSender.Classify(HttpStatusCode.BadRequest,
            Error(400, "INVALID_ARGUMENT", FcmError("INVALID_ARGUMENT"), BadRequest(field)),
            null));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"error":{"code":400,"status":"INVALID_ARGUMENT"}}""")]
    public void A_400_that_names_nothing_keeps_the_token(string body) =>
        Assert.IsType<FcmSendResult.Failed>(FcmSender.Classify(HttpStatusCode.BadRequest, body, null));

    [Fact]
    public void A_404_that_is_not_unregistered_keeps_the_token() =>
        Assert.IsType<FcmSendResult.Failed>(
            FcmSender.Classify(HttpStatusCode.NotFound, Error(404, "NOT_FOUND"), null));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", "THIRD_PARTY_AUTH_ERROR")]
    [InlineData(HttpStatusCode.Forbidden, "PERMISSION_DENIED", "SENDER_ID_MISMATCH")]
    [InlineData(HttpStatusCode.Forbidden, "PERMISSION_DENIED", null)]
    public void A_credential_or_project_problem_keeps_the_token(HttpStatusCode status, string grpcStatus, string? errorCode)
    {
        var body = errorCode is null
            ? Error((int)status, grpcStatus)
            : Error((int)status, grpcStatus, FcmError(errorCode));

        var failed = Assert.IsType<FcmSendResult.Failed>(FcmSender.Classify(status, body, null));

        Assert.Contains(((int)status).ToString(), failed.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void A_429_or_5xx_may_pass_and_carries_the_retry_after(HttpStatusCode status)
    {
        var transient = Assert.IsType<FcmSendResult.Transient>(
            FcmSender.Classify(status, Error((int)status, "UNAVAILABLE", FcmError("UNAVAILABLE")), TimeSpan.FromSeconds(7)));

        Assert.Equal(TimeSpan.FromSeconds(7), transient.RetryAfter);
    }

    [Fact]
    public async Task A_retry_after_in_seconds_or_as_a_date_reaches_the_result()
    {
        var handler = new StubHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

            return response;
        });

        var transient = Assert.IsType<FcmSendResult.Transient>(
            await Sender(handler).SendAsync("device-token", Message, CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(30), transient.RetryAfter);

        var now = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.FromMinutes(2), FcmSender.RetryAfter(new(now.AddMinutes(2)), now));
        Assert.Equal(TimeSpan.Zero, FcmSender.RetryAfter(new(now.AddMinutes(-2)), now));
        Assert.Null(FcmSender.RetryAfter(null, now));
    }

    [Fact]
    public async Task A_connection_failure_may_pass()
    {
        var handler = new StubHandler(() => throw new HttpRequestException("refused"));

        Assert.IsType<FcmSendResult.Transient>(await Sender(handler).SendAsync("device-token", Message, CancellationToken));
    }
}
