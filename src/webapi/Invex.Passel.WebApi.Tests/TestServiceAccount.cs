using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     A service account's JSON key as the Google Cloud console downloads one, with a private key made for the test
///     and thrown away with it: nothing that could sign for a real account is ever committed.
/// </summary>
internal static class TestServiceAccount
{
    public const string ProjectId = "passel-test";

    /// <summary>
    ///     The key's JSON. <paramref name="tokenUri" /> is where the credential asks for access tokens; Google's by
    ///     default, a fake of the tests' own in the integration tests.
    /// </summary>
    public static string Json(string tokenUri = "https://oauth2.googleapis.com/token")
    {
        using var rsa = RSA.Create(2048);

        return new JsonObject
        {
            ["type"] = "service_account",
            ["project_id"] = ProjectId,
            ["private_key_id"] = "0123456789abcdef0123456789abcdef01234567",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = $"push-sender@{ProjectId}.iam.gserviceaccount.com",
            ["client_id"] = "100000000000000000000",
            ["auth_uri"] = "https://accounts.google.com/o/oauth2/auth",
            ["token_uri"] = tokenUri,
        }.ToJsonString();
    }

    /// <summary>
    ///     Writes <paramref name="json" /> to a new file under the temporary directory and returns its path; the caller
    ///     deletes it.
    /// </summary>
    public static string WriteFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"passel-fcm-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        return path;
    }
}
