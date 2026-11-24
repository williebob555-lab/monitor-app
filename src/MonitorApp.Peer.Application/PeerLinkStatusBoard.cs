using System.Collections.Concurrent;
using System.Globalization;

namespace MonitorApp.Peer.Application;

/// <summary>Where the link to one trusted device stands right now.</summary>
public enum PeerLinkState
{
    /// <summary>Nothing tried yet since this device started.</summary>
    Unknown = 0,

    /// <summary>Dialing an address.</summary>
    Connecting = 1,

    /// <summary>A session is up (dialed out, or the other device dialed in).</summary>
    Connected = 2,

    /// <summary>The last try failed; it will be tried again.</summary>
    Retrying = 3,

    /// <summary>Given up until something changes (refused, incompatible, revoked).</summary>
    Stopped = 4,

    /// <summary>This device cannot dial it (no address, for example a phone); it waits for the other device to connect.</summary>
    Waiting = 5,

    /// <summary>Neither device can call the other (two phones or tablets); readings pass through the computers both reach.</summary>
    Indirect = 6,
}

/// <summary>
/// The live state of one link, with the address being used and the last failure in the transport's own words. Kept in
/// memory only: it says what is happening now, while <see cref="PeerStateTracker"/> keeps the durable history.
/// </summary>
public sealed record PeerLinkStatus(
    string PeerId,
    PeerLinkState State,
    string? Address,
    DateTimeOffset Since,
    DateTimeOffset? LastConnectedAt,
    string? LastError,
    DateTimeOffset? LastErrorAt);

/// <summary>
/// What each connection to another device is doing right now (R4 in the bill of work), so the pairing screen can say
/// "connected", "trying 192.168.1.20", or "can't reach it: timed out" instead of leaving the person to guess why readings
/// stopped. Every connection path reports into it: dialing out, sessions the other device opened, and failures.
/// </summary>
public sealed class PeerLinkStatusBoard
{
    private readonly ConcurrentDictionary<string, PeerLinkStatus> links = new(StringComparer.Ordinal);
    private readonly TimeProvider time;

    public PeerLinkStatusBoard(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    public void Connecting(string peerId, string address) =>
        Update(peerId, current => current with { State = PeerLinkState.Connecting, Address = address, Since = Now });

    public void Connected(string peerId, string? address = null) =>
        Update(peerId, current => current with
        {
            State = PeerLinkState.Connected,
            Address = address ?? current.Address,
            Since = current.State == PeerLinkState.Connected ? current.Since : Now,
            LastConnectedAt = Now,
        });

    /// <summary>A session ended or a try failed. <paramref name="retrying"/> false means it will not be tried again by itself.</summary>
    public void Failed(string peerId, string error, bool retrying) =>
        Update(peerId, current => current with
        {
            State = retrying ? PeerLinkState.Retrying : PeerLinkState.Stopped,
            Since = Now,
            LastError = error,
            LastErrorAt = Now,
        });

    /// <summary>A session the other device opened has closed; with no way to dial it, this device waits for it.</summary>
    public void Disconnected(string peerId, bool canDial) =>
        Update(peerId, current => current.State != PeerLinkState.Connected
            ? current
            : current with { State = canDial ? PeerLinkState.Retrying : PeerLinkState.Waiting, Since = Now });

    public void Waiting(string peerId) =>
        Update(peerId, current => current.State is PeerLinkState.Connected or PeerLinkState.Connecting or PeerLinkState.Retrying
            ? current
            : current with { State = PeerLinkState.Waiting, Since = Now });

    public PeerLinkStatus? Get(string peerId) => links.TryGetValue(peerId, out var status) ? status : null;

    public IReadOnlyDictionary<string, PeerLinkStatus> Snapshot() => new Dictionary<string, PeerLinkStatus>(links, StringComparer.Ordinal);

    private DateTimeOffset Now => time.GetUtcNow();

    private void Update(string peerId, Func<PeerLinkStatus, PeerLinkStatus> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        links.AddOrUpdate(
            peerId,
            id => change(new PeerLinkStatus(id, PeerLinkState.Unknown, null, Now, null, null, null)),
            (_, current) => change(current));
    }

    /// <summary>
    /// The link and the readings in one or two plain sentences for a device's card: what is happening, the reason when it
    /// is not working, and when the last reading from that device arrived.
    /// </summary>
    public static string Describe(PeerLinkStatus? link, DateTimeOffset? lastReadingAt, DateTimeOffset now, string? name = null)
    {
        var who = string.IsNullOrWhiteSpace(name) ? "it" : name;
        var via = link?.Address is { Length: > 0 } address ? $" ({address})" : string.Empty;
        var connection = link?.State switch
        {
            PeerLinkState.Connected => $"Connected{via} for {Span(now - link.Since)}.",
            PeerLinkState.Connecting => $"Connecting to {who}{via}…",
            PeerLinkState.Retrying => $"Can't reach {who}{via}: {Reason(link.LastError)} Trying again.",
            PeerLinkState.Stopped => $"Stopped trying to reach {who}: {Reason(link.LastError)}",
            PeerLinkState.Waiting => $"Not connected. This device can't call {who}; it waits for {who} to connect" +
                (link.LastConnectedAt is { } last ? $" (last connected {Span(now - last)} ago)." : "."),
            PeerLinkState.Indirect => $"Not connected directly: phones and tablets can't call each other, so {who}'s readings come through your computers.",
            _ => "Not connected yet since this device started.",
        };
        var readings = lastReadingAt is { } at
            ? $" Last reading from {who}: {(now - at < TimeSpan.FromMinutes(1) ? "just now" : Span(now - at) + " ago")}."
            : $" No readings from {who} have arrived here yet.";
        return connection + readings;
    }

    private static string Reason(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "no reason given." : error.TrimEnd().EndsWith('.') ? error.Trim() : error.Trim() + ".";

    private static string Span(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "less than a minute",
        { TotalHours: < 1 } => Plural((int)span.TotalMinutes, "minute"),
        { TotalDays: < 1 } => Plural((int)span.TotalHours, "hour"),
        _ => Plural((int)span.TotalDays, "day"),
    };

    private static string Plural(int count, string unit) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + unit + (count == 1 ? string.Empty : "s");
}
