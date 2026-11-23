using System.Globalization;

namespace MonitorApp.Client.Mobile;

/// <summary>
/// Keeps the last unexpected error on disk so the next launch can show it. A phone app that just closes tells nobody
/// anything; with this the person sees what went wrong and can pass it on. Only the exception type, message and stack
/// are kept, never readings or keys, and only the latest one.
/// </summary>
public sealed class CrashJournal(string directory)
{
    private const string FileName = "last-problem.txt";
    private const int MaxLength = 6000;

    private string PathName => Path.Combine(directory, FileName);

    /// <summary>Records an error. Never throws: it runs while the app is already failing.</summary>
    public string Record(Exception exception, string where, string version, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var text = Describe(exception, where, version, at);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(PathName, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return text;
    }

    /// <summary>Returns the recorded error, if any, and forgets it so it is shown once.</summary>
    public string? Take()
    {
        try
        {
            if (!File.Exists(PathName))
            {
                return null;
            }

            var text = File.ReadAllText(PathName);
            File.Delete(PathName);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Describe(Exception exception, string where, string version, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var root = exception;
        while (root is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            root = aggregate.InnerExceptions[0];
        }

        var text = string.Create(CultureInfo.InvariantCulture,
            $"Something went wrong ({where}, version {version}, {at:yyyy-MM-dd HH:mm} UTC): {root.GetType().Name}: {root.Message}\n\n{root}");
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }
}
