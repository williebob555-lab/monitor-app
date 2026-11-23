using MonitorApp.Client.Mobile;
using Xunit;

namespace MonitorApp.Client.Mobile.Tests;

public sealed class CrashJournalTests
{
    [Fact]
    public void ALastProblemIsShownOnceAndThenForgotten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "monitor-crash-journal", Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new CrashJournal(directory);
            Assert.Null(journal.Take());

            InvalidOperationException thrown;
            try
            {
                throw new InvalidOperationException("Call from invalid thread");
            }
            catch (InvalidOperationException exception)
            {
                thrown = exception;
            }

            journal.Record(new AggregateException(thrown), "screen", "0.1.19", DateTimeOffset.UnixEpoch);
            var shown = journal.Take();
            Assert.NotNull(shown);
            Assert.StartsWith("Something went wrong (screen, version 0.1.19, 1970-01-01 00:00 UTC): InvalidOperationException: Call from invalid thread", shown, StringComparison.Ordinal);
            Assert.Contains(nameof(ALastProblemIsShownOnceAndThenForgotten), shown, StringComparison.Ordinal);
            Assert.Null(journal.Take());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
