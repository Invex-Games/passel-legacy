namespace Invex.Passel.WebApi.Services;

/// <summary>
///     What one notification says, whichever device it goes to: a title, a body for a link only, the data the app
///     opens the packet by, and the tag that makes a second notification of the same packet replace the first.
/// </summary>
/// <remarks>
///     Its text goes through Google's servers, so it carries no content the user wrote: a link's host, and for a
///     note or a file only the kind and the device it came from (ADR 0005).
/// </remarks>
internal sealed record PushMessage
{
    /// <summary>
    ///     The Android notification channel the app creates for packets.
    /// </summary>
    public const string ChannelId = "packets";

    /// <summary>
    ///     How long FCM keeps a notification for a phone that is offline.
    /// </summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromDays(1);

    public required string Title { get; init; }

    /// <summary>
    ///     The link's host, or <c>null</c>: a note or a file has no body.
    /// </summary>
    public required string? Body { get; init; }

    /// <summary>
    ///     The packet's public id, as the list returns it: the app's <c>packetId</c> and the notification's tag.
    /// </summary>
    public required string PacketId { get; init; }

    /// <summary>
    ///     The packet's kind as the wire names it: <c>Link</c>, <c>Text</c> or <c>File</c>.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    ///     The notification of <paramref name="packet" />, sent from the device called
    ///     <paramref name="sourceDeviceName" /> (<c>null</c> when the packet names none, or its device is gone).
    /// </summary>
    public static PushMessage For(Packet packet, string? sourceDeviceName, string encodedPacketId)
    {
        var (kind, type, body) = packet.Content switch
        {
            PacketContent.Link link => ("link", nameof(PacketContent.Link), HostOf(link.Url)),
            PacketContent.Text => ("note", nameof(PacketContent.Text), null),
            PacketContent.File => ("file", nameof(PacketContent.File), null),
            _ => throw new UnreachableException(),
        };

        return new()
        {
            Title = string.IsNullOrWhiteSpace(sourceDeviceName)
                ? $"New {kind}"
                : $"{char.ToUpperInvariant(kind[0])}{kind[1..]} from {sourceDeviceName}",
            Body = body,
            PacketId = encodedPacketId,
            Type = type,
        };
    }

    /// <summary>
    ///     The host of the link, nothing of its path or query, or <c>null</c> when it has none.
    /// </summary>
    private static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host
            : null;
}
