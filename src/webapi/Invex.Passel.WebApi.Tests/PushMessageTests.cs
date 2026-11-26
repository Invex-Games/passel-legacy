using System.Text.Json.Nodes;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     What a notification says and how it goes to FCM: a link's host and nothing else of it, a note or a file by its
///     kind alone, the device it came from when there is one, and the Android settings ADR 0005 chose.
/// </summary>
public sealed class PushMessageTests
{
    private const string EncodedId = "qrjMmYBVnK8m3coMo0hiMeN";

    private static Packet PacketOf(PacketContent content) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = "alice",
            CreatedAt = DateTimeOffset.UtcNow,
            Content = content,
        };

    private static readonly PacketContent Link = new PacketContent.Link
    {
        Name = "Private name",
        Url = "https://www.example.com/secret/path?q=private#fragment",
    };

    private static readonly PacketContent Text = new PacketContent.Text
    {
        Title = "Private title",
        Body = "Private body",
    };

    private static readonly PacketContent File = new PacketContent.File
    {
        FileName = "private.pdf",
        ContentType = "application/pdf",
        ParcelId = Guid.NewGuid(),
        IsImage = false,
    };

    [Fact]
    public void A_link_names_its_source_device_and_shows_only_its_host()
    {
        var message = PushMessage.For(PacketOf(Link), "Chrome", EncodedId);

        Assert.Equal("Link from Chrome", message.Title);
        Assert.Equal("www.example.com", message.Body);
        Assert.Equal(EncodedId, message.PacketId);
        Assert.Equal("Link", message.Type);
    }

    [Fact]
    public void A_link_without_a_source_device_is_a_new_link()
    {
        var message = PushMessage.For(PacketOf(Link), null, EncodedId);

        Assert.Equal("New link", message.Title);
        Assert.Equal("www.example.com", message.Body);
    }

    [Theory]
    [InlineData("Pixel", "Note from Pixel")]
    [InlineData(null, "New note")]
    [InlineData("  ", "New note")]
    public void A_note_says_only_that_it_is_a_note(string? source, string title)
    {
        var message = PushMessage.For(PacketOf(Text), source, EncodedId);

        Assert.Equal(title, message.Title);
        Assert.Null(message.Body);
        Assert.Equal("Text", message.Type);
    }

    [Theory]
    [InlineData("Laptop", "File from Laptop")]
    [InlineData(null, "New file")]
    public void A_file_says_only_that_it_is_a_file(string? source, string title)
    {
        var message = PushMessage.For(PacketOf(File), source, EncodedId);

        Assert.Equal(title, message.Title);
        Assert.Null(message.Body);
        Assert.Equal("File", message.Type);
    }

    [Fact]
    public void The_request_body_is_a_notification_message_with_data_for_one_token()
    {
        var message = PushMessage.For(PacketOf(Link), "Chrome", EncodedId);

        var body = JsonNode.Parse(FcmSender.RequestBody("device-token", message))!;

        var expected = JsonNode.Parse($$"""
            {
              "message": {
                "token": "device-token",
                "notification": { "title": "Link from Chrome", "body": "www.example.com" },
                "data": { "packetId": "{{EncodedId}}", "type": "Link" },
                "android": {
                  "priority": "HIGH",
                  "ttl": "86400s",
                  "notification": { "channel_id": "packets", "tag": "{{EncodedId}}" }
                }
              }
            }
            """);

        Assert.True(JsonNode.DeepEquals(expected, body), body.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public void No_content_the_user_wrote_goes_to_FCM(PacketContent content)
    {
        var body = FcmSender.RequestBody("device-token", PushMessage.For(PacketOf(content), "Chrome", EncodedId));

        foreach (var secret in new[] { "Private", "private", "secret", "q=", "fragment" })
            Assert.DoesNotContain(secret, body);
    }

    [Fact]
    public void A_note_has_no_body_in_its_notification()
    {
        var body = JsonNode.Parse(FcmSender.RequestBody("t", PushMessage.For(PacketOf(Text), null, EncodedId)))!;

        Assert.Equal(["title"], body["message"]!["notification"]!.AsObject().Select(property => property.Key));
    }

    public static TheoryData<PacketContent> Contents =>
        [Link, Text, File];
}
