using Invex.Passel.WebApi.Options;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     The push settings the API starts with: neither FCM setting (push off), or both with a usable service account
///     key; anything in between stops it with a message that says what is wrong.
/// </summary>
public sealed class PushOptionsTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
            System.IO.File.Delete(file);
    }

    private string KeyFile(string json)
    {
        var path = TestServiceAccount.WriteFile(json);
        _files.Add(path);

        return path;
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? projectId, string? credentialFile) =>
        new PushOptionsValidator().Validate(null,
            new PushOptions
            {
                Fcm = new FcmOptions
                {
                    ProjectId = projectId,
                    CredentialFile = credentialFile,
                },
            });

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", null)]
    public void Neither_setting_leaves_push_off_and_passes(string? projectId, string? credentialFile)
    {
        Assert.True(Validate(projectId, credentialFile).Succeeded);
        Assert.False(new PushOptions { Fcm = new() { ProjectId = projectId, CredentialFile = credentialFile } }.Enabled);
    }

    [Fact]
    public void A_project_without_a_credential_fails_and_says_which_is_missing()
    {
        var result = Validate(TestServiceAccount.ProjectId, null);

        Assert.True(result.Failed);
        Assert.Contains("CredentialFile is not", result.FailureMessage);
    }

    [Fact]
    public void A_credential_without_a_project_fails_and_says_which_is_missing()
    {
        var result = Validate(" ", KeyFile(TestServiceAccount.Json()));

        Assert.True(result.Failed);
        Assert.Contains("ProjectId is not", result.FailureMessage);
    }

    [Fact]
    public void Both_with_a_service_account_key_pass_and_turn_push_on()
    {
        var path = KeyFile(TestServiceAccount.Json());

        Assert.True(Validate(TestServiceAccount.ProjectId, path).Succeeded);
        Assert.True(new PushOptions { Fcm = new() { ProjectId = TestServiceAccount.ProjectId, CredentialFile = path } }.Enabled);
    }

    [Fact]
    public void A_credential_file_that_is_not_there_fails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"passel-missing-{Guid.NewGuid():N}.json");
        var result = Validate(TestServiceAccount.ProjectId, path);

        Assert.True(result.Failed);
        Assert.Contains("does not exist", result.FailureMessage);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"type":"authorized_user","client_id":"a","client_secret":"b","refresh_token":"c"}""")]
    [InlineData("""{"type":"service_account","client_email":"a@b.iam.gserviceaccount.com","private_key":"not a key"}""")]
    public void A_credential_file_that_is_not_a_service_account_key_fails_without_echoing_it(string json)
    {
        var result = Validate(TestServiceAccount.ProjectId, KeyFile(json));

        Assert.True(result.Failed);
        Assert.Contains("is not a service account's JSON key", result.FailureMessage);
        Assert.DoesNotContain("client_secret", result.FailureMessage);
        Assert.DoesNotContain("refresh_token", result.FailureMessage);
    }

    [Theory]
    [InlineData("Passel")]
    [InlineData("-passel")]
    [InlineData("passel-test/../../other")]
    [InlineData("passel test")]
    [InlineData("passel-test-")]
    public void A_project_id_google_would_not_give_fails(string projectId)
    {
        var result = Validate(projectId, KeyFile(TestServiceAccount.Json()));

        Assert.True(result.Failed);
        Assert.Contains("is not a Google Cloud project id", result.FailureMessage);
    }

    [Theory]
    [InlineData("passel-12345")]
    [InlineData("123456789012")]
    [InlineData("example.com:passel")]
    public void A_project_id_number_or_domain_scoped_id_passes(string projectId) =>
        Assert.True(Validate(projectId, KeyFile(TestServiceAccount.Json())).Succeeded);
}
