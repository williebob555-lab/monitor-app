using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApp.Model;
using Xunit;

namespace MonitorApp.Peer.LocalApi.Tests;

public sealed class LocalApiThingsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-local-api-things-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ANodeWithoutTheModelSaysSo()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        using var response = await fixture.Client.GetAsync(new Uri("api/v1/things", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddingAThingNeedsTheTokenAndShowsUpInTheList()
    {
        Directory.CreateDirectory(directory);
        await using var model = new ModelStore(Path.Combine(directory, "model.db"));
        await model.InitializeAsync();
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, model: model);

        using var noToken = new HttpRequestMessage(HttpMethod.Post, "api/v1/things") { Content = JsonContent.Create(new { text = "Focusrite 2i2" }) };
        using (var rejected = await fixture.Client.SendAsync(noToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }

        var csrf = await fixture.GetCsrfTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/things")
        {
            Content = JsonContent.Create(new { text = "Focusrite 2i2\nneeds a 12 V supply", kindId = "device.audio" }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using (var created = await fixture.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var empty = new HttpRequestMessage(HttpMethod.Post, "api/v1/things") { Content = JsonContent.Create(new { text = " " }) };
        empty.Headers.Add("X-CSRF-TOKEN", await fixture.GetCsrfTokenAsync());
        using (var refused = await fixture.Client.SendAsync(empty))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        var list = await fixture.Client.GetFromJsonAsync<ThingsResponse>("api/v1/things", Json);
        var thing = Assert.Single(list!.Things);
        Assert.Equal(("Focusrite 2i2", "Audio equipment"), (thing.Name, thing.KindLabel));
        Assert.Equal("Entered", Assert.Single(thing.Facts).Evidence);
        Assert.Contains(list.Kinds, k => k.Id == "device.audio");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
