using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Transport;
using Xunit;
using ZXing;
using ZXing.Common;

namespace MonitorApp.Peer.LocalApi.Tests;

public sealed partial class LocalApiPhonePairingTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "monitor-local-api-phone-tests", Guid.NewGuid().ToString("N"));

    private static readonly IPAddress Tailnet = IPAddress.Parse("100.64.0.5");

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task IssuedQrDecodesToTheExactInvitationAndTheTypedCodeIsRedeemableOnce()
    {
        var time = new ManualTime();
        var codes = new PairingShortCodeRegistry(time);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(
            directory, phonePairing: new PhonePairingOptions([Tailnet], 47000, codes, Time: time));

        var page = await IssueAsync(fixture);

        var svg = SvgPattern().Match(page).Value;
        var payload = WebUtility.HtmlDecode(PayloadPattern().Match(page).Groups[1].Value);
        Assert.Equal(payload, DecodeQr(svg));
        var invitation = PeerPairingInvitation.Decode(payload);
        Assert.Equal(Tailnet, invitation.Address);
        Assert.Equal(47000, invitation.Port);
        Assert.Equal("peer-local", invitation.PeerId);
        Assert.Equal(fixture.LocalIdentity.PublicKeyFingerprint, invitation.FingerprintSha256);
        Assert.Equal(time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds(), invitation.ExpiresAt!.Value.ToUnixTimeSeconds());
        Assert.Contains("100.64.0.5:47000", page, StringComparison.Ordinal);

        var typed = CodePattern().Match(page).Groups[1].Value;
        var keys = PairingShortCode.Derive(PairingShortCode.Normalize(typed)!);
        var redemption = codes.Redeem(keys.LookupId);
        Assert.Equal("peer-local", redemption!.PeerId);
        Assert.Equal(fixture.LocalIdentity.PublicKeyFingerprint, redemption.FingerprintSha256);
        Assert.Null(codes.Redeem(keys.LookupId));
    }

    [Fact]
    public async Task QrHasAQuietZoneAndTheSectionIsPlainSvgWithoutScriptsOrInlineStyles()
    {
        var codes = new PairingShortCodeRegistry();
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(
            directory, phonePairing: new PhonePairingOptions([Tailnet], 47000, codes));

        var page = await IssueAsync(fixture);
        var response = await fixture.Client.GetAsync("/");
        var csp = string.Join(';', response.Headers.GetValues("Content-Security-Policy"));
        var nonce = Regex.Match(csp, "script-src 'nonce-([^']+)'").Groups[1].Value;

        var svg = SvgPattern().Match(page).Value;
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", svg.Replace("http://www.w3.org/2000/svg", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        var body = await response.Content.ReadAsStringAsync();
        foreach (Match tag in Regex.Matches(body, "<(script|style)\\b[^>]*>"))
        {
            Assert.Contains($"nonce=\"{nonce}\"", tag.Value, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(@"\son[a-z]+\s*=", body);
        Assert.DoesNotContain("style=\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        var grid = ModuleGrid(svg);
        Assert.All(grid[..4], row => Assert.DoesNotContain(true, row));
        Assert.All(grid, row => Assert.DoesNotContain(true, row[..4]));
    }

    [Fact]
    public async Task ExpiredInvitationDisappearsFromThePageAndItsCodeNoLongerRedeems()
    {
        var time = new ManualTime();
        var codes = new PairingShortCodeRegistry(time);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(
            directory, phonePairing: new PhonePairingOptions([Tailnet], 47000, codes, Time: time));
        var page = await IssueAsync(fixture);
        var keys = PairingShortCode.Derive(PairingShortCode.Normalize(CodePattern().Match(page).Groups[1].Value)!);

        time.Advance(TimeSpan.FromMinutes(10));

        var after = await fixture.Client.GetStringAsync("/");
        Assert.DoesNotContain("<svg", after, StringComparison.Ordinal);
        Assert.DoesNotContain("phonePayload", after, StringComparison.Ordinal);
        Assert.Contains("Show a new pairing code", after, StringComparison.Ordinal);
        Assert.Null(codes.Redeem(keys.LookupId));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    public async Task LoopbackOrWildcardListenerShowsAClearMessageAndOffersNoCode(string bound)
    {
        var codes = new PairingShortCodeRegistry();
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(
            directory, phonePairing: new PhonePairingOptions([IPAddress.Parse(bound)], 47000, codes));

        var page = await fixture.Client.GetStringAsync("/");

        Assert.Contains("cannot be reached by a phone", page, StringComparison.Ordinal);
        Assert.Contains("Tailscale", page, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"phoneAddress\"", page, StringComparison.Ordinal);
        var response = await PostAsync(fixture, bound);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddressMustBeOneTheNodeConfiguredAndMutationNeedsCsrf()
    {
        var codes = new PairingShortCodeRegistry();
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(
            directory, phonePairing: new PhonePairingOptions([Tailnet], 47000, codes));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(fixture, "100.64.0.99")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(fixture, "8.8.8.8")).StatusCode);
        var forbidden = await fixture.Client.PostAsync("api/phone-invitations",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["address"] = "100.64.0.5" }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.DoesNotContain("<svg", await fixture.Client.GetStringAsync("/"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingPhoneRequestShowsItsDisplayNameAndFingerprintForApproval()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);

        var page = await fixture.Client.GetStringAsync("/");
        var pairings = await fixture.Client.GetFromJsonAsync<PendingPairingView[]>("api/pairings");

        var pending = Assert.Single(pairings!, item => item.PairingId == fixture.PendingPairingId);
        Assert.Equal("Pending laptop", pending.DisplayName);
        Assert.Matches("^[0-9A-F]{64}$", pending.FingerprintSha256);
        Assert.Contains("Pending laptop", page, StringComparison.Ordinal);
        Assert.Contains($"<code>{pending.FingerprintSha256}</code>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnconfiguredNodeSaysPhonePairingIsUnavailable()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);

        Assert.Contains("Phone pairing is not configured", await fixture.Client.GetStringAsync("/"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(fixture, "100.64.0.5")).StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(LocalApiHostTests.Fixture fixture, string address)
    {
        var csrf = await fixture.GetCsrfTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/phone-invitations")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["address"] = address,
                ["__RequestVerificationToken"] = csrf,
            }),
        };
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<string> IssueAsync(LocalApiHostTests.Fixture fixture)
    {
        var response = await PostAsync(fixture, "100.64.0.5");
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect, response.StatusCode.ToString());
        return await fixture.Client.GetStringAsync("/");
    }

    private static List<bool[]> ModuleGrid(string svg)
    {
        var size = int.Parse(ViewBoxPattern().Match(svg).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var grid = Enumerable.Range(0, size).Select(_ => new bool[size]).ToList();
        foreach (Match run in RunPattern().Matches(PathPattern().Match(svg).Groups[1].Value))
        {
            var x = int.Parse(run.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var y = int.Parse(run.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var width = int.Parse(run.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            for (var index = 0; index < width; index++)
            {
                grid[y][x + index] = true;
            }
        }

        return grid;
    }

    private static string DecodeQr(string svg)
    {
        var grid = ModuleGrid(svg);
        const int scale = 6;
        var pixels = grid.Count * scale;
        var gray = new byte[pixels * pixels];
        for (var y = 0; y < pixels; y++)
        {
            for (var x = 0; x < pixels; x++)
            {
                gray[(y * pixels) + x] = grid[y / scale][x / scale] ? (byte)0 : (byte)255;
            }
        }

        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = false },
        };
        var result = reader.Decode(new RGBLuminanceSource(gray, pixels, pixels, RGBLuminanceSource.BitmapFormat.Gray8));
        Assert.NotNull(result);
        return result.Text;
    }

    [GeneratedRegex("<svg.*?</svg>", RegexOptions.Singleline)]
    private static partial Regex SvgPattern();

    [GeneratedRegex("<textarea[^>]*id=\"phonePayload\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline)]
    private static partial Regex PayloadPattern();

    [GeneratedRegex("code <strong><code>([0-9A-Z]{5}-[0-9A-Z]{5})</code>")]
    private static partial Regex CodePattern();

    [GeneratedRegex("viewBox=\"0 0 (\\d+) \\d+\"")]
    private static partial Regex ViewBoxPattern();

    [GeneratedRegex("<path fill=\"#000\" d=\"([^\"]*)\"")]
    private static partial Regex PathPattern();

    [GeneratedRegex("M(\\d+) (\\d+)h(\\d+)v1h-\\d+z")]
    private static partial Regex RunPattern();

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
