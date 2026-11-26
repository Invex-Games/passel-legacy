namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record CreateLinkPacket
{
    public required string UserId { get; init; }

    public required string? Title { get; init; }

    public required string Url { get; init; }

    /// <summary>
    ///     The sender's own device the packet was sent from, or <c>null</c> when the client did not say. Already checked
    ///     to be one of the user's devices.
    /// </summary>
    public Guid? SourceDeviceId { get; init; }
}
