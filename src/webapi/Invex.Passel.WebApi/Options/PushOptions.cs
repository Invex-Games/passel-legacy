using System.ComponentModel.DataAnnotations;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     How new packets reach the user's other devices as notifications, bound from the <c>Push</c> configuration
///     section. Push is on only when <see cref="Fcm" /> names both a project and a credential; with neither, nothing
///     is queued or sent, and with only one of them the API refuses to start (<see cref="PushOptionsValidator" />).
/// </summary>
internal sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>
    ///     Firebase Cloud Messaging, which reaches the Android app.
    /// </summary>
    public FcmOptions Fcm { get; set; } = new();

    /// <summary>
    ///     Whether anything is sent: both FCM settings are set.
    /// </summary>
    public bool Enabled =>
        Fcm.IsConfigured;

    /// <summary>
    ///     How many packets may wait for their notifications on one instance. Beyond it, new packets notify no one.
    /// </summary>
    [Range(1, 100_000)]
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>
    ///     How many packets one instance sends notifications for at the same time.
    /// </summary>
    [Range(1, 64)]
    public int MaxConcurrentSends { get; set; } = 4;

    /// <summary>
    ///     How often one notification is attempted before it is dropped. Only answers that may pass (a 429, a 5xx, a
    ///     timeout or a connection error) are tried again.
    /// </summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    ///     The wait before the second attempt; each later one waits twice as long as the one before. A longer
    ///     <c>Retry-After</c> from FCM wins.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     The longest <c>Retry-After</c> waited for. FCM asking for a longer wait drops the notification instead.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
///     The Firebase project the notifications are sent through and the service account that may send them, bound from
///     <c>Push:Fcm</c>. Empty or blank counts as unset.
/// </summary>
internal sealed class FcmOptions
{
    /// <summary>
    ///     The Firebase (Google Cloud) project id, such as <c>passel-12345</c>.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    ///     The path of a service account's JSON key. The account needs the Firebase Cloud Messaging API Admin role on
    ///     the project and nothing else.
    /// </summary>
    public string? CredentialFile { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(CredentialFile);
}
