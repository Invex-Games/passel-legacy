using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     The OAuth 2 access tokens the FCM HTTP v1 API is called with.
/// </summary>
internal interface IFcmAccessTokenSource
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
///     Access tokens for the service account in <see cref="FcmOptions.CredentialFile" />, with the one scope that
///     sending needs. The credential is read on first use, and caches each token until shortly before it expires;
///     the key and the tokens are never logged.
/// </summary>
internal sealed class FcmAccessTokenSource(IOptions<PushOptions> options) : IFcmAccessTokenSource
{
    public const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly Lazy<ITokenAccess> _credential = new(() => Load(options.Value.Fcm.CredentialFile!));

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        _credential.Value.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);

    /// <summary>
    ///     The service account's credential, scoped for sending. Only a service account's key is accepted: a user's
    ///     or an external account's credential throws, as does a file that is not a credential at all.
    /// </summary>
    internal static ITokenAccess Load(string path)
    {
        var account = CredentialFactory.FromFile<ServiceAccountCredential>(path);

        return GoogleCredential.FromServiceAccountCredential(account).CreateScoped(Scope);
    }
}
