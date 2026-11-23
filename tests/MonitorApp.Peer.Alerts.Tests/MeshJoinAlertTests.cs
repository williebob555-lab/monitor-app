using MonitorApp.Contracts.V1;
using Xunit;

namespace MonitorApp.Peer.Alerts.Tests;

public sealed class MeshJoinAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADeviceThatJoinedIsShownOnceHoweverManyComputersAnnounceItAndNeverToItself()
    {
        InboxAlert Item(string origin, string rule, string subject, int minutes) => new(new Alert(
            Alert.CreateId(origin, rule, subject, Now.AddMinutes(minutes)), 1, rule, subject, origin, AlertSeverity.Warning,
            AlertState.Raised, "t", "d", Now.AddMinutes(minutes), null), Now, 0);
        var fromServer = Item("server", MeshJoinAlerts.RuleId, "tablet", 1);
        var fromDesk = Item("desk", MeshJoinAlerts.RuleId, "tablet", 0);
        var aboutMe = Item("desk", MeshJoinAlerts.RuleId, "phone", 0);
        var cpuDesk = Item("desk", "threshold.cpu_high", "cpu", 0);
        var cpuServer = Item("server", "threshold.cpu_high", "cpu", 0);

        var shown = MeshJoinAlerts.Collapse([fromServer, fromDesk, aboutMe, cpuDesk, cpuServer], "phone");

        Assert.Equal([fromDesk, cpuDesk, cpuServer], shown);
    }
}
