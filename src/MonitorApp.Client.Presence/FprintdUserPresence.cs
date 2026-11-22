using System.ComponentModel;
using System.Diagnostics;
using MonitorApp.Client.Core;

namespace MonitorApp.Client.Presence;

/// <summary>
/// Linux fingerprint verification through the stock <c>fprintd-verify</c> tool (fprintd over D-Bus, current user).
/// A process call is used instead of a D-Bus library on purpose: no new dependency in the trust chain, the tool already
/// speaks to fprintd and polkit correctly, and the boundary is testable with a real child process. Strictness: a fixed
/// absolute executable path that must not be group/world writable, no arguments (the tool verifies the invoking user),
/// no shell, a hard timeout that kills the process, and success only when the tool exits 0 and reports
/// <c>verify-match</c>.
/// </summary>
public sealed class FprintdUserPresence : ILocalUserPresence
{
    public static readonly IReadOnlyList<string> DefaultExecutablePaths =
        ["/usr/bin/fprintd-verify", "/bin/fprintd-verify", "/usr/local/bin/fprintd-verify"];

    private readonly string? executable;
    private readonly TimeSpan timeout;

    public FprintdUserPresence(string? executablePath = null, TimeSpan? timeout = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        executable = OperatingSystem.IsWindows()
            ? null
            : Resolve(executablePath is null ? DefaultExecutablePaths : [executablePath]);
    }

    public bool IsAvailable => executable is not null;

    public async Task<LocalPresenceVerification> VerifyAsync(string reason, CancellationToken cancellationToken)
    {
        // The reason cannot be shown by fprintd-verify; it is accepted so the contract matches other providers.
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (executable is null)
        {
            return LocalPresenceVerification.Unavailable;
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("fprintd-verify did not start.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return LocalPresenceVerification.Unavailable;
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false) + "\n" + await error.ConfigureAwait(false);
                return Classify(process.ExitCode, text);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                return LocalPresenceVerification.Cancelled;
            }
        }
    }

    /// <summary>Maps the tool's exit code and text to an outcome; exposed for tests of the parsing rules.</summary>
    internal static LocalPresenceVerification Classify(int exitCode, string text)
    {
        var lower = text.ToLowerInvariant();
        if (exitCode == 0 && lower.Contains("verify result: verify-match", StringComparison.Ordinal))
        {
            return LocalPresenceVerification.Verified;
        }

        if (lower.Contains("no devices available", StringComparison.Ordinal) ||
            lower.Contains("no fingerprints enrolled", StringComparison.Ordinal) ||
            lower.Contains("no enrolled", StringComparison.Ordinal) ||
            lower.Contains("verify-disconnected", StringComparison.Ordinal))
        {
            return LocalPresenceVerification.Unavailable;
        }

        return LocalPresenceVerification.Cancelled;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process ended between the check and the kill.
        }
    }

    private static string? Resolve(IEnumerable<string> candidates)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var path in candidates)
        {
            if (!Path.IsPathRooted(path) || !File.Exists(path))
            {
                continue;
            }

            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 ||
                (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            {
                continue;
            }

            return Path.GetFullPath(path);
        }

        return null;
    }
}
