using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Transport;
using QRCoder;

namespace MonitorApp.Peer.LocalApi;

/// <summary>
/// "Add a phone" configuration. <paramref name="AdvertiseAddresses"/> are the node's own configured peer listener
/// addresses (never discovered or typed on the page); only private, non-loopback ones can be advertised to a phone.
/// <paramref name="Codes"/> must be the same registry the peer gRPC host redeems from.
/// </summary>
public sealed record PhonePairingOptions(
    IReadOnlyList<IPAddress> AdvertiseAddresses,
    int PeerPort,
    PairingShortCodeRegistry Codes,
    TimeSpan? Lifetime = null,
    TimeProvider? Time = null)
{
    public TimeSpan EffectiveLifetime => Lifetime ?? TimeSpan.FromMinutes(10);
}

internal sealed record PhoneInvitationIssue(
    IPAddress Address, int Port, string Payload, string ShortCode, string QrSvg, DateTimeOffset ExpiresAt);

internal sealed class PhonePairingService(PhonePairingOptions? options, PeerStore store)
{
    private readonly object gate = new();
    private PhoneInvitationIssue? last;

    public bool IsConfigured => options is not null;

    public IReadOnlyList<IPAddress> Advertisable =>
        options?.AdvertiseAddresses.Where(IsAdvertisable).ToArray() ?? [];

    public async Task<bool> IssueAsync(string? addressText, CancellationToken cancellationToken)
    {
        if (options is null || !IPAddress.TryParse(addressText, out var requested))
        {
            return false;
        }

        var address = Advertisable.FirstOrDefault(candidate => candidate.Equals(requested));
        if (address is null)
        {
            return false;
        }

        var identity = await store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return false;
        }

        var issue = options.Codes.Issue(identity.PeerId, identity.PublicKeyFingerprint, options.EffectiveLifetime);
        var expires = DateTimeOffset.FromUnixTimeSeconds(issue.ExpiresAt.ToUnixTimeSeconds());
        var payload = new PeerPairingInvitation(
            address, options.PeerPort, identity.PeerId, identity.PublicKeyFingerprint, expires).Encode();
        var result = new PhoneInvitationIssue(address, options.PeerPort, payload, issue.Code, QrSvg.Render(payload), expires);
        lock (gate)
        {
            last = result;
        }

        return true;
    }

    public string RenderSection(string csrf, HtmlEncoder html)
    {
        var builder = new StringBuilder("<section aria-labelledby=\"phone\"><h2 id=\"phone\">Add a phone</h2>");
        if (options is null)
        {
            builder.Append("<p>Phone pairing is not configured on this node.</p></section>");
            return builder.ToString();
        }

        var addresses = Advertisable;
        if (addresses.Count == 0)
        {
            builder.Append("<p role=\"alert\"><strong>This peer cannot be reached by a phone yet.</strong> Its listener is bound to ")
                .Append(html.Encode(string.Join(", ", options.AdvertiseAddresses)))
                .Append(", which a phone cannot use. Tell Monitor App to listen on this computer's Tailscale (or other private) address, then restart it and reload this page. ")
                .Append("Windows, in an administrator PowerShell: <code>Set-ItemProperty HKLM:\\SYSTEM\\CurrentControlSet\\Services\\MonitorApp -Name Environment -Type MultiString -Value \"MONITOR_APP_PEER_BIND=$(tailscale ip -4)\"; New-NetFirewallRule -DisplayName 'Monitor App peer (Tailscale)' -Direction Inbound -Protocol TCP -LocalPort ")
                .Append(options.PeerPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(" -RemoteAddress 100.64.0.0/10 -Action Allow; Restart-Service MonitorApp</code>. ")
                .Append("Fedora or Ubuntu: set <code>MONITOR_APP_PEER_BIND=</code> followed by the output of <code>tailscale ip -4</code> in <code>/etc/monitor-app/node.conf</code>, then <code>sudo systemctl restart monitor-app</code>. ")
                .Append("Monitor App never binds a public or wildcard address for you.</p></section>");
            return builder.ToString();
        }

        builder.Append("<p>Pair a phone running Monitor App. The code below only lets that phone find and verify this computer; you still approve the phone under Pending pairings after checking its name and fingerprint.</p>")
            .Append("<form method=\"post\" action=\"/api/phone-invitations\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"")
            .Append(html.Encode(csrf)).Append("\"><p><label for=\"phoneAddress\">Address the phone should connect to</label><br><select id=\"phoneAddress\" name=\"address\">");
        foreach (var address in addresses)
        {
            builder.Append("<option>").Append(html.Encode(address.ToString())).Append("</option>");
        }

        builder.Append("</select></p><button type=\"submit\">Show a new pairing code</button></form>");
        PhoneInvitationIssue? current;
        lock (gate)
        {
            current = last;
        }

        if (current is not null && (options.Time ?? TimeProvider.System).GetUtcNow() < current.ExpiresAt)
        {
            builder.Append("<div role=\"group\" aria-label=\"Phone pairing code\"><p><strong>Scan this with the phone.</strong> Valid until ")
                .Append(html.Encode(current.ExpiresAt.ToString("u", System.Globalization.CultureInfo.InvariantCulture)))
                .Append(".</p>").Append(current.QrSvg)
                .Append("<p>Cannot scan? On the phone choose enter code, then type address <code>")
                .Append(html.Encode($"{current.Address}:{current.Port}")).Append("</code> and code <strong><code>")
                .Append(html.Encode(current.ShortCode)).Append("</code></strong>. The code works once.</p>")
                .Append("<p><label for=\"phonePayload\">Full invitation text (paste on the phone if scanning is not possible)</label><br><textarea id=\"phonePayload\" readonly rows=\"4\" cols=\"60\">")
                .Append(html.Encode(current.Payload)).Append("</textarea></p></div>");
        }

        builder.Append("</section>");
        return builder.ToString();
    }

    private static bool IsAdvertisable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        try
        {
            _ = new PeerPairingInvitation(address, 1, "probe", new byte[32], null);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Server-side QR rendering to an inline SVG (no scripts, no external requests, no style attributes).</summary>
internal static class QrSvg
{
    public static string Render(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
        {
            var x = 0;
            while (x < size)
            {
                if (!matrix[y][x])
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x < size && matrix[y][x])
                {
                    x++;
                }

                path.Append(System.Globalization.CultureInfo.InvariantCulture, $"M{start} {y}h{x - start}v1h-{x - start}z");
            }
        }

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" width=\"288\" height=\"288\" role=\"img\" aria-label=\"QR code containing the Monitor App pairing invitation\" shape-rendering=\"crispEdges\">" +
            $"<rect width=\"{size}\" height=\"{size}\" fill=\"#fff\"/><path fill=\"#000\" d=\"{path}\"/></svg>";
    }
}
