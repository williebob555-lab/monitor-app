using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;

namespace MonitorApp.Peer.Collection;

public sealed class PeerSystemMetricsIngestionBridge
{
    private readonly PeerStore store;
    private readonly ILocalPeerIdentityProvider localIdentity;
    private readonly ISystemSnapshotCollector collector;
    private readonly PeerCollectionOptions options;
    private readonly Func<DateTimeOffset> utcNow;
    private int running;

    public PeerSystemMetricsIngestionBridge(
        PeerStore store,
        ILocalPeerIdentityProvider localIdentity,
        ISystemSnapshotCollector collector,
        PeerCollectionOptions options,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.localIdentity = localIdentity ?? throw new ArgumentNullException(nameof(localIdentity));
        this.collector = collector ?? throw new ArgumentNullException(nameof(collector));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<PeerCollectionRunResult> RunOnceAsync(CancellationToken cancellationToken = default) =>
        RunOnceAsync(null, cancellationToken);

    /// <summary>
    /// One collection run. With a <paramref name="signalFilter"/> only the named signals are stored (the cheap fast path
    /// while a viewer watches specific signals); null stores everything.
    /// </summary>
    public async Task<PeerCollectionRunResult> RunOnceAsync(
        IReadOnlySet<string>? signalFilter,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            return new PeerCollectionRunResult(
                PeerCollectionRunStatus.AlreadyRunning,
                0,
                null,
                null,
                null);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.CollectionTimeout);
            SystemSnapshot snapshot;
            try
            {
                snapshot = await collector.CollectAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("System metric collection exceeded its configured timeout.", exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var observedAt = RequireUtc(snapshot.ObservedAt);
            // Another bridge on the same store (LAN links) may be allocating local sequences right now.
            await store.LocalAppendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var appendGate = new GateRelease(store.LocalAppendGate);
            var receivedAt = RequireUtc(utcNow());
            var identity = await store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("Local peer persistence identity is not initialized.");
            EnsureIdentityMatches(identity);

            var envelopes = TelemetryEnvelopeFactory.Create(
                identity.PeerId,
                options.BootId,
                options.CollectorId,
                snapshot);
            if (signalFilter is not null)
            {
                envelopes = [.. envelopes.Where(envelope => signalFilter.Contains(SignalId(envelope)))];
            }

            if (envelopes.Count > options.MaximumRecordsPerRun)
            {
                throw new InvalidOperationException("Collected snapshot exceeds the configured record limit.");
            }

            if (envelopes.Count == 0)
            {
                return new PeerCollectionRunResult(
                    PeerCollectionRunStatus.Completed,
                    0,
                    null,
                    null,
                    observedAt);
            }

            var nextSequence = identity.NextSequence;
            if (nextSequence > ulong.MaxValue - (ulong)envelopes.Count)
            {
                throw new InvalidOperationException("Local origin sequence space is exhausted.");
            }

            var firstSequence = nextSequence;
            var writes = new List<OriginRecordWrite>(envelopes.Count);
            foreach (var envelope in envelopes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                envelope.Sequence = nextSequence;
                envelope.IngestedAt = Timestamp.FromDateTimeOffset(receivedAt);
                var quality = Quality(envelope);
                var signalId = SignalId(envelope);
                var record = OriginRecordCryptography.CreateSigned(
                    localIdentity.IdentityKey,
                    identity.PeerId,
                    identity.OriginStreamId,
                    nextSequence,
                    options.SubjectEntityId,
                    options.CollectorId,
                    signalId,
                    envelope.ToByteString(),
                    snapshot.ObservedAt.Clone(),
                    quality);
                writes.Add(new OriginRecordWrite(
                    record.OriginPeerId,
                    record.OriginStreamId,
                    record.Sequence,
                    record.SubjectEntityId,
                    record.CapabilityId,
                    record.SignalId,
                    record.RecordBytes.ToByteArray(),
                    record.OriginSignature.ToByteArray(),
                    observedAt,
                    receivedAt,
                    null,
                    RecordProvenance.LocalOrigin,
                    quality.ToString()));
                nextSequence++;
            }

            // One transaction for the whole collection: all of its records are stored, or none are.
            await store.AppendOriginRecordsAsync(writes, cancellationToken).ConfigureAwait(false);

            return new PeerCollectionRunResult(
                PeerCollectionRunStatus.Completed,
                envelopes.Count,
                firstSequence,
                nextSequence - 1,
                observedAt);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private void EnsureIdentityMatches(LocalPeerIdentity identity)
    {
        var provider = localIdentity.PublicIdentity;
        if (!string.Equals(identity.PeerId, provider.PeerId, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(
                identity.PublicKeyFingerprint,
                provider.PublicKeyFingerprint))
        {
            throw new InvalidOperationException(
                "Local signing identity does not match the initialized peer persistence identity.");
        }
    }

    private static string SignalId(TelemetryEnvelope envelope) => envelope.PayloadCase switch
    {
        TelemetryEnvelope.PayloadOneofCase.Metric => envelope.Metric.MetricId,
        TelemetryEnvelope.PayloadOneofCase.CollectorStatus =>
            $"collector.health.{envelope.CollectorStatus.CapabilityId}",
        _ => throw new InvalidOperationException("Collected telemetry payload type is unsupported."),
    };

    private static ObservationQuality Quality(TelemetryEnvelope envelope) => envelope.PayloadCase switch
    {
        TelemetryEnvelope.PayloadOneofCase.Metric => envelope.Metric.Quality,
        TelemetryEnvelope.PayloadOneofCase.CollectorStatus => envelope.CollectorStatus.State switch
        {
            CapabilityState.Available => ObservationQuality.Valid,
            CapabilityState.Degraded => ObservationQuality.Estimated,
            CapabilityState.Unsupported => ObservationQuality.Unavailable,
            CapabilityState.PermissionDenied => ObservationQuality.PermissionDenied,
            CapabilityState.Error => ObservationQuality.Error,
            _ => ObservationQuality.Error,
        },
        _ => throw new InvalidOperationException("Collected telemetry payload type is unsupported."),
    };

    private static DateTimeOffset RequireUtc(Timestamp timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        return RequireUtc(timestamp.ToDateTimeOffset());
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new InvalidOperationException("Collection timestamps must use UTC.");

    private readonly struct GateRelease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

