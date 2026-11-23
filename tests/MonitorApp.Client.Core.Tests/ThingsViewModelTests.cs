using MonitorApp.Client.Core.ViewModels;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

public sealed class ThingsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CardsJoinLiveReadingsAndNestParts()
    {
        var laptop = Thing("peer:laptop", "Wilson-laptop", "Computer", subjects: ["peer:laptop"], parts: ["observed:nic"],
            facts: [new ThingFactDto("identity.peer-key-fingerprint", "Peer key fingerprint", "AB12", "Measured", "monitoring", Now, 0)]);
        var nic = Thing("observed:nic", "Network adapter wlan0", "Network adapter", inside: "peer:laptop",
            facts: [new ThingFactDto("identity.lan.interface.mac", "Hardware address (MAC)", "aa:bb", "Measured", "monitoring", Now, 0)]);
        var amp = Thing("thing:amp", "Focusrite 2i2", "Audio equipment",
            facts: [new ThingFactDto("note.text", "Note", "needs a 12 V supply", "Entered", "you", Now, 0)]);
        HistoryPointDto[] readings =
        [
            Point("peer:laptop", "system.memory.total", 16e9, "Bytes"),
            Point("peer:other", "system.memory.total", 8e9, "Bytes"),
        ];

        var cards = ThingsViewModel.Build([laptop, nic, amp], readings);

        Assert.Equal(["Wilson-laptop", "Focusrite 2i2"], cards.Select(c => c.Name));
        var laptopCard = cards[0];
        var memory = Assert.Single(laptopCard.Values);
        Assert.Equal(("measured · live", "Brush.Healthy"), (memory.Badge, memory.BadgeBrushKey));
        Assert.Equal("16.0 GB", memory.Value);
        var part = Assert.Single(laptopCard.Parts);
        Assert.Equal("Hardware address (MAC) aa:bb", part.Detail);
        var note = Assert.Single(cards[1].Values);
        Assert.Equal(("entered", "Brush.Planned"), (note.Badge, note.BadgeBrushKey));
    }

    private static ThingDto Thing(string id, string name, string kind, string? inside = null, string[]? subjects = null, string[]? parts = null, ThingFactDto[]? facts = null) =>
        new(id, name, kind.ToLowerInvariant(), kind, "Owned", [], subjects is { Length: > 0 }, subjects ?? [], facts ?? [], inside, parts ?? [], Now);

    private static HistoryPointDto Point(string subject, string signal, double value, string unit) =>
        new("origin", "stream", 1, subject, signal, Now, Now, "collector", "LocalOrigin", "Valid", "Fresh", 5, new ObservationValueDto("number", value, null, null), unit, null);
}
