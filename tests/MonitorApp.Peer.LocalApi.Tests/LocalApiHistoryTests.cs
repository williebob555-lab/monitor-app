using System.Net;
using System.Net.Http.Json;
using Google.Protobuf;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.LocalApi.Tests;

public sealed class LocalApiHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Base = LocalApiHostTests.Now.AddDays(-2);

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "monitor-local-api-history-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HistoryIsOrderedByObservedTimeAndDecodesValuesUnitsAndStates()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        await Append(fixture, 3, Base, MetricBytes(10.5));
        await Append(fixture, 1, Base.AddMinutes(2), MetricBytes(30));
        await Append(fixture, 2, Base.AddMinutes(1), MetricBytes(20));
        await Append(fixture, 4, Base.AddMinutes(3), UnavailableBytes(), quality: "Unavailable");
        await Append(fixture, 5, Base.AddMinutes(4), StatusBytes(CapabilityState.PermissionDenied), quality: "PermissionDenied",
            signal: "collector.health.x");

        var response = await History(fixture, Base, Base.AddHours(1), "&subjectEntityId=host-a");

        Assert.False(response.Truncated);
        Assert.Equal("raw", response.Resolution);
        Assert.Null(response.NextCursor);
        Assert.Equal([3UL, 2UL, 1UL, 4UL, 5UL], response.Points.Select(point => point.Sequence));
        var first = response.Points[0];
        Assert.Equal("number", first.Value!.Kind);
        Assert.Equal(10.5, first.Value.Number);
        Assert.Equal("Bytes", first.Unit);
        Assert.Equal("Fresh", first.Freshness);
        Assert.Equal("peer-remote", first.OriginPeerId);
        Assert.Equal("origin-remote", first.OriginStreamId);
        Assert.Equal("adapter-one", first.Source);
        Assert.Equal("Replicated", first.Provenance);
        var unavailable = response.Points[3];
        Assert.Null(unavailable.Value);
        Assert.Equal("Unavailable", unavailable.Freshness);
        Assert.Equal("Unavailable", unavailable.Quality);
        var denied = response.Points[4];
        Assert.Null(denied.Value);
        Assert.Equal("Unavailable", denied.Freshness);
        Assert.Equal("PermissionDenied", denied.Quality);
        Assert.Equal("detail", denied.Detail);
    }

    [Fact]
    public async Task UnavailableObservationNeverSerializesAsNumericZeroEvenWhenPayloadCarriesOne()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        // A buggy collector might attach a zero value to an unavailable sample; the API must still report a state.
        var envelope = new TelemetryEnvelope
        {
            Metric = new MetricSample
            {
                MetricId = "cpu.load",
                Unit = MetricUnit.Ratio,
                Quality = ObservationQuality.Unavailable,
                Value = new MetricValue { DoubleValue = 0 },
            },
        };
        await Append(fixture, 1, Base, envelope.ToByteArray(), quality: "Unavailable");

        var raw = await fixture.Client.GetStringAsync(Query("api/v1/history", Base, Base.AddHours(1), "&subjectEntityId=host-a"));
        var response = await History(fixture, Base, Base.AddHours(1), "&subjectEntityId=host-a");

        var point = Assert.Single(response.Points);
        Assert.Null(point.Value);
        Assert.Equal("Unavailable", point.Freshness);
        Assert.DoesNotContain("\"number\":0", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowIsHalfOpenAndFiltersApply()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        await Append(fixture, 1, Base, MetricBytes(1));
        await Append(fixture, 2, Base.AddMinutes(10), MetricBytes(2));
        await Append(fixture, 3, Base.AddMinutes(5), MetricBytes(3), subject: "host-b");
        await Append(fixture, 4, Base.AddMinutes(6), MetricBytes(4), signal: "memory.used");

        var window = await History(fixture, Base, Base.AddMinutes(10), string.Empty);
        var subject = await History(fixture, Base, Base.AddHours(1), "&subjectEntityId=host-b");
        var signal = await History(fixture, Base, Base.AddHours(1), "&signalId=memory.used");
        var origin = await History(fixture, Base, Base.AddHours(1), "&originPeerId=nobody");

        Assert.Equal([1UL, 3UL, 4UL], window.Points.Select(point => point.Sequence));
        Assert.Equal([3UL], subject.Points.Select(point => point.Sequence));
        Assert.Equal([4UL], signal.Points.Select(point => point.Sequence));
        Assert.Empty(origin.Points);
    }

    [Fact]
    public async Task TruncationReportsFlagAndCursorThatPagesWithoutLossOrDuplicates()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        // Equal observed times across two origins exercise the keyset tie-break.
        await Append(fixture, 1, Base, MetricBytes(1));
        await Append(fixture, 2, Base, MetricBytes(2));
        await Append(fixture, 1, Base, MetricBytes(3), origin: "peer-other", stream: "origin-other");
        await Append(fixture, 3, Base.AddSeconds(1), MetricBytes(4));
        await Append(fixture, 4, Base.AddSeconds(2), MetricBytes(5));

        var seen = new List<(string Origin, ulong Sequence)>();
        string? cursor = null;
        var pages = 0;
        HistoryResponse page;
        do
        {
            page = await History(fixture, Base, Base.AddHours(1), "&limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            seen.AddRange(page.Points.Select(point => (point.OriginPeerId, point.Sequence)));
            Assert.Equal(page.Truncated, page.NextCursor is not null);
            cursor = page.NextCursor;
            pages++;
        }
        while (page.Truncated && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(
            [("peer-other", 1UL), ("peer-remote", 1UL), ("peer-remote", 2UL), ("peer-remote", 3UL), ("peer-remote", 4UL)],
            seen);
    }

    [Fact]
    public async Task ExactLimitIsNotReportedAsTruncated()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        await Append(fixture, 1, Base, MetricBytes(1));
        await Append(fixture, 2, Base.AddSeconds(1), MetricBytes(2));

        var page = await History(fixture, Base, Base.AddHours(1), "&limit=2");

        Assert.Equal(2, page.Count);
        Assert.False(page.Truncated);
        Assert.Null(page.NextCursor);
    }

    [Theory]
    [InlineData("api/v1/history", "from is required")]
    [InlineData("api/v1/history?from=2026-11-01T00:00:00Z", "to is required")]
    [InlineData("api/v1/history?from=nonsense&to=2026-11-02T00:00:00Z", "not a valid")]
    [InlineData("api/v1/history?from=2026-11-02T00:00:00Z&to=2026-11-02T00:00:00Z", "later than from")]
    [InlineData("api/v1/history?from=2026-11-03T00:00:00Z&to=2026-11-02T00:00:00Z", "later than from")]
    [InlineData("api/v1/history?from=2026-10-01T00:00:00Z&to=2026-11-02T00:00:00Z", "31 days")]
    [InlineData("api/v1/history?from=2026-11-01T00:00:00Z&to=2026-11-02T00:00:00Z&limit=0", "limit")]
    [InlineData("api/v1/history?from=2026-11-01T00:00:00Z&to=2026-11-02T00:00:00Z&limit=5001", "limit")]
    [InlineData("api/v1/history?from=2026-11-01T00:00:00Z&to=2026-11-02T00:00:00Z&cursor=not-a-cursor", "cursor")]
    [InlineData("api/v1/state?asOf=garbage", "asOf")]
    [InlineData("api/v1/state?lookbackSeconds=0", "lookbackSeconds")]
    [InlineData("api/v1/state?lookbackSeconds=99999999", "lookbackSeconds")]
    [InlineData("api/v1/state?limit=5001", "limit")]
    public async Task InvalidBoundsAreRejectedWithExplicitErrors(string url, string expected)
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);

        var response = await fixture.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(expected, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThirtyOneDayWindowIsAcceptedAtTheBoundary()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);

        var response = await History(fixture, Base, Base.AddDays(31), string.Empty);

        Assert.Equal(Base.AddDays(31), response.To);
    }

    [Fact]
    public async Task AsOfReturnsLatestRecordAtOrBeforeTimeWithAgeAndFreshness()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        var t = Base.AddHours(1);
        await Append(fixture, 1, t.AddMinutes(-30), MetricBytes(1));
        await Append(fixture, 2, t.AddMinutes(-2), MetricBytes(2));
        await Append(fixture, 3, t, MetricBytes(3));
        await Append(fixture, 4, t.AddMinutes(5), MetricBytes(4));
        await Append(fixture, 5, t.AddMinutes(-20), MetricBytes(9), subject: "host-b");

        var response = await State(fixture, t, "&lookbackSeconds=3600");

        var hostA = Assert.Single(response.Points, point => point.SubjectEntityId == "host-a");
        Assert.Equal(3UL, hostA.Sequence);
        Assert.Equal(3, hostA.Value!.Number);
        Assert.Equal(0, hostA.AgeSeconds);
        Assert.Equal("Fresh", hostA.Freshness);
        var hostB = Assert.Single(response.Points, point => point.SubjectEntityId == "host-b");
        Assert.Equal(1200, hostB.AgeSeconds);
        Assert.Equal("Stale", hostB.Freshness);
        Assert.Equal(9, hostB.Value!.Number);
        Assert.Equal(t, response.AsOf);
        Assert.False(response.Truncated);

        var earlier = await State(fixture, t.AddMinutes(-1), "&lookbackSeconds=3600&subjectEntityId=host-a");
        var point = Assert.Single(earlier.Points);
        Assert.Equal(2UL, point.Sequence);
        Assert.Equal(60, point.AgeSeconds);
    }

    [Fact]
    public async Task AsOfHonoursLookbackAndKeepsLatestUnavailableStateInsteadOfOlderValue()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        var t = Base.AddHours(1);
        await Append(fixture, 1, t.AddMinutes(-10), MetricBytes(42));
        await Append(fixture, 2, t.AddMinutes(-1), UnavailableBytes(), quality: "Unavailable");
        await Append(fixture, 3, t.AddHours(-3), MetricBytes(7), signal: "memory.used");

        var response = await State(fixture, t, "&lookbackSeconds=3600");

        var cpu = Assert.Single(response.Points, point => point.SignalId == "cpu.load");
        Assert.Equal(2UL, cpu.Sequence);
        Assert.Null(cpu.Value);
        Assert.Equal("Unavailable", cpu.Freshness);
        Assert.Equal(60, cpu.AgeSeconds);
        Assert.DoesNotContain(response.Points, point => point.SignalId == "memory.used");
    }

    [Fact]
    public async Task StateWithoutAsOfUsesServerClock()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        await Append(fixture, 1, LocalApiHostTests.Now.AddMinutes(-1), MetricBytes(5));

        var response = await fixture.Client.GetFromJsonAsync<StateAsOfResponse>(
            "api/v1/state?subjectEntityId=host-a", LocalApiHostTests.JsonOptions);

        Assert.Equal(LocalApiHostTests.Now, response!.AsOf);
        Assert.Equal(60, Assert.Single(response.Points).AgeSeconds);
    }

    [Fact]
    public async Task CatalogListsSubjectsSignalsUnitsAndOrigins()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        await Append(fixture, 1, Base, MetricBytes(1));
        await Append(fixture, 2, Base, StatusBytes(CapabilityState.Available), signal: "collector.health.x");
        await fixture.PeerStore.UpsertCapabilityAsync(new CapabilityCatalogEntry(
            "system.cpu", "system", "1", [1], Base));

        var catalog = await fixture.Client.GetFromJsonAsync<CatalogResponse>("api/v1/catalog", LocalApiHostTests.JsonOptions);

        var host = Assert.Single(catalog!.Subjects, subject => subject.SubjectEntityId == "host-a");
        var load = Assert.Single(host.Signals, signal => signal.SignalId == "cpu.load");
        Assert.Equal("Bytes", load.Unit);
        Assert.Equal("number", load.Kind);
        Assert.Equal("status", Assert.Single(host.Signals, signal => signal.SignalId == "collector.health.x").Kind);
        Assert.Contains(catalog.Subjects, subject => subject.SubjectEntityId == "entity-one");
        Assert.Contains("peer-remote", catalog.OriginPeers);
        Assert.Contains(catalog.Capabilities, capability => capability.CapabilityId == "system.cpu");
    }

    [Fact]
    public async Task UndecodableStoredBytesAreReportedAsAStateWithoutFailingTheQuery()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);

        var response = await History(
            fixture, LocalApiHostTests.Now.AddHours(-1), LocalApiHostTests.Now, "&subjectEntityId=entity-one");

        var point = Assert.Single(response.Points);
        Assert.Null(point.Value);
        Assert.Contains("not a telemetry envelope", point.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageIncludesClientSectionsUnderNonceBoundSecurityHeadersWithoutExternalResources()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);

        var response = await fixture.Client.GetAsync("/");
        var page = await response.Content.ReadAsStringAsync();

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        var marker = "nonce-";
        var start = csp.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var nonce = csp[start..csp.IndexOf('\'', start)];
        Assert.Contains($"<script nonce=\"{nonce}\">", page, StringComparison.Ordinal);
        Assert.Contains($"<style nonce=\"{nonce}\">", page, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        foreach (var heading in new[] { "Current state", "History", "State as of a time" })
        {
            Assert.Contains(heading, page, StringComparison.Ordinal);
        }

        Assert.Contains("<label for=\"history-subject\">", page, StringComparison.Ordinal);
        Assert.Contains("<label for=\"asof-time\">", page, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\">Freshness</th>", page, StringComparison.Ordinal);
        Assert.Contains("<option value=\"1h\">", page, StringComparison.Ordinal);
        Assert.Contains("<option value=\"7d\">", page, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"http", page, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"http", page, StringComparison.Ordinal);
    }

    private static string Query(string path, DateTimeOffset from, DateTimeOffset to, string extra) =>
        $"{path}?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}{extra}";

    private static async Task<HistoryResponse> History(
        LocalApiHostTests.Fixture fixture, DateTimeOffset from, DateTimeOffset to, string extra) =>
        (await fixture.Client.GetFromJsonAsync<HistoryResponse>(
            Query("api/v1/history", from, to, extra), LocalApiHostTests.JsonOptions))!;

    private static async Task<StateAsOfResponse> State(
        LocalApiHostTests.Fixture fixture, DateTimeOffset asOf, string extra) =>
        (await fixture.Client.GetFromJsonAsync<StateAsOfResponse>(
            $"api/v1/state?asOf={Uri.EscapeDataString(asOf.ToString("O"))}{extra}", LocalApiHostTests.JsonOptions))!;

    private static async Task Append(
        LocalApiHostTests.Fixture fixture,
        ulong sequence,
        DateTimeOffset observedAt,
        byte[] bytes,
        string quality = "Valid",
        string subject = "host-a",
        string signal = "cpu.load",
        string origin = "peer-remote",
        string stream = "origin-remote")
    {
        await fixture.PeerStore.AppendOriginRecordAsync(new OriginRecordWrite(
            origin,
            stream,
            sequence,
            subject,
            "adapter-one",
            signal,
            bytes,
            Enumerable.Repeat((byte)0x2a, 64).ToArray(),
            observedAt,
            observedAt.AddMilliseconds(10),
            origin,
            RecordProvenance.Replicated,
            quality));
    }

    private static byte[] MetricBytes(double value) => new TelemetryEnvelope
    {
        Metric = new MetricSample
        {
            MetricId = "cpu.load",
            Unit = MetricUnit.Bytes,
            Quality = ObservationQuality.Valid,
            Value = new MetricValue { DoubleValue = value },
        },
    }.ToByteArray();

    private static byte[] UnavailableBytes() => new TelemetryEnvelope
    {
        Metric = new MetricSample
        {
            MetricId = "cpu.load",
            Unit = MetricUnit.Ratio,
            Quality = ObservationQuality.Unavailable,
        },
    }.ToByteArray();

    private static byte[] StatusBytes(CapabilityState state) => new TelemetryEnvelope
    {
        CollectorStatus = new CollectorStatus { CapabilityId = "x", State = state, DetailCode = "detail" },
    }.ToByteArray();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
