using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;

namespace MonitorApp.Peer.Collection;

/// <summary>Readings about one subject (this computer, or a device it found) taken in one pass.</summary>
public sealed record SubjectReadings(string SubjectEntityId, IReadOnlyList<CollectedMetric> Metrics);

/// <summary>
/// Signs and stores readings about several subjects in one transaction, for collectors that describe other things than
/// this computer (devices found on the network, Tailscale devices). Same signing, sequence and gate rules as
/// <see cref="PeerSystemMetricsIngestionBridge"/>, so the records replicate exactly like system readings.
/// </summary>
public sealed class SubjectObservationsWriter(
    PeerStore store,
    ILocalPeerIdentityProvider localIdentity,
    string collectorId,
    string bootId,
    Func<DateTimeOffset>? utcNow = null)
{
    public const int MaximumRecordsPerWrite = 4_000;
    private readonly Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Stores every reading; returns how many records were written.</summary>
    public async Task<int> WriteAsync(DateTimeOffset observedAt, IReadOnlyList<SubjectReadings> subjects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (observedAt.Offset != TimeSpan.Zero) throw new InvalidOperationException("Collection timestamps must use UTC.");
        var total = subjects.Sum(s => s.Metrics.Count);
        if (total == 0) return 0;
        if (total > MaximumRecordsPerWrite) throw new InvalidOperationException("Too many readings in one write.");
        foreach (var subject in subjects)
        {
            if (string.IsNullOrWhiteSpace(subject.SubjectEntityId) || subject.SubjectEntityId.Length > 512)
                throw new InvalidOperationException("Subject entity id is required and bounded.");
        }

        await store.LocalAppendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = await store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("Local peer persistence identity is not initialized.");
            var provider = localIdentity.PublicIdentity;
            if (!string.Equals(identity.PeerId, provider.PeerId, StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(identity.PublicKeyFingerprint, provider.PublicKeyFingerprint))
            {
                throw new InvalidOperationException("Local signing identity does not match the initialized peer persistence identity.");
            }

            var receivedAt = clock();
            var observed = Timestamp.FromDateTimeOffset(observedAt);
            var sequence = identity.NextSequence;
            if (sequence > ulong.MaxValue - (ulong)total) throw new InvalidOperationException("Local origin sequence space is exhausted.");
            var writes = new List<OriginRecordWrite>(total);
            foreach (var subject in subjects)
            {
                foreach (var metric in subject.Metrics)
                {
                    var sample = new MetricSample { MetricId = metric.MetricId, Unit = metric.Unit, Quality = metric.Quality };
                    if (metric.Value is not null) sample.Value = metric.Value.Clone();
                    var envelope = new TelemetryEnvelope
                    {
                        AgentId = identity.PeerId,
                        BootId = bootId,
                        CollectorId = collectorId,
                        ObservedAt = observed.Clone(),
                        Metric = sample,
                        Sequence = sequence,
                        IngestedAt = Timestamp.FromDateTimeOffset(receivedAt),
                    };
                    var record = OriginRecordCryptography.CreateSigned(
                        localIdentity.IdentityKey, identity.PeerId, identity.OriginStreamId, sequence,
                        subject.SubjectEntityId, collectorId, metric.MetricId, envelope.ToByteString(), observed.Clone(), metric.Quality);
                    writes.Add(new OriginRecordWrite(
                        record.OriginPeerId, record.OriginStreamId, record.Sequence, record.SubjectEntityId, record.CapabilityId,
                        record.SignalId, record.RecordBytes.ToByteArray(), record.OriginSignature.ToByteArray(), observedAt, receivedAt,
                        null, RecordProvenance.LocalOrigin, metric.Quality.ToString()));
                    sequence++;
                }
            }

            await store.AppendOriginRecordsAsync(writes, cancellationToken).ConfigureAwait(false);
            return writes.Count;
        }
        finally
        {
            store.LocalAppendGate.Release();
        }
    }
}
