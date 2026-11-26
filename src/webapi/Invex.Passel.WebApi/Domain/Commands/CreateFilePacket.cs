namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record CreateFilePacket
{
    public required string UserId { get; init; }

    public required Guid ParcelId { get; init; }

    /// <summary>
    ///     The name to send the file under, or <c>null</c> to keep the one it was uploaded with.
    /// </summary>
    public required string? FileName { get; init; }

    /// <summary>
    ///     The sender's own device the packet was sent from, or <c>null</c> when the client did not say. Already checked
    ///     to be one of the user's devices.
    /// </summary>
    public Guid? SourceDeviceId { get; init; }
}
