namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record ClearDevicePush
{
    public required string UserId { get; init; }

    public required Guid DeviceId { get; init; }

    /// <summary>
    ///     When set, the token is cleared only while it is still the one with this <see cref="Device.PushKey" />: the
    ///     push sender's clear of a token FCM refused, which must leave alone a replacement the device registered while
    ///     the send was in flight. <c>null</c> clears whatever the device holds, as a sign-out does.
    /// </summary>
    public string? ExpectedPushKey { get; init; }
}
