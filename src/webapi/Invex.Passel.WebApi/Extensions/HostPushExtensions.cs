using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Extensions;

internal static class HostPushExtensions
{
    /// <summary>
    ///     How long one request to FCM may take, the access token excepted. The worker retries a timeout.
    /// </summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Push notifications (roadmap 2.B1, ADR 0005): the options, the queue the packet store fills after each create,
    ///     the worker that drains it, and the FCM client it sends with. Everything is registered whether push is on or
    ///     not; with it off the queue takes nothing and the worker returns at once.
    /// </summary>
    public static void AddPasselPush(this WebApplicationBuilder builder)
    {
        // Validated at startup: half a configuration, or a credential that can't be used, stops the API there
        builder
            .Services
            .AddOptions<PushOptions>()
            .BindConfiguration(PushOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<PushOptions>, PushOptionsValidator>();
        builder.Services.TryAddSingleton(TimeProvider.System);

        builder
            .Services
            .AddSingleton<IFcmAccessTokenSource, FcmAccessTokenSource>()
            .AddSingleton<PushQueue>()
            .AddSingleton<IPushQueue>(services => services.GetRequiredService<PushQueue>())
            .AddHostedService<PushWorker>();

        // The worker is the one retry layer, honouring Retry-After, so the default handler's retries come off; a
        // second layer would send each message up to nine times. The Authorization header carries the access token,
        // so no header value reaches the client's logs. RemoveAllResilienceHandlers is marked experimental; it is the
        // documented way to take the default handler off one client, and the FCM tests fail if it stops working
#pragma warning disable EXTEXP0001
        builder
            .Services
            .AddHttpClient<IFcmSender, FcmSender>(client => client.Timeout = SendTimeout)
            .RemoveAllResilienceHandlers()
            .RedactLoggedHeaders(_ => true);
#pragma warning restore EXTEXP0001
    }
}
