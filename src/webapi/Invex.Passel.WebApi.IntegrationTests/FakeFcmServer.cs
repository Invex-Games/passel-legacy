using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The FCM HTTP v1 API and Google's token endpoint, on the loopback, as far as the push sender uses them: it hands
///     out an access token for any assertion, records every <c>messages:send</c> and answers each with what the test
///     set for its token, 200 by default. The API reaches it through an <c>FcmEndpoint</c> the push tests register
///     and a service account key whose <c>token_uri</c> points here.
/// </summary>
internal sealed class FakeFcmServer : IAsyncDisposable
{
    public const string AccessToken = "fake-access-token";

    /// <summary>
    ///     One <c>messages:send</c>: its path, its Authorization header, its body, and the device token in it.
    /// </summary>
    public sealed record SendRequest(string Path, string? Authorization, JsonNode Body, string Token);

    private readonly WebApplication _app;
    private readonly Channel<SendRequest> _sends = Channel.CreateUnbounded<SendRequest>();
    private readonly ConcurrentDictionary<string, Func<Task<IResult>>> _answers = new();
    private int _tokenRequests;

    private FakeFcmServer(WebApplication app) =>
        _app = app;

    public int Port { get; private set; }

    public Uri BaseAddress =>
        new($"http://127.0.0.1:{Port}/");

    public string TokenUri =>
        $"http://127.0.0.1:{Port}/token";

    /// <summary>
    ///     How often the sender's credential has asked for an access token.
    /// </summary>
    public int TokenRequests =>
        Volatile.Read(ref _tokenRequests);

    public static async Task<FakeFcmServer> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        // The API's appsettings.json is copied next to the test assembly, and its AllowedHosts names the production
        // host, which the builder would read and answer every other host with 400
        builder.Services.Configure<HostFilteringOptions>(options => options.AllowedHosts = ["*"]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var server = new FakeFcmServer(app);
        server.Map(app);

        await app.StartAsync(cancellationToken);
        server.Port = new Uri(app.Urls.Single()).Port;

        return server;
    }

    private void Map(WebApplication app)
    {
        app.MapPost("/token",
            () =>
            {
                Interlocked.Increment(ref _tokenRequests);

                return Results.Json(new
                {
                    access_token = AccessToken,
                    expires_in = 3600,
                    token_type = "Bearer",
                });
            });

        app.MapPost("/v1/projects/{project}/{action}",
            async (HttpContext context) =>
            {
                var body = (await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted))!;
                var token = body["message"]!["token"]!.GetValue<string>();

                await _sends.Writer.WriteAsync(new(context.Request.Path,
                    context.Request.Headers.Authorization,
                    body,
                    token));

                return _answers.TryGetValue(token, out var answer)
                    ? await answer()
                    : Results.Json(new { name = "projects/passel-test/messages/1" });
            });
    }

    /// <summary>
    ///     Answers every send to <paramref name="token" /> with <paramref name="answer" />.
    /// </summary>
    public void AnswerFor(string token, Func<Task<IResult>> answer) =>
        _answers[token] = answer;

    /// <summary>
    ///     What FCM answers for a token whose app is gone.
    /// </summary>
    public static IResult Unregistered() =>
        Results.Content("""
            {"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}
            """,
            "application/json",
            statusCode: StatusCodes.Status404NotFound);

    public static IResult Unavailable() =>
        Results.Content("""
            {"error":{"code":503,"message":"The service is currently unavailable.","status":"UNAVAILABLE","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNAVAILABLE"}]}}
            """,
            "application/json",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    ///     The next send, or a failure once <paramref name="timeout" /> has passed without one.
    /// </summary>
    public async Task<SendRequest> NextSendAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(timeout);

        try
        {
            return await _sends.Reader.ReadAsync(waiting.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No message reached the fake FCM within {timeout}.");
        }
    }

    /// <summary>
    ///     Every send that arrives within <paramref name="silence" /> of the one before it, for a test to check that
    ///     nothing more came.
    /// </summary>
    public async Task<IReadOnlyList<SendRequest>> SendsUntilSilenceAsync(TimeSpan silence, CancellationToken cancellationToken)
    {
        var sends = new List<SendRequest>();

        while (true)
        {
            try
            {
                sends.Add(await NextSendAsync(silence, cancellationToken));
            }
            catch (TimeoutException)
            {
                return sends;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
