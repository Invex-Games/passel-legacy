using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     A device a packet's notification goes to: its id, its FCM token, and the <see cref="Device.PushKey" /> of that
///     token, which a clear after an <c>UNREGISTERED</c> answer must still find on the device.
/// </summary>
internal sealed record PushTarget(Guid DeviceId, string Token, string PushKey)
{
    // Records print every property; the token is a credential for reaching the device
    public override string ToString() =>
        $"{nameof(PushTarget)} {{ {nameof(DeviceId)} = {DeviceId} }}";
}

/// <summary>
///     Sends the notifications queued by <see cref="PushQueue" />, a few packets at a time: one FCM message to each
///     of the owner's Android devices with a token, except the one the packet came from. Best effort, like the link
///     previews: a notification that can't be sent after the retries is dropped, and the packet is no worse for it.
/// </summary>
internal sealed partial class PushWorker(
    PushQueue queue,
    IServiceScopeFactory scopeFactory,
    IDocumentStore store,
    IIdEncodingService encodingService,
    IOptions<PushOptions> options,
    ILogger<PushWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogDisabled();

            return Task.CompletedTask;
        }

        return Task.WhenAll(Enumerable
            .Range(0, options.Value.MaxConcurrentSends)
            .Select(_ => RunAsync(stoppingToken)));
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // One packet's surprise must not stop the others
                    LogFailed(exception, job.PacketId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(PushJob job, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        var packet = await query.LoadAsync<Packet>(job.PacketId, cancellationToken);

        // Deleted before its turn came, which is no error: there is nothing to open any more
        if (packet is null || packet.UserId != job.UserId)
        {
            LogGone(job.PacketId);

            return;
        }

        // A handful at most: the device cap bounds them. In registration order, so the sends go out in a known order
        var devices = await query
            .Query<Device>()
            .Where(d => d.UserId == job.UserId)
            .OrderBy(d => d.Id)
            .ToListAsync(cancellationToken);

        var targets = Targets(packet, devices);

        if (targets.Count == 0)
            return;

        // A source device removed since the packet was sent leaves the notification without a name
        var sourceName = devices.FirstOrDefault(d => d.Id == packet.SourceDeviceId)?.Name;
        var message = PushMessage.For(packet, sourceName, encodingService.Encode(packet.Id));
        var sender = scope.ServiceProvider.GetRequiredService<IFcmSender>();

        foreach (var target in targets)
        {
            var result = await SendWithRetriesAsync(sender,
                target.Token,
                message,
                options.Value,
                token => StillTargetedAsync(job.UserId, target, token),
                cancellationToken);

            switch (result)
            {
                case null:
                    LogWithdrawn(packet.Id, target.DeviceId);

                    break;

                case FcmSendResult.Sent:
                    LogSent(packet.Id, target.DeviceId);

                    break;

                case FcmSendResult.Unregistered:
                    await ClearAsync(scope.ServiceProvider, job.UserId, target);

                    break;

                case FcmSendResult.Transient transient:
                    LogDropped(packet.Id, target.DeviceId, transient.Reason);

                    break;

                case FcmSendResult.Failed failed:
                    LogRefused(packet.Id, target.DeviceId, failed.Reason);

                    break;
            }
        }
    }

    /// <summary>
    ///     Whether <paramref name="target" /> is still where this packet's notification may go: the device is still
    ///     there, still the user's, and still holds the token that was read for it. Read afresh, in a session of its
    ///     own, before every attempt: while a send waits or retries, the token can move to another account (whoever
    ///     registers it last holds it), be withdrawn at sign-out, or go with its device, and the user's notification
    ///     must then not reach it.
    /// </summary>
    private async Task<bool> StillTargetedAsync(string userId, PushTarget target, CancellationToken cancellationToken)
    {
        await using var session = store.QuerySession();
        var device = await session.LoadAsync<Device>(target.DeviceId, cancellationToken);

        return device is not null && device.UserId == userId && device.PushKey == target.PushKey;
    }

    /// <summary>
    ///     Clears a token FCM called dead, but only while the device still holds it: the app may have registered a
    ///     replacement while the message was in flight, and that one must stay.
    /// </summary>
    private async Task ClearAsync(IServiceProvider services, string userId, PushTarget target)
    {
        try
        {
            await services
                .GetRequiredService<IDeviceStoreService>()
                .Handle(new ClearDevicePush
                {
                    UserId = userId,
                    DeviceId = target.DeviceId,
                    ExpectedPushKey = target.PushKey,
                });

            LogUnregistered(target.DeviceId);
        }
        catch (DeviceWriteConflictException exception)
        {
            // The device kept changing under the clear; the next send finds the token dead again if it still is
            LogClearFailed(exception, target.DeviceId);
        }
    }

    /// <summary>
    ///     The devices <paramref name="packet" /> notifies, out of <paramref name="devices" />: its owner's devices with
    ///     an FCM token, except the one it was sent from. A Web Push subscription waits for the Web Push sender, and a
    ///     device of another user is never one.
    /// </summary>
    internal static IReadOnlyList<PushTarget> Targets(Packet packet, IEnumerable<Device> devices) =>
        devices
            .Where(device => device.UserId == packet.UserId && device.Id != packet.SourceDeviceId)
            .Select(device => device is { Push: DevicePush.Fcm fcm, PushKey: { } key }
                ? new PushTarget(device.Id, fcm.Token, key)
                : null)
            .OfType<PushTarget>()
            .ToList();

    /// <summary>
    ///     Sends the message, trying again after a transient failure up to <see cref="PushOptions.MaxAttempts" />
    ///     times in all. The wait before each new attempt doubles from <see cref="PushOptions.RetryDelay" />, and is at
    ///     least the <c>Retry-After</c> FCM asked for; one longer than <see cref="PushOptions.MaxRetryAfter" /> ends the
    ///     attempts. Anything but a transient failure is returned at once. <paramref name="stillTargeted" /> is asked
    ///     before every attempt, the first included; when it says no, nothing more is sent and the answer is
    ///     <c>null</c>: the token is no longer the target's.
    /// </summary>
    internal static async Task<FcmSendResult?> SendWithRetriesAsync(
        IFcmSender sender,
        string token,
        PushMessage message,
        PushOptions options,
        Func<CancellationToken, Task<bool>> stillTargeted,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;

        for (var attempt = 1;; attempt++)
        {
            if (!await stillTargeted(cancellationToken))
                return null;

            var result = await sender.SendAsync(token, message, cancellationToken);

            if (result is not FcmSendResult.Transient transient || attempt >= options.MaxAttempts)
                return result;

            if (transient.RetryAfter > options.MaxRetryAfter)
                return result;

            var backoff = options.RetryDelay * Math.Pow(2, attempt - 1);

            await delay(transient.RetryAfter is { } retryAfter && retryAfter > backoff
                    ? retryAfter
                    : backoff,
                cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Push notifications are off: set Push:Fcm:ProjectId and Push:Fcm:CredentialFile to send them")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Packet {PacketId} was deleted before its notifications were sent")]
    private partial void LogGone(Guid packetId);

    // The device id only: the token that moved or was withdrawn is never logged
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Device {DeviceId} gave up or lost its push token, or was removed, before packet {PacketId} reached it; nothing more is sent to it")]
    private partial void LogWithdrawn(Guid packetId, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Notified device {DeviceId} of packet {PacketId}")]
    private partial void LogSent(Guid packetId, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "FCM no longer knows the push token of device {DeviceId}; cleared, unless the device has registered another since")]
    private partial void LogUnregistered(Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Dropped the notification of packet {PacketId} to device {DeviceId} after the retries: {Reason}")]
    private partial void LogDropped(Guid packetId, Guid deviceId, string reason);

    // Error: the server's credential or project is wrong, or the message is, and every notification will fail alike
    [LoggerMessage(Level = LogLevel.Error,
        Message = "FCM refused the notification of packet {PacketId} to device {DeviceId}: {Reason}. The token is kept; check Push:Fcm:ProjectId and the service account's key and role")]
    private partial void LogRefused(Guid packetId, Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not clear the dead push token of device {DeviceId}")]
    private partial void LogClearFailed(Exception exception, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending the notifications of packet {PacketId} failed unexpectedly")]
    private partial void LogFailed(Exception exception, Guid packetId);
}
