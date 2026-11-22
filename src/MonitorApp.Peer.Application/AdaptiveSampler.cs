using System.Diagnostics;

namespace MonitorApp.Peer.Application;

/// <summary>
/// Collection pacing shared by the desktop scheduler and the phone loop: sample at the normal interval, switch to the
/// registry's fast interval while any unexpired live interest exists, and fall back when it lapses.
/// </summary>
public static class AdaptiveSampler
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> work,
        string subjectPeerId,
        TimeSpan normalInterval,
        LiveInterestRegistry? interests,
        bool collectImmediately,
        Action<TimeSpan>? onInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectPeerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(normalInterval, TimeSpan.Zero);

        using var wake = new SemaphoreSlim(0, 1);
        using var subscription = interests?.Subscribe(() =>
        {
            try
            {
                wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // A wake-up is already pending; coalescing is exactly what is wanted.
            }
        });
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!first || collectImmediately)
            {
                await work(cancellationToken).ConfigureAwait(false);
            }

            first = false;
            var waited = Stopwatch.StartNew();
            while (true)
            {
                var target = interests?.EffectiveInterval(subjectPeerId, normalInterval) ?? normalInterval;
                onInterval?.Invoke(target);
                var remaining = target - waited.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                if (!await wake.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
    }
}
