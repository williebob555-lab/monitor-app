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

        if (batch.Records.Count == 0 && batch.PrunedRange is null)
        {
            throw new OriginReplicationValidationException("An incoming origin batch must contain at least one record.");
        }

        if (batch.Records.Count == 0)
        {
            // Nothing but the sender's statement that a range is gone.
            return new OriginBatchApplyResult(await ApplyPrunedRangeAsync(sourcePeerId, batch.PrunedRange!, cancellationToken).ConfigureAwait(false), [], 0, 0);
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
        var source = await store.ReadTrustedPeerAsync(sourcePeerId, cancellationToken).ConfigureAwait(false);
        if (source is null || source.TrustState != PeerTrustState.Active)
        {
            throw new OriginReplicationTrustException("The source peer is not actively trusted.");
        }

        // A relayed batch (another trusted device forwarding the origin's records) is accepted only for an origin that
        // is actively trusted here, and every record must carry the origin's own signature: the relay cannot alter,
        // invent or reorder them. Only the origin itself may declare part of its stream pruned.
        var relayed = !string.Equals(sourcePeerId, first.OriginPeerId, StringComparison.Ordinal);
        var trusted = source;
        if (relayed)
        {
            if (batch.PrunedRange is not null)
            {
                throw new OriginReplicationTrustException("Only the origin may declare its records pruned.");
            }

            trusted = await store.ReadTrustedPeerAsync(first.OriginPeerId, cancellationToken).ConfigureAwait(false);
            if (trusted is null || trusted.TrustState != PeerTrustState.Active)
            {
                throw new OriginReplicationTrustException("The origin of relayed records is not actively trusted.");
            }
        }

        var identity = new PeerPublicIdentity(trusted.PeerId, trusted.PublicKeySpki, trusted.PublicKeyFingerprint);
        foreach (var record in batch.Records)
        {
            if (!OriginRecordCryptography.Verify(record, identity))
            {
                throw new OriginReplicationValidationException("An origin record signature is invalid.");
            }
        }

        if (batch.PrunedRange is { } pruned)
        {
            if (pruned.OriginStreamId != first.OriginStreamId || pruned.LastSequence >= first.Sequence)
            {
                throw new OriginReplicationValidationException("The pruned range does not precede the batch on the same stream.");
            }

            await ApplyPrunedRangeAsync(sourcePeerId, pruned, cancellationToken).ConfigureAwait(false);
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
            if (existingBySequence.TryGetValue(record.Sequence, out var existing) && !Equivalent(existing, record))
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

    /// <summary>
    /// Reads a range to push to a replica. Unlike <see cref="ReadAsync"/>, a range the local retention pruned is not
    /// an error: the batch starts after it and carries the pruned range, so the replica records it as intentionally
    /// absent instead of waiting for records that no longer exist.
    /// </summary>
    public async Task<OriginRecordBatch> ReadForPushAsync(OriginRangeRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OriginRangePrunedException pruned) when (pruned.Range is { } range)
        {
            var resume = request.Clone();
            resume.FirstSequence = checked(range.LastSequence + 1);
            var contractRange = new PrunedRange
            {
                OriginPeerId = range.OriginPeerId,
                OriginStreamId = range.OriginStreamId,
                FirstSequence = range.FirstSequence,
                LastSequence = range.LastSequence,
            };
            if (resume.LastSequence != 0 && resume.FirstSequence > resume.LastSequence)
            {
                return new OriginRecordBatch { RequestId = request.RequestId, PrunedRange = contractRange };
            }

            var batch = await ReadAsync(resume, cancellationToken).ConfigureAwait(false);
            batch.PrunedRange = contractRange;
            return batch;
        }
    }

    /// <summary>
    /// Reads another device's records that this device holds, to forward them to a peer that cannot reach that device.
    /// Ranges pruned here are skipped (only the origin may declare its stream pruned), the batch stops at the first
    /// sequence this device lacks, and an empty batch means nothing more is held from the requested sequence.
    /// </summary>
    public async Task<OriginRecordBatch> ReadRelayAsync(OriginRangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resume = request.Clone();
        for (var skipped = 0; skipped < 64; skipped++)
        {
            OriginRecordBatch batch;
            try
            {
                batch = await ReadAsync(resume, cancellationToken).ConfigureAwait(false);
            }
            catch (OriginRangePrunedException pruned) when (pruned.Range is { } range)
            {
                resume.FirstSequence = checked(range.LastSequence + 1);
                if (resume.LastSequence != 0 && resume.FirstSequence > resume.LastSequence) break;
                continue;
            }

            for (var index = 1; index < batch.Records.Count; index++)
            {
                if (batch.Records[index].Sequence == batch.Records[index - 1].Sequence + 1) continue;
                while (batch.Records.Count > index) batch.Records.RemoveAt(batch.Records.Count - 1);
                batch.HasMore = true;
                break;
            }

            if (batch.Records.Count == 0) batch.HasMore = false;
            batch.PrunedRange = null;
            return batch;
        }

        return new OriginRecordBatch { RequestId = request.RequestId };
    }

    public async Task<OriginRecordBatch> ReadAsync(OriginRangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = PeerContractValidator.Validate(request);
        if (!validation.IsValid)
        {
            throw new OriginReplicationValidationException(string.Join(" ", validation.Errors));
        }

        var pruned = await store.ReadPrunedRangeContainingAsync(request.OriginPeerId, request.OriginStreamId, request.FirstSequence, cancellationToken).ConfigureAwait(false);
        if (pruned is not null)
        {
            throw new OriginRangePrunedException(request.FirstSequence, pruned);
        }

        // A batch never spans a pruned range: it stops before it so the next request lands on the explicit outcome.
        var nextPruned = await store.ReadNextPrunedRangeAfterAsync(request.OriginPeerId, request.OriginStreamId, request.FirstSequence, cancellationToken).ConfigureAwait(false);
        var recordLimit = (int)Math.Min(request.MaximumRecordCount, (uint)options.MaximumBatchRecords);
        var byteLimit = (int)Math.Min(request.MaximumBatchBytes, (uint)options.MaximumBatchBytes);
        var stored = await store.ReadOriginRangeAsync(request.OriginPeerId, request.OriginStreamId, request.FirstSequence,
            Math.Min(recordLimit + 1, 10_000), cancellationToken).ConfigureAwait(false);
        var response = new OriginRecordBatch { RequestId = request.RequestId };
        foreach (var item in stored)
        {
            if (request.LastSequence != 0 && item.Sequence > request.LastSequence) break;
            if (nextPruned is not null && item.Sequence >= nextPruned.FirstSequence) break;
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
        response.HasMore = stored.Any(item => item.Sequence > lastReturned && (request.LastSequence == 0 || item.Sequence <= request.LastSequence)) ||
            (nextPruned is not null && nextPruned.FirstSequence > lastReturned && (request.LastSequence == 0 || nextPruned.FirstSequence <= request.LastSequence));
        return response;
    }

    /// <summary>
    /// The origin reported that a range was removed by its retention. Records the range durably as intentionally
    /// absent, so the cursor advances past it instead of leaving a perpetual gap, and returns the new acknowledgement.
    /// Only an actively trusted origin may declare its own stream pruned, and the range must cover the sequence this
    /// replica asked for next (anything else is a protocol violation, not a prune).
    /// </summary>
    public async Task<OriginAck> ApplyPrunedRangeAsync(string sourcePeerId, PrunedRange range, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePeerId);
        ArgumentNullException.ThrowIfNull(range);
        if (!string.Equals(sourcePeerId, range.OriginPeerId, StringComparison.Ordinal))
        {
            throw new OriginReplicationTrustException("Relayed pruned ranges are not supported; the source must own the origin stream.");
        }

        if (string.IsNullOrWhiteSpace(range.OriginStreamId) || range.FirstSequence == 0 || range.LastSequence < range.FirstSequence)
        {
            throw new OriginReplicationValidationException("The pruned range is invalid.");
        }

        var trusted = await store.ReadTrustedPeerAsync(sourcePeerId, cancellationToken).ConfigureAwait(false);
        if (trusted is null || trusted.TrustState != PeerTrustState.Active)
        {
            throw new OriginReplicationTrustException("The source peer is not actively trusted.");
        }

        var prior = await store.ReadReplicationCursorAsync(sourcePeerId, range.OriginPeerId, range.OriginStreamId, cancellationToken).ConfigureAwait(false);
        var next = checked((prior.Cursor?.AcceptedThrough ?? 0) + 1);
        if (range.FirstSequence > next)
        {
            throw new OriginReplicationValidationException("The pruned range does not start at the next expected sequence.");
        }

        var receivedAt = RequireUtc(utcNow());
        await store.RecordPrunedRangeAsync(
            new PrunedOriginRange(range.OriginPeerId, range.OriginStreamId, range.FirstSequence, range.LastSequence, receivedAt),
            cancellationToken).ConfigureAwait(false);
        var cursorState = await RecalculateCursorAsync(sourcePeerId, range.OriginPeerId, range.OriginStreamId, receivedAt, cancellationToken).ConfigureAwait(false);
        return new OriginAck
        {
            Cursor = new OriginCursor
            {
                OriginPeerId = range.OriginPeerId,
                OriginStreamId = range.OriginStreamId,
                AcceptedThroughSequence = cursorState.Cursor.AcceptedThrough,
                UpdatedAt = Timestamp.FromDateTimeOffset(receivedAt),
            },
        };
    }

    // Sequences inside a pruned (intentionally absent) range count as present for cursor purposes.
    private async Task<(ReplicationCursor Cursor, IReadOnlyList<ReplicationGap> Gaps)> RecalculateCursorAsync(
        string sourcePeerId, string originPeerId, string originStreamId, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        var prior = await store.ReadReplicationCursorAsync(sourcePeerId, originPeerId, originStreamId, cancellationToken).ConfigureAwait(false);
        // Records are stored once whichever peer delivered them, so this source's cursor starts from the furthest any
        // source reached: a relay's cursor then never rescans what the origin (or another relay) already delivered.
        var held = await store.ReadHeldThroughAsync(originPeerId, originStreamId, cancellationToken).ConfigureAwait(false);
        var accepted = Math.Max(prior.Cursor?.AcceptedThrough ?? 0, held);
        var firstSequence = checked(accepted + 1);
        var records = await store.ReadOriginRangeAsync(originPeerId, originStreamId, firstSequence, options.CursorScanLimit, cancellationToken).ConfigureAwait(false);
        var absent = await store.ReadPrunedRangesFromAsync(originPeerId, originStreamId, firstSequence, cancellationToken).ConfigureAwait(false);
        var gaps = new List<ReplicationGap>();
        var expected = SkipAbsent(firstSequence, absent);
        var foundGap = false;
        if (expected > firstSequence) accepted = expected - 1;
        foreach (var record in records)
        {
            if (record.Sequence < expected) continue; // stored inside an absent range (or already accepted)
            if (record.Sequence > expected)
            {
                foreach (var gap in SubtractAbsent(expected, record.Sequence - 1, absent))
                {
                    gaps.Add(gap);
                    foundGap = true;
                }
            }

            if (!foundGap) accepted = record.Sequence;
            expected = checked(record.Sequence + 1);
            var skipped = SkipAbsent(expected, absent);
            if (skipped > expected && !foundGap) accepted = skipped - 1;
            expected = skipped;
        }

        var cursor = new ReplicationCursor(sourcePeerId, originPeerId, originStreamId, accepted, "raw", updatedAt);
        await store.UpdateReplicationCursorAsync(cursor, gaps, cancellationToken).ConfigureAwait(false);
        return (cursor, gaps);
    }

    private static ulong SkipAbsent(ulong sequence, IReadOnlyList<PrunedOriginRange> absent)
    {
        foreach (var range in absent)
        {
            if (range.FirstSequence <= sequence && range.LastSequence >= sequence) sequence = checked(range.LastSequence + 1);
        }

        return sequence;
    }

    private static IEnumerable<ReplicationGap> SubtractAbsent(ulong first, ulong last, IReadOnlyList<PrunedOriginRange> absent)
    {
        var cursor = first;
        foreach (var range in absent)
        {
            if (range.LastSequence < cursor) continue;
            if (range.FirstSequence > last) break;
            if (range.FirstSequence > cursor) yield return new ReplicationGap(cursor, range.FirstSequence - 1);
            cursor = checked(range.LastSequence + 1);
            if (cursor > last) yield break;
        }

        if (cursor <= last) yield return new ReplicationGap(cursor, last);
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

    // Which peer delivered a record does not matter: the same signed record may arrive from the origin and from relays.
    private static bool Equivalent(StoredOriginRecord stored, OriginRecord record) =>
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
        stored.Provenance == RecordProvenance.Replicated &&
        stored.Quality == record.Quality.ToString();

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero ? value : throw new InvalidOperationException("The replication clock must return UTC.");
}
