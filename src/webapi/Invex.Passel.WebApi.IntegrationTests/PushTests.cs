using System.Net;
using System.Text.Json.Nodes;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Services;
using Invex.Passel.WebApi.Tests;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     Push notifications end to end, against <see cref="FakeFcmServer" />: a host of its own with push on, a service
///     account key made for the test whose token endpoint is the fake's, and the fake as the FCM endpoint. One packet
///     is sent at a time (<c>Push:MaxConcurrentSends</c> 1), so a later packet's message reaching the fake shows that
///     the worker has finished with the ones before it.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class PushTests(PasselApiFixture fixture) : IAsyncLifetime
{
    /// <summary>
    ///     How long to wait for a message that is coming: the worker sends as soon as the create has committed.
    /// </summary>
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How long the fake is watched for a message that must not come.
    /// </summary>
    private static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(750);

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private FakeFcmServer _fcm = null!;
    private PasselApiFactory _api = null!;
    private string _keyFile = null!;

    public async ValueTask InitializeAsync()
    {
        _fcm = await FakeFcmServer.StartAsync(CancellationToken);
        _keyFile = TestServiceAccount.WriteFile(TestServiceAccount.Json(_fcm.TokenUri));

        var connectionString = await fixture.CreateEmptyDatabaseAsync($"passel_push_{Guid.NewGuid():N}", CancellationToken);

        _api = new PasselApiFactory(connectionString,
            settings: new Dictionary<string, string?>
            {
                ["Push:Fcm:ProjectId"] = TestServiceAccount.ProjectId,
                ["Push:Fcm:CredentialFile"] = _keyFile,
                ["Push:MaxConcurrentSends"] = "1",
                ["Push:MaxAttempts"] = "2",
                ["Push:RetryDelay"] = "00:00:00",
            },
            services: services => services.AddSingleton(new FcmEndpoint(_fcm.BaseAddress)));
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _fcm.DisposeAsync();
        File.Delete(_keyFile);
    }

    private static string NewFcmToken() =>
        $"fcm-{Guid.NewGuid():N}:APA91b{Guid.NewGuid():N}";

    /// <summary>
    ///     Registers an Android device with <paramref name="token" />, or without one.
    /// </summary>
    private static async Task<DeviceJson> AndroidAsync(HttpClient client, string name, string? token)
    {
        var device = await client.RegisterAsync(DeviceApi.NewInstallationId(), name, "android", HttpStatusCode.Created, CancellationToken);

        if (token is not null)
            using (var set = await client.PutPushAsync(device.Id, DeviceApi.FcmToken(token), CancellationToken))
                Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        return device;
    }

    private static Task<PacketJson> CreateTextFromAsync(HttpClient client, string? deviceId, string body) =>
        client.CreateAsync(new JsonObject
            {
                ["$type"] = "Text",
                ["title"] = "Private title",
                ["body"] = body,
                ["deviceId"] = deviceId,
            },
            CancellationToken);

    private async Task<string?> PushKeyOfAsync(string encodedDeviceId)
    {
        Assert.True(_api.Services.GetRequiredService<IIdEncodingService>().TryDecode(encodedDeviceId, out var id));

        await using var session = _api.Services.GetRequiredService<IDocumentStore>().QuerySession();

        return (await session.LoadAsync<Device>(id, CancellationToken))?.PushKey;
    }

    [Fact]
    public async Task A_new_packet_sends_one_message_to_each_other_android_device_of_its_owner()
    {
        using var alice = _api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = _api.CreateClientFor(PasselApiFixture.User("bob"));

        var (phoneToken, tabletToken) = (NewFcmToken(), NewFcmToken());
        var phone = await AndroidAsync(alice, "Pixel", phoneToken);
        await AndroidAsync(alice, "Tablet", tabletToken);
        await AndroidAsync(alice, "Old phone", null);

        var browser = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Chrome", "extension", HttpStatusCode.Created, CancellationToken);

        using (var subscribe = await alice.PutPushAsync(browser.Id,
                   DeviceApi.WebPushSubscription($"https://push.example/{Guid.NewGuid():N}"),
                   CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, subscribe.StatusCode);

        await AndroidAsync(bob, "Bob's phone", NewFcmToken());

        // From the phone: the tablet alone hears of it, named after the phone, with nothing of the note in it
        var note = await CreateTextFromAsync(alice, phone.Id, "Private body");

        var send = await _fcm.NextSendAsync(Arrival, CancellationToken);

        Assert.Empty(await _fcm.SendsUntilSilenceAsync(Silence, CancellationToken));
        Assert.Equal($"/v1/projects/{TestServiceAccount.ProjectId}/messages:send", send.Path);
        Assert.Equal($"Bearer {FakeFcmServer.AccessToken}", send.Authorization);
        Assert.True(_fcm.TokenRequests >= 1);
        Assert.Equal(tabletToken, send.Token);

        var expected = JsonNode.Parse($$"""
            {
              "message": {
                "token": "{{tabletToken}}",
                "notification": { "title": "Note from Pixel" },
                "data": { "packetId": "{{note.Id}}", "type": "Text" },
                "android": {
                  "priority": "HIGH",
                  "ttl": "86400s",
                  "notification": { "channel_id": "packets", "tag": "{{note.Id}}" }
                }
              }
            }
            """);

        Assert.True(JsonNode.DeepEquals(expected, send.Body), send.Body.ToJsonString());

        // Without a source device: both Android devices with a token, the link's host and nothing else of it
        await alice.CreateLinkAsync("Private name", "https://www.example.com/private/path?q=1", CancellationToken);

        var sends = await _fcm.SendsUntilSilenceAsync(Silence, CancellationToken);

        Assert.Equal(new[] { phoneToken, tabletToken }.Order(), sends.Select(s => s.Token).Order());

        foreach (var linkSend in sends)
        {
            var notification = linkSend.Body["message"]!["notification"]!;

            Assert.Equal("New link", notification["title"]!.GetValue<string>());
            Assert.Equal("www.example.com", notification["body"]!.GetValue<string>());
            Assert.Equal("Link", linkSend.Body["message"]!["data"]!["type"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task An_unregistered_answer_clears_the_devices_token()
    {
        using var alice = _api.CreateClientFor(PasselApiFixture.User("alice"));
        var deadToken = NewFcmToken();
        var source = await AndroidAsync(alice, "Pixel", NewFcmToken());
        var gone = await AndroidAsync(alice, "Uninstalled", deadToken);

        _fcm.AnswerFor(deadToken, () => Task.FromResult(FakeFcmServer.Unregistered()));

        await CreateTextFromAsync(alice, source.Id, "hello");

        Assert.Equal(deadToken, (await _fcm.NextSendAsync(Arrival, CancellationToken)).Token);

        await WaitUntilAsync(async () => (await alice.ListedAsync(gone.Id, CancellationToken)).PushUpdatedAt is null);

        // The source device was never sent to, and keeps its token
        Assert.NotNull((await alice.ListedAsync(source.Id, CancellationToken)).PushUpdatedAt);
    }

    [Fact]
    public async Task An_unregistered_answer_for_a_token_the_device_has_since_replaced_leaves_the_replacement()
    {
        using var alice = _api.CreateClientFor(PasselApiFixture.User("alice"));
        var (oldToken, newToken) = (NewFcmToken(), NewFcmToken());
        var source = await AndroidAsync(alice, "Pixel", NewFcmToken());
        var phone = await AndroidAsync(alice, "Phone", oldToken);

        // The answer for the old token waits until the phone has registered its new one
        var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _fcm.AnswerFor(oldToken,
            async () =>
            {
                await replaced.Task;

                return FakeFcmServer.Unregistered();
            });

        await CreateTextFromAsync(alice, source.Id, "first");

        Assert.Equal(oldToken, (await _fcm.NextSendAsync(Arrival, CancellationToken)).Token);

        using (var set = await alice.PutPushAsync(phone.Id, DeviceApi.FcmToken(newToken), CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var newKey = await PushKeyOfAsync(phone.Id);
        replaced.SetResult();

        // Sends run one packet at a time, so this one's message means the clear for the first is done
        await CreateTextFromAsync(alice, source.Id, "second");

        Assert.Equal(newToken, (await _fcm.NextSendAsync(Arrival, CancellationToken)).Token);
        Assert.Equal(newKey, await PushKeyOfAsync(phone.Id));
        Assert.NotNull(newKey);
    }

    [Fact]
    public async Task An_unavailable_fcm_is_tried_the_configured_number_of_times_and_the_token_kept()
    {
        using var alice = _api.CreateClientFor(PasselApiFixture.User("alice"));
        var token = NewFcmToken();
        var phone = await AndroidAsync(alice, "Phone", token);

        _fcm.AnswerFor(token, () => Task.FromResult(FakeFcmServer.Unavailable()));

        await alice.CreateTextAsync(null, "hello", CancellationToken);

        var first = await _fcm.NextSendAsync(Arrival, CancellationToken);
        var more = await _fcm.SendsUntilSilenceAsync(TimeSpan.FromSeconds(3), CancellationToken);

        // Push:MaxAttempts is 2, and the worker is the only layer that retries: the client's default resilience
        // handler would add retries of its own
        Assert.Equal([token, token], new[] { first }.Concat(more).Select(s => s.Token));
        Assert.NotNull((await alice.ListedAsync(phone.Id, CancellationToken)).PushUpdatedAt);
    }

    [Fact]
    public async Task A_packet_deleted_before_its_turn_notifies_no_one()
    {
        using var alice = _api.CreateClientFor(PasselApiFixture.User("alice"));
        var token = NewFcmToken();
        await AndroidAsync(alice, "Phone", token);

        // Holds the worker on the first packet while the second is created and deleted
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldToken = NewFcmToken();
        await AndroidAsync(alice, "Held", heldToken);

        _fcm.AnswerFor(heldToken,
            async () =>
            {
                await release.Task;

                return Results.Json(new { name = "projects/passel-test/messages/2" });
            });

        await alice.CreateTextAsync(null, "first", CancellationToken);

        var firstSends = new[]
        {
            await _fcm.NextSendAsync(Arrival, CancellationToken),
        }.Concat(await _fcm.SendsUntilSilenceAsync(Silence, CancellationToken)).ToList();

        var second = await alice.CreateTextAsync(null, "second", CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, await alice.DeletePacketAsync(second.Id, CancellationToken));

        release.SetResult();

        var third = await alice.CreateTextAsync(null, "third", CancellationToken);
        var later = await _fcm.SendsUntilSilenceAsync(TimeSpan.FromSeconds(2), CancellationToken);

        Assert.Contains(firstSends, s => s.Token == heldToken);
        Assert.All(later, s => Assert.Equal(third.Id, s.Body["message"]!["data"]!["packetId"]!.GetValue<string>()));
        Assert.Equal(new[] { heldToken, token }.Order(), later.Select(s => s.Token).Order());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Arrival;

        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"The condition did not hold within {Arrival}.");

            await Task.Delay(50, CancellationToken);
        }
    }
}
