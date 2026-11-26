using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     Refuses a push configuration that is half there or whose credential can't be used, so the API fails at startup
///     rather than at the first packet: both FCM settings or neither, a project id Google would accept, and a
///     credential file that can be read and holds a service account's key.
/// </summary>
internal sealed partial class PushOptionsValidator : IValidateOptions<PushOptions>
{
    public ValidateOptionsResult Validate(string? name, PushOptions options)
    {
        var fcm = options.Fcm;
        var hasProject = !string.IsNullOrWhiteSpace(fcm.ProjectId);
        var hasCredential = !string.IsNullOrWhiteSpace(fcm.CredentialFile);

        if (!hasProject && !hasCredential)
            return ValidateOptionsResult.Success;

        if (!hasProject || !hasCredential)
            return ValidateOptionsResult.Fail(
                $"Push:Fcm:ProjectId and Push:Fcm:CredentialFile go together: {(hasProject ? "ProjectId" : "CredentialFile")} " +
                $"is set and {(hasProject ? "CredentialFile" : "ProjectId")} is not. Set both to send push notifications, " +
                "or neither to leave them off.");

        var failures = new List<string>();

        // It goes into the FCM URL's path (escaped there in any case), so nothing but what a Google Cloud project id,
        // an older domain-scoped one (example.com:project) or a project number can hold
        if (!ProjectIdPattern().IsMatch(fcm.ProjectId!))
            failures.Add($"Push:Fcm:ProjectId '{fcm.ProjectId}' is not a Google Cloud project id: lower-case letters, " +
                         "digits and hyphens (with a domain and a colon in front for an older project), or the project number.");

        if (CredentialProblem(fcm.CredentialFile!) is { } problem)
            failures.Add(problem);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    ///     Why the file at <paramref name="path" /> can't be the sender's credential, or <c>null</c> when it can. The
    ///     messages name the path and the kind of failure, never anything read from the file.
    /// </summary>
    internal static string? CredentialProblem(string path)
    {
        if (!File.Exists(path))
            return $"Push:Fcm:CredentialFile '{path}' does not exist, or this process may not see it.";

        try
        {
            FcmAccessTokenSource.Load(path);

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Push:Fcm:CredentialFile '{path}' can't be read by this process ({exception.GetType().Name}); " +
                   "on the server it must belong to the API's user (1654) with mode 400.";
        }
        catch (Exception exception)
        {
            return $"Push:Fcm:CredentialFile '{path}' is not a service account's JSON key " +
                   $"({exception.GetType().Name}); create a key for the sending service account in the Google Cloud " +
                   "console and use the file it downloads.";
        }
    }

    [GeneratedRegex(@"^(?:[a-z0-9][a-z0-9.-]{0,61}[a-z0-9]:)?[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ProjectIdPattern();
}
