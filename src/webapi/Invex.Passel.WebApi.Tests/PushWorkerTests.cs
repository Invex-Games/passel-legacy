using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     Which devices a packet notifies, and how often a notification is tried: transient failures again a few times,
///     with backoff and the waits FCM asks for, everything else once.
/// </summary>
public sealed class PushWorkerTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static readonly PushMessage Message = new()
    {
        Title = "New note",
        Body = null,
        PacketId = "abc",
        Type = "Text",
    };

    private static Device DeviceOf(string userId, DevicePlatform platform, DevicePush? push) =>
        new Device
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            InstallationId = Guid.NewGuid().ToString(),
            Name = platform.ToString(),
            Platform = platform,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        }.WithPush(push);

    private static DevicePush.Fcm Fcm(string token) =>
        new()
        {
            Token = token,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static Packet PacketFrom(string userId, Guid? sourceDeviceId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            Content = new PacketContent.Text { Title = null, Body = "hello" },
            SourceDeviceId = sourceDeviceId,
        };

    [Fact]
    public void A_packet_notifies_the_owners_other_android_devices_with_a_token_and_no_one_else()
    {
        var source = DeviceOf("alice", DevicePlatform.Android, Fcm("source-token"));
        var phone = DeviceOf("alice", DevicePlatform.Android, Fcm("phone-token"));
        var tablet = DeviceOf("alice", DevicePlatform.Android, Fcm("tablet-token"));
        var withoutToken = DeviceOf("alice", DevicePlatform.Android, null);
        var browser = DeviceOf("alice", DevicePlatform.Extension, new DevicePush.WebPush
        {
            Endpoint = "https://push.example/1",
            P256dh = "key",
            Auth = "auth",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var bobs = DeviceOf("bob", DevicePlatform.Android, Fcm("bob-token"));

        var targets = PushWorker.Targets(PacketFrom("alice", source.Id), [source, phone, withoutToken, browser, bobs, tablet]);

        Assert.Equal([phone.Id, tablet.Id], targets.Select(target => target.DeviceId));
        Assert.Equal(["phone-token", "tablet-token"], targets.Select(target => target.Token));
        Assert.Equal([phone.PushKey, tablet.PushKey], targets.Select(target => target.PushKey));
    }

    [Fact]
    public void A_packet_without_a_source_notifies_every_android_device_of_the_owner()
    {
        var phone = DeviceOf("alice", DevicePlatform.Android, Fcm("phone-token"));
        var tablet = DeviceOf("alice", DevicePlatform.Android, Fcm("tablet-token"));

        Assert.Equal([phone.Id, tablet.Id], PushWorker.Targets(PacketFrom("alice", null), [phone, tablet]).Select(t => t.DeviceId));
    }

    [Fact]
    public void A_target_prints_without_its_token() =>
        Assert.DoesNotContain("secret-token", new PushTarget(Guid.NewGuid(), "secret-token", "key").ToString());

    private static readonly FcmSendResult Sent = new FcmSendResult.Sent();
    private static readonly FcmSendResult Unregistered = new FcmSendResult.Unregistered();
    private static readonly FcmSendResult Failed = new FcmSendResult.Failed("403");

    private static FcmSendResult Transient(TimeSpan? retryAfter = null) =>
        new FcmSendResult.Transient("503", retryAfter);

    private sealed class ScriptedSender(params FcmSendResult[] script) : IFcmSender
    {
        public int Calls { get; private set; }

        public Task<FcmSendResult> SendAsync(string token, PushMessage message, CancellationToken cancellationToken)
        {
            var result = script[Math.Min(Calls, script.Length - 1)];
            Calls++;

            return Task.FromResult(result);
        }
    }

    private static readonly PushOptions Options = new()
    {
        MaxAttempts = 3,
        RetryDelay = TimeSpan.FromSeconds(2),
        MaxRetryAfter = TimeSpan.FromMinutes(1),
    };

    private static async Task<(FcmSendResult Result, List<TimeSpan> Waits)> SendAsync(ScriptedSender sender)
    {
        var waits = new List<TimeSpan>();

        var result = await PushWorker.SendWithRetriesAsync(sender,
            "token",
            Message,
            Options,
            CancellationToken,
            (wait, _) =>
            {
                waits.Add(wait);

                return Task.CompletedTask;
            });

        return (result, waits);
    }

    [Theory]
    [InlineData(nameof(Sent))]
    [InlineData(nameof(Unregistered))]
    [InlineData(nameof(Failed))]
    public async Task Anything_but_a_transient_failure_is_tried_once(string name)
    {
        var answer = name switch
        {
            nameof(Sent) => Sent,
            nameof(Unregistered) => Unregistered,
            _ => Failed,
        };
        var sender = new ScriptedSender(answer);

        var (result, waits) = await SendAsync(sender);

        Assert.Same(answer, result);
        Assert.Equal(1, sender.Calls);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A_transient_failure_is_tried_again_with_doubling_waits_until_the_attempts_run_out()
    {
        var sender = new ScriptedSender(Transient());

        var (result, waits) = await SendAsync(sender);

        Assert.IsType<FcmSendResult.Transient>(result);
        Assert.Equal(3, sender.Calls);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], waits);
    }

    [Fact]
    public async Task A_transient_failure_that_passes_ends_in_what_came_next()
    {
        var sender = new ScriptedSender(Transient(), Unregistered);

        var (result, _) = await SendAsync(sender);

        Assert.Same(Unregistered, result);
        Assert.Equal(2, sender.Calls);
    }

    [Fact]
    public async Task A_retry_after_longer_than_the_backoff_is_waited_for()
    {
        var sender = new ScriptedSender(Transient(TimeSpan.FromSeconds(30)), Transient(TimeSpan.FromSeconds(1)), Sent);

        var (result, waits) = await SendAsync(sender);

        Assert.Same(Sent, result);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(4)], waits);
    }

    [Fact]
    public async Task A_retry_after_longer_than_the_most_waited_for_drops_the_notification()
    {
        var sender = new ScriptedSender(Transient(TimeSpan.FromMinutes(5)), Sent);

        var (result, waits) = await SendAsync(sender);

        Assert.IsType<FcmSendResult.Transient>(result);
        Assert.Equal(1, sender.Calls);
        Assert.Empty(waits);
    }
}
