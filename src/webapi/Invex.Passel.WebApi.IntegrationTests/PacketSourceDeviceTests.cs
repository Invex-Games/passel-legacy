using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Services;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The <c>deviceId</c> a packet is sent with: one of the caller's own devices or a validation problem, kept on the
///     stored packet as its source (through a link's preview too), and not on the wire.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class PacketSourceDeviceTests(PasselApiFixture fixture) : IAsyncLifetime
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private TestPageServer _pages = null!;

    public async ValueTask InitializeAsync() =>
        _pages = await TestPageServer.StartAsync(CancellationToken);

    public ValueTask DisposeAsync() =>
        _pages.DisposeAsync();

    private static JsonObject Text(string? deviceId) =>
        new()
        {
            ["$type"] = "Text",
            ["title"] = null,
            ["body"] = "hello",
            ["deviceId"] = deviceId,
        };

    private async Task<Packet> StoredAsync(string encodedId)
    {
        Assert.True(fixture.Api.Services.GetRequiredService<IIdEncodingService>().TryDecode(encodedId, out var id));

        await using var session = fixture.Api.Services.GetRequiredService<IDocumentStore>().QuerySession();

        return (await session.LoadAsync<Packet>(id, CancellationToken))!;
    }

    private Guid Decode(string encodedId)
    {
        Assert.True(fixture.Api.Services.GetRequiredService<IIdEncodingService>().TryDecode(encodedId, out var id));

        return id;
    }

    [Fact]
    public async Task A_packet_sent_from_one_of_the_callers_devices_keeps_it_as_its_source_and_does_not_show_it()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var device = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Pixel", "android", HttpStatusCode.Created, CancellationToken);

        using var content = new StringContent(Text(device.Id).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await alice.PostAsync(PacketApi.PacketsPath, content, CancellationToken);
        using var created = await PacketApi.ReadJsonAsync(response, HttpStatusCode.OK, CancellationToken);

        Assert.False(created.RootElement.TryGetProperty("deviceId", out _));
        Assert.False(created.RootElement.TryGetProperty("sourceDeviceId", out _));

        var packet = await StoredAsync(created.RootElement.GetProperty("id").GetString()!);

        Assert.Equal(Decode(device.Id), packet.SourceDeviceId);
    }

    [Fact]
    public async Task A_packet_sent_without_a_device_has_no_source()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        var created = await alice.CreateTextAsync(null, "hello", CancellationToken);

        Assert.Null((await StoredAsync(created.Id)).SourceDeviceId);
    }

    [Fact]
    public async Task A_links_source_survives_its_preview()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var device = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Chrome", "extension", HttpStatusCode.Created, CancellationToken);

        await using var stream = await EventStream.OpenAsync(alice, CancellationToken);
        await stream.ReadAsync(CancellationToken);

        var link = await alice.CreateAsync(new JsonObject
            {
                ["$type"] = "Link",
                ["name"] = null,
                ["url"] = $"http://{PasselApiFactory.TestPagesHost}:{_pages.Port}/article",
                ["deviceId"] = device.Id,
            },
            CancellationToken);

        Assert.Equal(EventStream.PacketCreatedEventType, (await stream.ReadAsync(CancellationToken)).EventType);
        Assert.Equal(EventStream.PacketUpdatedEventType, (await stream.ReadAsync(CancellationToken)).EventType);

        var stored = await StoredAsync(link.Id);

        Assert.NotNull(((PacketContent.Link)stored.Content).Preview);
        Assert.Equal(Decode(device.Id), stored.SourceDeviceId);
    }

    public static TheoryData<string> UnusableDeviceIds =>
    [
        "",
        "not-an-id-at-all!",
        new string('a', 65),
    ];

    [Theory]
    [MemberData(nameof(UnusableDeviceIds))]
    public async Task A_device_id_that_names_no_device_is_a_validation_problem(string deviceId)
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        await AssertRefusedAsync(alice, deviceId);
    }

    [Fact]
    public async Task Another_users_device_or_a_removed_one_is_refused_like_an_unknown_one_and_stores_nothing()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = fixture.Api.CreateClientFor(PasselApiFixture.User("bob"));

        var bobs = await bob.RegisterAsync(DeviceApi.NewInstallationId(), "Bob", "android", HttpStatusCode.Created, CancellationToken);
        var removed = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Old", "android", HttpStatusCode.Created, CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, await alice.RemoveDeviceAsync(removed.Id, CancellationToken));

        // A valid id of the right shape that was never a device
        var never = fixture.Api.Services.GetRequiredService<IIdEncodingService>().Encode(Guid.CreateVersion7());

        await AssertRefusedAsync(alice, bobs.Id);
        await AssertRefusedAsync(alice, removed.Id);
        await AssertRefusedAsync(alice, never);

        Assert.Empty((await alice.ListAsync(null, CancellationToken)).Packets);
    }

    private static async Task AssertRefusedAsync(HttpClient client, string deviceId)
    {
        using var content = new StringContent(Text(deviceId).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(PacketApi.PacketsPath, content, CancellationToken);
        using var problem = await PacketApi.ReadJsonAsync(response, HttpStatusCode.BadRequest, CancellationToken);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("DeviceId must be the id of one of your devices.",
            problem.RootElement.GetProperty("errors").GetProperty("deviceId")[0].GetString());
    }
}
