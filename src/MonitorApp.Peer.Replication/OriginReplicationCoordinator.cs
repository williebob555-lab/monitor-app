using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Replication;

public interface IOriginBatchReceiver
{
    Task<OriginBatchApplyResult> ApplyAsync(string sourcePeerId, OriginRecordBatch batch, CancellationToken cancellationToken = default);
}

public interface IOriginRangeProvider
{
    Task<OriginRecordBatch> ReadAsync(OriginRangeRequest request, CancellationToken cancellationToken = default);
}

public sealed record OriginBatchApplyResult(OriginAck Acknowledgement, IReadOnlyList<ReplicationGap> Gaps, int InsertedCount, int IdenticalRetryCount);

public sealed class OriginReplicationCoordinator : IOriginBatchReceiver, IOriginRangeProvider
{
    private readonly PeerStore store;
    private readonly OriginReplicationOptions options;
    private readonly Func<DateTimeOffset> utcNow;

    public OriginReplicationCoordinator(PeerStore store, OriginReplicationOptions? options = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.options = options ?? OriginReplicationOptions.Default;
        this.options.Validate();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<OriginBatchApplyResult> ApplyAsync(string sourcePeerId, OriginRecordBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePeerId);
        ArgumentNullException.ThrowIfNull(batch);
        var validation = PeerContractValidator.Validate(batch);
        if (!validation.IsValid)
        {
            throw new OriginReplicationValidationException(string.Join(" ", validation.Errors));
        }

        if (batch.Records.Count == 0)
        {
            throw new OriginReplicationValidationException("An incoming origin batch must contain at least one record.");
        }

        if (batch.Records.Count > options.MaximumBatchRecords)
        {
            throw new OriginReplicationLimitException("Origin batch exceeds the configured record limit.");
        }

        var batchBytes = batch.Records.Sum(record => record.CalculateSize());
        if (batchBytes > options.MaximumBatchBytes)
        {
            throw new OriginReplicationLimitException("Origin batch exceeds the configured byte limit.");
        }

        var first = batch.Records[0];
        if (!string.Equals(sourcePeerId, first.OriginPeerId, StringComparison.Ordinal))
        {
            throw new OriginReplicationTrustException("Relayed origin records are not supported; the source must own the origin stream.");
        }

        var trusted = await store.ReadTrustedPeerAsync(sourcePeerId, cancellationToken).ConfigureAwait(false);
        if (trusted is null || trusted.TrustState != PeerTrustState.Active)
        {
            throw new OriginReplicationTrustException("The source peer is not actively trusted.");
        }

        var identity = new PeerPublicIdentity(trusted.PeerId, trusted.PublicKeySpki, trusted.PublicKeyFingerprint);
        foreach (var record in batch.Records)
        {
            if (!OriginRecordCryptography.Verify(record, identity))
            {
                throw new OriginReplicationValidationException("An origin record signature is invalid.");
            }
        }

        var existingRecords = await store.ReadOriginRangeAsync(
            first.OriginPeerId,
            first.OriginStreamId,
            first.Sequence,
            batch.Records.Count,
            cancellationToken).ConfigureAwait(false);
        var existingBySequence = existingRecords
            .Where(item => item.Sequence >= first.Sequence && item.Sequence <= batch.Records[^1].Sequence)
            .ToDictionary(item => item.Sequence);
        foreach (var record in batch.Records)
        {
            if (existingBySequence.TryGetValue(record.Sequence, out var existing) && !Equivalent(existing, record, sourcePeerId))
            {
                throw new PeerPersistenceConflictException("Origin sequence already contains different content.");
            }
        }

        var inserted = 0;
        var retried = 0;
        var receivedAt = RequireUtc(utcNow());
        foreach (var record in batch.Records)
        {
            existingBySequence.TryGetValue(record.Sequence, out var existing);
            var outcome = await store.AppendOriginRecordAsync(new OriginRecordWrite(
                record.OriginPeerId,
                record.OriginStreamId,
                record.Sequence,
                record.SubjectEntityId,
                record.CapabilityId,
                record.SignalId,
                record.RecordBytes.ToByteArray(),
                record.OriginSignature.ToByteArray(),
                record.ObservedAt.ToDateTimeOffset(),
                existing?.ReceivedAt ?? receivedAt,
                existing?.ReceivedFromPeerId ?? sourcePeerId,
                RecordProvenance.Replicated,
                record.Quality.ToString()), cancellationToken).ConfigureAwait(false);
            if (outcome == RecordInsertOutcome.Inserted) inserted++; else retried++;
        }

        var cursorState = await RecalculateCursorAsync(sourcePeerId, first.OriginPeerId, first.OriginStreamId, receivedAt, cancellationToken).ConfigureAwait(false);
        var ack = new OriginAck
        {
            Cursor = new OriginCursor
            {
                OriginPeerId = first.OriginPeerId,
                OriginStreamId = first.OriginStreamId,
                AcceptedThroughSequence = cursorState.Cursor.AcceptedThrough,
                UpdatedAt = Timestamp.FromDateTimeOffset(receivedAt),
            },
        };
        return new OriginBatchApplyResult(ack, cursorState.Gaps, inserted, retried);
    }

    public async Task<OriginRecordBatch> ReadAsync(OriginRangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = PeerContractValidator.Validate(request);
        if (!validation.IsValid)
        {
            throw new OriginReplicationValidationException(string.Join(" ", validation.Errors));
        }

        var recordLimit = (int)Math.Min(request.MaximumRecordCount, (uint)options.MaximumBatchRecords);
        var byteLimit = (int)Math.Min(request.MaximumBatchBytes, (uint)options.MaximumBatchBytes);
        var stored = await store.ReadOriginRangeAsync(request.OriginPeerId, request.OriginStreamId, request.FirstSequence,
            Math.Min(recordLimit + 1, 10_000), cancellationToken).ConfigureAwait(false);
        var response = new OriginRecordBatch { RequestId = request.RequestId };
        foreach (var item in stored)
        {
            if (request.LastSequence != 0 && item.Sequence > request.LastSequence) break;
            if (response.Records.Count == recordLimit) break;
            var record = ToContract(item);
            if (response.CalculateSize() + record.CalculateSize() > byteLimit)
            {
                if (response.Records.Count == 0)
                {
                    throw new OriginReplicationLimitException("The first origin record exceeds the requested byte limit.");
                }

                break;
            }

            response.Records.Add(record);
        }

        var lastReturned = response.Records.Count == 0 ? request.FirstSequence - 1 : response.Records[^1].Sequence;
        response.HasMore = stored.Any(item => item.Sequence > lastReturned && (request.LastSequence == 0 || item.Sequence <= request.LastSequence));
        return response;
    }

    private async Task<(ReplicationCursor Cursor, IReadOnlyList<ReplicationGap> Gaps)> RecalculateCursorAsync(
        string sourcePeerId, string originPeerId, string originStreamId, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        var prior = await store.ReadReplicationCursorAsync(sourcePeerId, originPeerId, originStreamId, cancellationToken).ConfigureAwait(false);
        var accepted = prior.Cursor?.AcceptedThrough ?? 0;
        var firstSequence = checked(accepted + 1);
        var records = await store.ReadOriginRangeAsync(originPeerId, originStreamId, firstSequence, options.CursorScanLimit, cancellationToken).ConfigureAwait(false);
        var gaps = new List<ReplicationGap>();
        var expected = firstSequence;
        var foundGap = false;
        foreach (var record in records)
        {
            if (record.Sequence > expected)
            {
                gaps.Add(new ReplicationGap(expected, record.Sequence - 1));
                foundGap = true;
            }

            if (!foundGap && record.Sequence == expected) accepted = record.Sequence;
            expected = checked(record.Sequence + 1);
        }

        var cursor = new ReplicationCursor(sourcePeerId, originPeerId, originStreamId, accepted, "raw", updatedAt);
        await store.UpdateReplicationCursorAsync(cursor, gaps, cancellationToken).ConfigureAwait(false);
        return (cursor, gaps);
    }

    private static OriginRecord ToContract(StoredOriginRecord item)
    {
        if (!System.Enum.TryParse<ObservationQuality>(item.Quality, out var quality) || quality == ObservationQuality.Unspecified)
        {
            throw new OriginReplicationValidationException("A stored origin record has an unsupported observation quality.");
        }

        return new OriginRecord
        {
            OriginPeerId = item.OriginPeerId,
            OriginStreamId = item.OriginStreamId,
            Sequence = item.Sequence,
            SubjectEntityId = item.SubjectEntityId,
            CapabilityId = item.AdapterInstanceId,
            SignalId = item.SignalId,
            RecordBytes = Google.Protobuf.ByteString.CopyFrom(item.RecordBytes),
            ContentSha256 = Google.Protobuf.ByteString.CopyFrom(item.ContentHash),
            OriginSignature = Google.Protobuf.ByteString.CopyFrom(item.Signature),
            ObservedAt = Timestamp.FromDateTimeOffset(item.ObservedAt),
            Quality = quality,
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
        };
    }

    private static bool Equivalent(StoredOriginRecord stored, OriginRecord record, string sourcePeerId) =>
        stored.OriginPeerId == record.OriginPeerId &&
        stored.OriginStreamId == record.OriginStreamId &&
        stored.Sequence == record.Sequence &&
        stored.SubjectEntityId == record.SubjectEntityId &&
        stored.AdapterInstanceId == record.CapabilityId &&
        stored.SignalId == record.SignalId &&
        stored.RecordBytes.AsSpan().SequenceEqual(record.RecordBytes.Span) &&
        stored.ContentHash.AsSpan().SequenceEqual(record.ContentSha256.Span) &&
        stored.Signature.AsSpan().SequenceEqual(record.OriginSignature.Span) &&
        stored.ObservedAt == record.ObservedAt.ToDateTimeOffset() &&
        stored.ReceivedFromPeerId == sourcePeerId &&
        stored.Provenance == RecordProvenance.Replicated &&
        stored.Quality == record.Quality.ToString();

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero ? value : throw new InvalidOperationException("The replication clock must return UTC.");
}
