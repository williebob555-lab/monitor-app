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

    [Fact]
    public async Task WhatsMissingIsListedAndANeedOnTheListIsNoLongerSuggested()
    {
        Directory.CreateDirectory(directory);
        await using var model = new ModelStore(Path.Combine(directory, "model.db"));
        await model.InitializeAsync();
        await model.AppendAsync(
        [
            new ObjectCreated("fuzz", "Fuzz", "device.audio", Lifecycle.Owned),
            new PortAdded("fuzz", new PortSpec("dc", "DC in", "dc-barrel-5521", PortGender.Socket, PortDirection.Input, ["dc-power"])),
            new PropertyAsserted("fuzz", "electrical.voltage", PropertyValue.Number(9, "V"), new Evidence(EvidenceLevel.Specified, "datasheet"), "dc"),
        ], "test");
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, model: model);

        var list = await fixture.Client.GetFromJsonAsync<ThingsResponse>("api/v1/things", Json);
        var need = Assert.Single(list!.Missing!);
        Assert.Equal(("power-missing", "Power for Fuzz", "Unknown"), (need.Code, need.Need, need.Certainty));
        Assert.Equal(["fuzz"], need.Things);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/things")
        {
            Content = JsonContent.Create(new { text = "Power for Fuzz\n" + need.Explanation, kindId = "requirement" }),
        };
        request.Headers.Add("X-CSRF-TOKEN", await fixture.GetCsrfTokenAsync());
        using (var created = await fixture.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        list = await fixture.Client.GetFromJsonAsync<ThingsResponse>("api/v1/things", Json);
        Assert.Empty(list!.Missing!);
        Assert.Equal("Idea", list.Things.Single(t => t.Name == "Power for Fuzz").Lifecycle);
    }

    [Fact]
    public async Task EditingAThingNeedsTheTokenAndPutsItInsideAnother()
    {
        Directory.CreateDirectory(directory);
        await using var model = new ModelStore(Path.Combine(directory, "model.db"));
        await model.InitializeAsync();
        var rack = await Things.CaptureAsync(model, "Studio rack");
        var amp = await Things.CaptureAsync(model, "Focusrite", "device.audio");
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, model: model);

        using var noToken = new HttpRequestMessage(HttpMethod.Post, "api/v1/things/edit") { Content = JsonContent.Create(new { id = amp.Id, name = "Hijacked" }) };
        using (var rejected = await fixture.Client.SendAsync(noToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/things/edit")
        {
            Content = JsonContent.Create(new { id = amp.Id, name = "Focusrite 2i2", insideId = rack.Id }),
        };
        request.Headers.Add("X-CSRF-TOKEN", await fixture.GetCsrfTokenAsync());
        using (var saved = await fixture.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using var loop = new HttpRequestMessage(HttpMethod.Post, "api/v1/things/edit") { Content = JsonContent.Create(new { id = rack.Id, insideId = amp.Id }) };
        loop.Headers.Add("X-CSRF-TOKEN", await fixture.GetCsrfTokenAsync());
        using (var refused = await fixture.Client.SendAsync(loop))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        var list = await fixture.Client.GetFromJsonAsync<ThingsResponse>("api/v1/things", Json);
        var edited = list!.Things.Single(t => t.Id == amp.Id);
        Assert.Equal(("Focusrite 2i2", rack.Id), (edited.Name, edited.InsideId));
        Assert.Null(list.Things.Single(t => t.Id == rack.Id).InsideId);
    }

    [Fact]
    public async Task ThingsCanBeReadAsTheyWereEarlier()
    {
        Directory.CreateDirectory(directory);
        await using var model = new ModelStore(Path.Combine(directory, "model.db"));
        await model.InitializeAsync();
        var rack = await Things.CaptureAsync(model, "Studio rack");
        await Task.Delay(30);
        var before = DateTimeOffset.UtcNow;
        await Task.Delay(30);
        var amp = await Things.CaptureAsync(model, "Focusrite");
        await Things.EditAsync(model, amp.Id, new ThingEdit(InsideId: rack.Id));
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, model: model);

        var then = await fixture.Client.GetFromJsonAsync<ThingsResponse>("api/v1/things?asOf=" + Uri.EscapeDataString(before.ToString("O")), Json);
        Assert.Equal(["Studio rack"], then!.Things.Select(t => t.Name));
        Assert.Empty(then.Things.Single().PartIds);
        var now = await fixture.Client.GetFromJsonAsync<ThingsResponse>("api/v1/things", Json);
        Assert.Equal([amp.Id], now!.Things.Single(t => t.Id == rack.Id).PartIds);

        using var bad = await fixture.Client.GetAsync(new Uri("api/v1/things?asOf=yesterday-ish", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
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
