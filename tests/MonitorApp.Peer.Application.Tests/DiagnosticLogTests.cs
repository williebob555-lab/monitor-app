using MonitorApp.Peer.Application;
using Xunit;

namespace MonitorApp.Peer.Application.Tests;

[Collection(DiagnosticLogGroup.Name)]
public sealed class DiagnosticLogTests
{
    [Fact]
    public void LinesReachTheRingAndTheDailyFileWithKeysCut()
    {
        var folder = Path.Combine(Path.GetTempPath(), "monitor-diagnostics-" + Guid.NewGuid().ToString("N"));
        var now = new DateTimeOffset(2026, 11, 24, 13, 30, 0, TimeSpan.Zero);
        DiagnosticLog.Reset();
        try
        {
            DiagnosticLog.Configure(folder, () => now);
            var key = Convert.ToBase64String(new byte[64]);
            DiagnosticLog.Write("pairing", $"pinned key {key} for peer-2ea34d025aa98eea3f44");

            var line = Assert.Single(DiagnosticLog.Recent(), item => item.Contains("pinned key", StringComparison.Ordinal));
            Assert.Contains("[pairing] pinned key AAAAAAAA… for peer-2ea34d025aa98eea3f44", line, StringComparison.Ordinal);
            Assert.DoesNotContain(key, line, StringComparison.Ordinal);
            Assert.Contains(line + Environment.NewLine, File.ReadAllText(Path.Combine(folder, "diagnostics-20261124.log")), StringComparison.Ordinal);
        }
        finally
        {
            DiagnosticLog.Reset();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void LinkChangesAreLoggedOnceEach()
    {
        DiagnosticLog.Reset();
        var board = new PeerLinkStatusBoard();
        board.Connecting("peer-diag-a", "100.88.178.60:55431");
        board.Failed("peer-diag-a", "Connection refused", retrying: true);
        board.Failed("peer-diag-a", "Connection refused", retrying: true);
        board.Connected("peer-diag-a");

        var lines = DiagnosticLog.Recent().Where(line => line.Contains("peer-diag-a", StringComparison.Ordinal)).Select(line => line[(line.IndexOf('[', StringComparison.Ordinal))..]).ToArray();
        Assert.Equal(
        [
            "[link] peer-diag-a Connecting via 100.88.178.60:55431",
            "[link] peer-diag-a Retrying via 100.88.178.60:55431: Connection refused",
            "[link] peer-diag-a Connected via 100.88.178.60:55431",
        ], lines);
        DiagnosticLog.Reset();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticLogGroup
{
    public const string Name = "diagnostic log";
}
