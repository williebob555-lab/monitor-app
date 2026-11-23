using System.Globalization;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Alerts;

public sealed record AlertEvaluationResult(int Raised, int Resolved, int Escalated = 0);

/// <summary>
/// Deterministic built-in rule evaluation. Single execution owner: only the peer that collected an observation
/// (provenance LocalOrigin and origin peer == this peer) evaluates observation and threshold rules for it, so a
/// replicated copy on another peer can never raise a second alert. The peer-silent rule is owned by the observing
/// peer for its own trusted peers. Episode start times and alert ids come from stored data, never from the
/// wall clock at evaluation, so repeated or delayed evaluation is idempotent.
/// </summary>
public sealed class AlertEvaluator(
    PeerStore peers,
    AlertStore alerts,
    AlertRulesConfig config,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private int running;

    private enum Verdict
    {
        Breach = 1,
        Healthy = 2,
        Unknown = 3,
    }

    private sealed record Outcome(
        Verdict Verdict,
        string RuleId,
        string SubjectId,
        DateTimeOffset At,
        AlertSeverity Severity = AlertSeverity.Warning,
        string Title = "",
        string Detail = "");

    private sealed record Sample(DateTimeOffset ObservedAt, string Quality, double? Value);

    public async Task<AlertEvaluationResult> EvaluateOnceAsync(CancellationToken cancellationToken = default)
    {
        config.Validate();
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            return new AlertEvaluationResult(0, 0);
        }

        try
        {
            var local = await peers.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("Local peer identity is not initialized.");
            var now = clock.GetUtcNow().ToUniversalTime();
            var trusted = await peers.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false);
            var activePeers = trusted.Where(item => item.TrustState == PeerTrustState.Active)
                .Select(item => item.PeerId).ToHashSet(StringComparer.Ordinal);
            var recipients = (await alerts.ReadRecipientsAsync(cancellationToken).ConfigureAwait(false))
                .Where(activePeers.Contains).ToArray();
            var current = await peers.ReadCurrentObservationsAsync(cancellationToken).ConfigureAwait(false);
            var owned = current
                .Where(item => item.Provenance == RecordProvenance.LocalOrigin &&
                    string.Equals(item.OriginPeerId, local.PeerId, StringComparison.Ordinal))
                .ToArray();

            var overrides = (await alerts.ReadRuleOverridesAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(item => (item.PeerId, item.RuleId));
            var outcomes = new List<Outcome>();
            foreach (var observation in owned)
            {
                outcomes.Add(await EvaluateUnavailableAsync(local.PeerId, observation, cancellationToken).ConfigureAwait(false));
            }

            foreach (var baseRule in config.Thresholds)
            {
                // A disabled rule reports Healthy so an episode already open for it resolves instead of staying raised forever.
                overrides.TryGetValue((local.PeerId, baseRule.RuleId), out var overridden);
                var rule = overridden?.Threshold is { } threshold ? baseRule with { ThresholdPercent = threshold } : baseRule;
                foreach (var observation in owned.Where(item => item.SignalId == rule.SignalId))
                {
                    outcomes.Add(overridden is { Enabled: false }
                        ? new Outcome(Verdict.Healthy, rule.RuleId, observation.SubjectEntityId, observation.ObservedAt)
                        : await EvaluateThresholdAsync(local.PeerId, rule, observation, cancellationToken).ConfigureAwait(false));
                }
            }

            var lastSeen = await alerts.ReadPeerLastSeenAsync(cancellationToken).ConfigureAwait(false);
            foreach (var peer in trusted.Where(item => item.PeerId != local.PeerId))
            {
                overrides.TryGetValue((peer.PeerId, AlertRulesConfig.PeerSilentRuleId), out var silence);
                outcomes.Add(silence is { Enabled: false }
                    ? new Outcome(Verdict.Healthy, AlertRulesConfig.PeerSilentRuleId, peer.PeerId, now)
                    : EvaluatePeerSilence(peer, lastSeen, current, now,
                        silence?.Threshold is { } minutes ? config with { PeerSilentAfter = TimeSpan.FromMinutes(minutes) } : config));
            }

            var raised = 0;
            var resolved = 0;
            foreach (var outcome in outcomes.OrderBy(item => item.RuleId, StringComparer.Ordinal)
                         .ThenBy(item => item.SubjectId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (outcome.Verdict == Verdict.Breach)
                {
                    if (await InCooldownAsync(local.PeerId, outcome, now, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    var alert = new Alert(
                        Alert.CreateId(local.PeerId, outcome.RuleId, outcome.SubjectId, outcome.At),
                        1, outcome.RuleId, outcome.SubjectId, local.PeerId, outcome.Severity, AlertState.Raised,
                        outcome.Title, outcome.Detail, outcome.At, null);
                    if (await alerts.RaiseAsync(alert, recipients, config.OutboxTtl, now, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        raised++;
                    }
                }
                else if (outcome.Verdict == Verdict.Healthy &&
                    await alerts.ResolveAsync(local.PeerId, outcome.RuleId, outcome.SubjectId, outcome.At, recipients,
                        config.OutboxTtl, now, cancellationToken).ConfigureAwait(false) is not null)
                {
                    resolved++;
                }
            }

            var escalated = await alerts.EscalateDueAsync(now, config.EscalationAfter, recipients, config.OutboxTtl, cancellationToken)
                .ConfigureAwait(false);
            return new AlertEvaluationResult(raised, resolved, escalated.Count);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    /// <summary>
    /// True while a resolved episode of the same rule and subject is younger than the re-fire cooldown, so a
    /// flapping condition produces one alert per cooldown window instead of one per flap. An active episode is not a
    /// cooldown case (the store ignores a duplicate raise).
    /// </summary>
    private async Task<bool> InCooldownAsync(
        string localPeerId,
        Outcome outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (config.RefireCooldown <= TimeSpan.Zero)
        {
            return false;
        }

        var resolvedAt = await alerts.ReadLastResolvedAtAsync(localPeerId, outcome.RuleId, outcome.SubjectId, cancellationToken)
            .ConfigureAwait(false);
        return resolvedAt is { } resolved && now - resolved < config.RefireCooldown;
    }

    private async Task<Outcome> EvaluateUnavailableAsync(
        string localPeerId,
        CurrentObservation latest,
        CancellationToken cancellationToken)
    {
        var rule = AlertRulesConfig.ObservationUnavailableRuleId;
        var subject = $"{latest.SubjectEntityId}#{latest.SignalId}";
        // "Unavailable" means the device does not provide this reading (unsupported capability, no battery, no Wi-Fi link):
        // that is a fact about the hardware, not a fault, so it never raises and it resolves an alert raised under the
        // older rule. Only explicit collection failures (Error, PermissionDenied) are problems.
        if (IsGood(latest.Quality) || latest.Quality == "Unavailable")
        {
            return new Outcome(Verdict.Healthy, rule, subject, latest.ObservedAt);
        }

        if (!IsBad(latest.Quality))
        {
            return new Outcome(Verdict.Unknown, rule, subject, latest.ObservedAt);
        }

        var samples = await ReadSamplesAsync(localPeerId, latest.SubjectEntityId, latest.SignalId, latest.ObservedAt,
            Lookback(config.UnavailableAfter), cancellationToken).ConfigureAwait(false);
        var start = TrailingRunStart(samples, sample => IsBad(sample.Quality));
        return start is { } begin && latest.ObservedAt - begin >= config.UnavailableAfter
            ? new Outcome(Verdict.Breach, rule, subject, begin, AlertSeverity.Warning,
                latest.Quality == "PermissionDenied" ? $"No permission to read {latest.SignalId}" : $"Cannot read {latest.SignalId}",
                $"{latest.SignalId} on {latest.SubjectEntityId} has reported {latest.Quality} since {begin:O}.")
            : new Outcome(Verdict.Unknown, rule, subject, latest.ObservedAt);
    }

    private async Task<Outcome> EvaluateThresholdAsync(
        string localPeerId,
        ThresholdRule rule,
        CurrentObservation latest,
        CancellationToken cancellationToken)
    {
        var subject = latest.SubjectEntityId;
        var numerators = await ReadSamplesAsync(localPeerId, subject, rule.SignalId, latest.ObservedAt,
            Lookback(rule.Sustained), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Sample> percentages = numerators;
        if (rule.DenominatorSignalId is { } denominatorSignal)
        {
            var denominators = (await ReadSamplesAsync(localPeerId, subject, denominatorSignal, latest.ObservedAt,
                Lookback(rule.Sustained), cancellationToken).ConfigureAwait(false))
                .ToDictionary(item => item.ObservedAt);
            percentages = numerators.Select(item => denominators.TryGetValue(item.ObservedAt, out var denominator) &&
                IsGood(item.Quality) && IsGood(denominator.Quality) && item.Value is { } n &&
                denominator.Value is { } d and > 0
                ? item with { Value = n / d * 100 }
                : item with { Value = null }).ToArray();
        }
        else
        {
            percentages = numerators.Select(item => item with { Value = item.Value * 100 }).ToArray();
        }

        var last = percentages.Count == 0 ? null : percentages[percentages.Count - 1];
        if (last is null || last.ObservedAt != latest.ObservedAt || last.Value is not { } value || !IsGood(last.Quality))
        {
            return new Outcome(Verdict.Unknown, rule.RuleId, subject, latest.ObservedAt);
        }

        bool Breaches(Sample sample) => IsGood(sample.Quality) && sample.Value is { } v &&
            (rule.Comparison == ThresholdComparison.Below ? v < rule.ThresholdPercent : v > rule.ThresholdPercent);

        if (!Breaches(last))
        {
            return new Outcome(Verdict.Healthy, rule.RuleId, subject, last.ObservedAt);
        }

        var start = TrailingRunStart(percentages, Breaches)!.Value;
        if (last.ObservedAt - start < rule.Sustained)
        {
            return new Outcome(Verdict.Unknown, rule.RuleId, subject, last.ObservedAt);
        }

        var comparison = rule.Comparison == ThresholdComparison.Below ? "below" : "above";
        return new Outcome(Verdict.Breach, rule.RuleId, subject, start, rule.Severity, rule.Title,
            string.Create(CultureInfo.InvariantCulture,
                $"{rule.Title} on {subject}: {value:F1}% is {comparison} {rule.ThresholdPercent:F1}% since {start:O}."));
    }

    private static Outcome EvaluatePeerSilence(
        TrustedPeer peer,
        IReadOnlyDictionary<string, DateTimeOffset> touched,
        IReadOnlyList<CurrentObservation> current,
        DateTimeOffset now,
        AlertRulesConfig rules)
    {
        const string rule = AlertRulesConfig.PeerSilentRuleId;
        if (peer.TrustState != PeerTrustState.Active)
        {
            return new Outcome(Verdict.Healthy, rule, peer.PeerId, peer.RevokedAt ?? now);
        }

        var seen = peer.PairedAt;
        if (touched.TryGetValue(peer.PeerId, out var touchedAt) && touchedAt > seen)
        {
            seen = touchedAt;
        }

        foreach (var observation in current)
        {
            if (observation.Provenance == RecordProvenance.Replicated &&
                string.Equals(observation.OriginPeerId, peer.PeerId, StringComparison.Ordinal) &&
                observation.ReceivedAt > seen)
            {
                seen = observation.ReceivedAt;
            }
        }

        var deadline = seen + rules.PeerSilentAfter;
        var name = peer.DisplayName ?? peer.PeerId;
        return now >= deadline
            ? new Outcome(Verdict.Breach, rule, peer.PeerId, deadline, AlertSeverity.Warning,
                $"Peer not seen: {name}",
                $"{name} has not connected or replicated since {seen:O} (silent for at least {FormatMinutes(rules.PeerSilentAfter)}).")
            : new Outcome(Verdict.Healthy, rule, peer.PeerId, seen);
    }

    private static string FormatMinutes(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.TotalMinutes:F0} minutes");

    private async Task<IReadOnlyList<Sample>> ReadSamplesAsync(
        string localPeerId,
        string subject,
        string signal,
        DateTimeOffset latest,
        TimeSpan lookback,
        CancellationToken cancellationToken)
    {
        var page = await peers.ReadOriginWindowAsync(new OriginRecordWindowQuery(
            latest - lookback, latest.AddTicks(1), subject, signal, localPeerId, null, 5000), cancellationToken)
            .ConfigureAwait(false);
        return page.Records
            .Where(item => item.Provenance == RecordProvenance.LocalOrigin)
            .Select(item => new Sample(item.ObservedAt, item.Quality, DecodeValue(item.RecordBytes)))
            .ToArray();
    }

    private static double? DecodeValue(byte[] recordBytes)
    {
        try
        {
            var envelope = TelemetryEnvelope.Parser.ParseFrom(recordBytes);
            if (envelope.PayloadCase != TelemetryEnvelope.PayloadOneofCase.Metric)
            {
                return null;
            }

            var value = envelope.Metric.Value;
            return value?.ValueCase switch
            {
                MetricValue.ValueOneofCase.DoubleValue => value.DoubleValue,
                MetricValue.ValueOneofCase.SignedIntegerValue => value.SignedIntegerValue,
                MetricValue.ValueOneofCase.UnsignedIntegerValue => value.UnsignedIntegerValue,
                _ => null,
            };
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            return null;
        }
    }

    /// <summary>Start of the unbroken trailing run of matching samples (samples are ascending), or null if the last does not match.</summary>
    private static DateTimeOffset? TrailingRunStart(IReadOnlyList<Sample> samples, Func<Sample, bool> matches)
    {
        DateTimeOffset? start = null;
        for (var index = samples.Count - 1; index >= 0 && matches(samples[index]); index--)
        {
            start = samples[index].ObservedAt;
        }

        return start;
    }

    private static TimeSpan Lookback(TimeSpan sustained) =>
        TimeSpan.FromTicks(Math.Max(sustained.Ticks * 2, TimeSpan.FromMinutes(30).Ticks));

    private static bool IsGood(string quality) => quality is "Valid" or "Estimated";

    private static bool IsBad(string quality) => quality is "Error" or "PermissionDenied";
}

internal static class CurrentObservationExtensions
{
    public static string QualityLabel(this CurrentObservation observation) => observation.Quality.ToLowerInvariant();
}
