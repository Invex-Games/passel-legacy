using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     A new packet whose owner's other devices are to be notified. The worker reads the packet and the devices
///     when it gets to the job, so a packet deleted by then notifies no one.
/// </summary>
internal sealed record PushJob(string UserId, Guid PacketId);

internal interface IPushQueue
{
    /// <summary>
    ///     Queues the notifications of a packet that has been committed. Returns <c>false</c> when push is off or the
    ///     queue is full; the packet then notifies no one, and is stored all the same.
    /// </summary>
    bool TryEnqueue(PushJob job);
}

/// <summary>
///     The packets waiting for <see cref="PushWorker" /> on this instance. In memory only, like the link previews: a
///     job queued here is lost if the instance stops before it ran, and its packet notifies no one, which the feed
///     never notices.
/// </summary>
internal sealed partial class PushQueue(IOptions<PushOptions> options, ILogger<PushQueue> logger) : IPushQueue
{
    private readonly bool _enabled = options.Value.Enabled;

    // Bounded, and a write to a full queue fails at once rather than waiting: a notification is a nicety, and a flood
    // of packets must not hold up the requests that create them
    private readonly Channel<PushJob> _channel = Channel.CreateBounded<PushJob>(
        new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

    private long _dropped;

    public ChannelReader<PushJob> Reader =>
        _channel.Reader;

    /// <summary>
    ///     How many packets this instance has dropped from a full queue since it started.
    /// </summary>
    public long Dropped =>
        Interlocked.Read(ref _dropped);

    public bool TryEnqueue(PushJob job)
    {
        if (!_enabled)
            return false;

        if (_channel.Writer.TryWrite(job))
            return true;

        LogQueueFull(job.PacketId, Interlocked.Increment(ref _dropped));

        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The push queue is full; packet {PacketId} notifies no device ({Dropped} dropped since startup)")]
    private partial void LogQueueFull(Guid packetId, long dropped);
}
