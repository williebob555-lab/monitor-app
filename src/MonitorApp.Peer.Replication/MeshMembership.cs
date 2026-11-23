using System.Security.Cryptography;
using Google.Protobuf;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Replication;

/// <summary>A trust change the mesh made on this peer, for history and notifications.</summary>
public sealed record MeshTrustChange(string PeerId, string? DisplayName, bool Admitted, string ByPeerId, string? ByDisplayName);

/// <summary>Outcome of applying statements received from a peer.</summary>
public sealed record MeshApplyResult(int Stored, int Ignored, IReadOnlyList<MeshTrustChange> Changes);

/// <summary>
/// Mesh membership (T080, owner decision 2026-11-23 "add once, trusted everywhere").
///
/// Every peer keeps a set of signed statements: "I trust device X with key K" (admit) and "I removed device X with key K"
/// (remove). This peer issues statements from its own trust table (direct pairings and local revocations), stores
/// statements it receives only when their issuer is already reachable from it, and swaps the set in the authenticated
/// capability exchange of every session. Trust is then derived from the statement set alone, deterministically:
/// <list type="number">
/// <item>Reachable: (peer id, key) pairs reachable from this peer over admit statements with valid signatures,
/// ignoring removals.</item>
/// <item>Removed: subjects of remove statements whose issuer is reachable. Removal is final for that key and wins over
/// any admission, so a removed device stays removed everywhere whoever re-admits it, and a removed device cannot stay
/// trusted by removing the others first (its removals do remove them, but it is removed too).</item>
/// <item>Trusted: reachable from this peer over admit statements whose issuer and subject are not removed. Admissions
/// by a removed device stop counting.</item>
/// </list>
/// Trust rows the mesh creates are marked, so the mesh can later revoke them when their admitter is removed; it never
/// rebinds a peer id to another key, never revives a revoked row, and never touches control grants (deny by default).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The gate never allocates a wait handle (only WaitAsync is used), so it holds nothing to release; the membership lives as long as its peer.")]
public sealed class MeshMembership
{
    /// <summary>Statements dated further ahead than this are refused (a removed device must not pre-date admissions).</summary>
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(10);

    /// <summary>Most statements accepted from one exchange.</summary>
    public const int MaximumStatementsPerExchange = 512;

    private readonly PeerStore store;
    private readonly PeerPublicIdentity local;
    private readonly IPeerIdentityKey localKey;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long version;

    public MeshMembership(PeerStore store, PeerPublicIdentity local, IPeerIdentityKey localKey, Func<DateTimeOffset>? utcNow = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.local = local ?? throw new ArgumentNullException(nameof(local));
        this.localKey = localKey ?? throw new ArgumentNullException(nameof(localKey));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Where this peer accepts sessions, sent to every peer it talks to (empty on phones).</summary>
    public IReadOnlyList<MeshLocator> SelfLocators { get; init; } = [];

    /// <summary>
    /// Records the locators an authenticated, actively trusted peer declared for itself. A peer can only point others at
    /// itself, and a wrong address only makes its own sessions fail (the key is pinned).
    /// </summary>
    public async Task<bool> ApplySelfLocatorsAsync(string fromPeerId, IEnumerable<MeshLocator> locators, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromPeerId);
        ArgumentNullException.ThrowIfNull(locators);
        var declared = locators.Take(MeshStatementCryptography.MaximumLocators + 1).ToArray();
        if (declared.Length is 0 or > MeshStatementCryptography.MaximumLocators ||
            declared.Any(item => string.IsNullOrWhiteSpace(item.Host) || item.Host.Length > MeshStatementCryptography.MaximumHostLength ||
                                 item.Port is < 1 or > 65535 || !System.Net.IPAddress.TryParse(item.Host, out _)))
        {
            return false;
        }

        var sender = await store.ReadTrustedPeerAsync(fromPeerId, cancellationToken).ConfigureAwait(false);
        if (sender is null || sender.TrustState != PeerTrustState.Active) return false;
        var known = await store.ReadPeerLocatorsAsync(fromPeerId, cancellationToken).ConfigureAwait(false);
        var now = utcNow();
        var added = false;
        foreach (var locator in declared)
        {
            if (known.Any(item => item.Host == locator.Host && item.Port == (int)locator.Port)) continue;
            await store.UpsertPeerLocatorAsync(new PeerLocator(fromPeerId, locator.Host, (int)locator.Port, now, null), cancellationToken).ConfigureAwait(false);
            added = true;
        }

        return added;
    }

    /// <summary>Increases whenever the stored statements or the trust derived from them change in this process.</summary>
    public long Version => Interlocked.Read(ref version);

    /// <summary>True once a reachable member removed this device from the mesh.</summary>
    public bool LocalRemoved { get; private set; }

    /// <summary>Raised after trust changed (outside the internal lock; handlers must not throw).</summary>
    public event Action<IReadOnlyList<MeshTrustChange>>? TrustChanged;

    /// <summary>Issues this peer's own statements if its trust table changed, and returns every stored statement to send.</summary>
    public async Task<IReadOnlyList<MeshStatement>> GetStatementsForExchangeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var stored = await store.ReadMeshStatementsAsync(cancellationToken).ConfigureAwait(false);
        return stored
            .OrderByDescending(item => item.IssuedAt)
            .Take(MaximumStatementsPerExchange)
            .Select(item => MeshStatement.Parser.ParseFrom(item.StatementBytes))
            .ToArray();
    }

    /// <summary>Re-issues this peer's own statements from its trust table and re-derives trust. Safe to call often.</summary>
    public async Task<IReadOnlyList<MeshTrustChange>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MeshTrustChange> changes;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var issued = await IssueLocalStatementsAsync(cancellationToken).ConfigureAwait(false);
            changes = await DeriveAndApplyAsync(cancellationToken).ConfigureAwait(false);
            if (issued > 0 || changes.Count > 0) Interlocked.Increment(ref version);
        }
        finally
        {
            gate.Release();
        }

        if (changes.Count > 0) TrustChanged?.Invoke(changes);
        return changes;
    }

    /// <summary>
    /// Applies statements an authenticated, actively trusted peer sent. Statements whose issuer is not reachable from
    /// this peer, whose signature does not verify, or that are dated in the future are dropped, never stored.
    /// </summary>
    public async Task<MeshApplyResult> ApplyAsync(string fromPeerId, IEnumerable<MeshStatement> statements, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromPeerId);
        ArgumentNullException.ThrowIfNull(statements);
        var incoming = statements.Take(MaximumStatementsPerExchange + 1).ToList();
        if (incoming.Count > MaximumStatementsPerExchange)
            throw new OriginReplicationLimitException("Too many mesh statements in one exchange.");

        var stored = 0;
        var ignored = 0;
        IReadOnlyList<MeshTrustChange> changes;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sender = await store.ReadTrustedPeerAsync(fromPeerId, cancellationToken).ConfigureAwait(false);
            if (sender is null || sender.TrustState != PeerTrustState.Active)
                throw new OriginReplicationTrustException("Mesh statements are accepted only from an actively trusted peer.");

            var now = utcNow();
            var candidates = new List<MeshStatement>();
            foreach (var statement in incoming)
            {
                if (MeshStatementCryptography.ValidateShape(statement) is not null ||
                    statement.IssuedAt.ToDateTimeOffset() > now + MaximumClockSkew)
                {
                    ignored++;
                    continue;
                }

                candidates.Add(statement);
            }

            // Issuers may be introduced by other statements in the same exchange: accept in rounds until nothing new verifies.
            var known = (await ReadVerifiedAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var progressed = true;
            while (progressed && candidates.Count > 0)
            {
                progressed = false;
                var reachable = Reachable(known);
                foreach (var statement in candidates.ToArray())
                {
                    if (!reachable.TryGetValue(IssuerNode(statement), out var issuer) || !MeshStatementCryptography.Verify(statement, issuer))
                        continue;

                    candidates.Remove(statement);
                    var subject = MeshStatementCryptography.Subject(statement);
                    var outcome = await store.UpsertMeshStatementAsync(new StoredMeshStatement(
                        statement.StatementId,
                        statement.Kind == MeshStatementKind.Admit ? StoredMeshStatementKind.Admit : StoredMeshStatementKind.Remove,
                        statement.IssuerPeerId,
                        statement.SubjectPeerId,
                        subject.PublicKeyFingerprint,
                        statement.ToByteArray(),
                        statement.IssuedAt.ToDateTimeOffset(),
                        now,
                        fromPeerId), cancellationToken).ConfigureAwait(false);
                    if (outcome == MeshStatementWriteOutcome.Unchanged)
                    {
                        ignored++;
                        continue;
                    }

                    stored++;
                    known.RemoveAll(item => item.StatementId == statement.StatementId ||
                        (item.Kind == statement.Kind && item.IssuerPeerId == statement.IssuerPeerId &&
                         Node(item.SubjectPeerId, MeshStatementCryptography.Subject(item).PublicKeyFingerprint) == Node(statement.SubjectPeerId, subject.PublicKeyFingerprint)));
                    known.Add(statement);
                    progressed = true;
                }
            }

            ignored += candidates.Count;
            changes = await DeriveAndApplyAsync(cancellationToken).ConfigureAwait(false);
            if (stored > 0 || changes.Count > 0) Interlocked.Increment(ref version);
        }
        finally
        {
            gate.Release();
        }

        if (changes.Count > 0) TrustChanged?.Invoke(changes);
        return new MeshApplyResult(stored, ignored, changes);
    }

    /// <summary>Pure derivation over a statement set, exposed for tests and the mesh screen.</summary>
    public MeshDerivation Derive(IReadOnlyList<MeshStatement> statements) => Derive(local, statements);

    public static MeshDerivation Derive(PeerPublicIdentity root, IReadOnlyList<MeshStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(statements);
        var valid = statements.Where(item => MeshStatementCryptography.ValidateShape(item) is null).ToArray();
        var reachable = Reachable(root, valid, removed: null);
        var removed = new Dictionary<string, (PeerPublicIdentity Subject, MeshStatement By)>(StringComparer.Ordinal);
        foreach (var statement in valid.Where(item => item.Kind == MeshStatementKind.Remove).OrderBy(item => item.StatementId, StringComparer.Ordinal))
        {
            if (!reachable.TryGetValue(IssuerNode(statement), out var issuer) || !MeshStatementCryptography.Verify(statement, issuer)) continue;
            var subject = MeshStatementCryptography.Subject(statement);
            removed.TryAdd(Node(subject), (subject, statement));
        }

        var rootNode = Node(root);
        removed.Remove(rootNode, out var localRemoval);
        var trusted = Reachable(root, valid, removed.Keys.ToHashSet(StringComparer.Ordinal), out var admittedBy);
        trusted.Remove(rootNode);
        return new MeshDerivation(
            trusted.Values.ToArray(),
            admittedBy,
            removed.Values.Select(item => (item.Subject, item.By)).ToArray(),
            localRemoval.By);
    }

    private async Task<int> IssueLocalStatementsAsync(CancellationToken cancellationToken)
    {
        var peers = await store.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false);
        var marks = (await store.ReadMeshTrustMarksAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(item => item.PeerId, StringComparer.Ordinal);
        var mine = (await store.ReadMeshStatementsAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => item.IssuerPeerId == local.PeerId)
            .ToArray();
        var issued = 0;
        var now = utcNow();
        foreach (var peer in peers)
        {
            marks.TryGetValue(peer.PeerId, out var mark);
            var subject = new PeerPublicIdentity(peer.PeerId, peer.PublicKeySpki, peer.PublicKeyFingerprint);
            if (peer.TrustState == PeerTrustState.Active && mark is not { AdmittedViaMesh: true })
            {
                // A direct pairing: vouch for it, with where it can be reached (refreshed when that changes).
                var locators = (await store.ReadPeerLocatorsAsync(peer.PeerId, cancellationToken).ConfigureAwait(false))
                    .OrderByDescending(item => item.LastConnectedAt ?? DateTimeOffset.MinValue)
                    .ThenBy(item => item.Host, StringComparer.Ordinal)
                    .ThenBy(item => item.Port)
                    .Take(MeshStatementCryptography.MaximumLocators)
                    .OrderBy(item => item.Host, StringComparer.Ordinal)
                    .ThenBy(item => item.Port)
                    .Select(item => new MeshLocator { Host = item.Host, Port = (uint)item.Port })
                    .ToArray();
                var existing = Latest(mine, StoredMeshStatementKind.Admit, peer);
                if (existing is not null)
                {
                    var parsed = MeshStatement.Parser.ParseFrom(existing.StatementBytes);
                    if (parsed.SubjectLocators.SequenceEqual(locators) && parsed.SubjectDisplayName == (peer.DisplayName ?? string.Empty)) continue;
                }

                await StoreOwnAsync(MeshStatementCryptography.CreateSigned(localKey, local, MeshStatementKind.Admit, subject,
                    peer.DisplayName, locators, Later(now, existing)), cancellationToken).ConfigureAwait(false);
                issued++;
            }
            else if (peer.TrustState == PeerTrustState.Revoked && mark is not { RemovedViaMesh: true } &&
                     Latest(mine, StoredMeshStatementKind.Remove, peer) is null)
            {
                // Revoked here by the owner: remove it everywhere.
                await StoreOwnAsync(MeshStatementCryptography.CreateSigned(localKey, local, MeshStatementKind.Remove, subject,
                    peer.DisplayName, [], now), cancellationToken).ConfigureAwait(false);
                issued++;
            }
        }

        return issued;
    }

    private async Task StoreOwnAsync(MeshStatement statement, CancellationToken cancellationToken)
    {
        var subject = MeshStatementCryptography.Subject(statement);
        await store.UpsertMeshStatementAsync(new StoredMeshStatement(
            statement.StatementId,
            statement.Kind == MeshStatementKind.Admit ? StoredMeshStatementKind.Admit : StoredMeshStatementKind.Remove,
            statement.IssuerPeerId,
            statement.SubjectPeerId,
            subject.PublicKeyFingerprint,
            statement.ToByteArray(),
            statement.IssuedAt.ToDateTimeOffset(),
            utcNow(),
            null), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MeshTrustChange>> DeriveAndApplyAsync(CancellationToken cancellationToken)
    {
        var statements = await ReadVerifiedAsync(cancellationToken).ConfigureAwait(false);
        var derivation = Derive(local, statements);
        LocalRemoved = derivation.LocalRemovedBy is not null;
        var peers = (await store.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(item => item.PeerId, StringComparer.Ordinal);
        var marks = (await store.ReadMeshTrustMarksAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(item => item.PeerId, StringComparer.Ordinal);
        var names = peers.Values.ToDictionary(item => item.PeerId, item => item.DisplayName, StringComparer.Ordinal);
        foreach (var statement in statements.Where(item => item.Kind == MeshStatementKind.Admit && item.SubjectDisplayName.Length > 0))
            names.TryAdd(statement.SubjectPeerId, statement.SubjectDisplayName);
        var now = utcNow();
        var changes = new List<MeshTrustChange>();

        foreach (var (subject, by) in derivation.Removed)
        {
            if (peers.TryGetValue(subject.PeerId, out var peer) && peer.TrustState == PeerTrustState.Active &&
                CryptographicOperations.FixedTimeEquals(peer.PublicKeyFingerprint, subject.PublicKeyFingerprint) &&
                await store.RemovePeerViaMeshAsync(subject.PeerId, subject.PublicKeyFingerprint, by.IssuerPeerId, now, cancellationToken).ConfigureAwait(false))
            {
                changes.Add(new MeshTrustChange(subject.PeerId, peer.DisplayName, false, by.IssuerPeerId, Name(names, by.IssuerPeerId)));
            }
        }

        var trustedNodes = derivation.Trusted.Select(Node).ToHashSet(StringComparer.Ordinal);
        foreach (var subject in derivation.Trusted)
        {
            if (peers.ContainsKey(subject.PeerId)) continue; // Existing rows are never rebound or revived.
            var admission = derivation.AdmittedBy[Node(subject)];
            var locators = admission.SubjectLocators
                .Select(item => new PeerLocator(subject.PeerId, item.Host, (int)item.Port, now, null))
                .ToArray();
            var name = admission.SubjectDisplayName.Length > 0 ? admission.SubjectDisplayName : null;
            if (await store.AdmitPeerViaMeshAsync(new TrustedPeer(subject.PeerId, subject.PublicKeySpki, subject.PublicKeyFingerprint,
                    name, PeerTrustState.Active, now, null), admission.IssuerPeerId, locators, cancellationToken).ConfigureAwait(false))
            {
                changes.Add(new MeshTrustChange(subject.PeerId, name, true, admission.IssuerPeerId, Name(names, admission.IssuerPeerId)));
            }
        }

        // A device the mesh admitted loses trust when nothing that is still trusted vouches for it any more.
        foreach (var mark in marks.Values.Where(item => item.AdmittedViaMesh))
        {
            if (!peers.TryGetValue(mark.PeerId, out var peer) || peer.TrustState != PeerTrustState.Active) continue;
            if (trustedNodes.Contains(Node(peer.PeerId, peer.PublicKeyFingerprint))) continue;
            var removal = derivation.Removed.FirstOrDefault(item => item.Subject.PeerId == peer.PeerId).By;
            var by = removal?.IssuerPeerId ?? mark.AdmittedBy ?? local.PeerId;
            if (await store.RemovePeerViaMeshAsync(peer.PeerId, peer.PublicKeyFingerprint, by, now, cancellationToken).ConfigureAwait(false))
                changes.Add(new MeshTrustChange(peer.PeerId, peer.DisplayName, false, by, Name(names, by)));
        }

        foreach (var change in changes)
        {
            var reason = change.Admitted
                ? $"Joined the mesh (added by {change.ByDisplayName ?? change.ByPeerId})"
                : $"Removed from the mesh by {change.ByDisplayName ?? change.ByPeerId}";
            await store.RecordPeerStateAsync(change.PeerId, change.Admitted ? PeerConnectivityState.Paired : PeerConnectivityState.Revoked,
                now, reason, cancellationToken).ConfigureAwait(false);
        }

        return changes;
    }

    private async Task<IReadOnlyList<MeshStatement>> ReadVerifiedAsync(CancellationToken cancellationToken) =>
        (await store.ReadMeshStatementsAsync(cancellationToken).ConfigureAwait(false))
            .Select(item => MeshStatement.Parser.ParseFrom(item.StatementBytes))
            .ToArray();

    private Dictionary<string, PeerPublicIdentity> Reachable(IReadOnlyList<MeshStatement> statements) =>
        Reachable(local, statements.Where(item => MeshStatementCryptography.ValidateShape(item) is null).ToArray(), removed: null);

    private static Dictionary<string, PeerPublicIdentity> Reachable(PeerPublicIdentity root, IReadOnlyList<MeshStatement> statements, ISet<string>? removed) =>
        Reachable(root, statements, removed ?? new HashSet<string>(StringComparer.Ordinal), out _);

    /// <summary>Breadth-first over admit statements from <paramref name="root"/>, skipping removed issuers and subjects.</summary>
    private static Dictionary<string, PeerPublicIdentity> Reachable(PeerPublicIdentity root, IReadOnlyList<MeshStatement> statements,
        ISet<string> removed, out Dictionary<string, MeshStatement> admittedBy)
    {
        var reached = new Dictionary<string, PeerPublicIdentity>(StringComparer.Ordinal) { [Node(root)] = root };
        admittedBy = new Dictionary<string, MeshStatement>(StringComparer.Ordinal);
        var byIssuer = statements
            .Where(item => item.Kind == MeshStatementKind.Admit)
            .OrderByDescending(item => item.IssuedAt.ToDateTimeOffset())
            .ThenBy(item => item.StatementId, StringComparer.Ordinal)
            .GroupBy(IssuerNode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(Node(root));
        while (queue.Count > 0)
        {
            var issuerNode = queue.Dequeue();
            if (removed.Contains(issuerNode) || !byIssuer.TryGetValue(issuerNode, out var admits)) continue;
            var issuer = reached[issuerNode];
            foreach (var statement in admits)
            {
                PeerPublicIdentity subject;
                try
                {
                    subject = MeshStatementCryptography.Subject(statement);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var subjectNode = Node(subject);
                if (reached.ContainsKey(subjectNode) || removed.Contains(subjectNode) || !MeshStatementCryptography.Verify(statement, issuer)) continue;
                reached[subjectNode] = subject;
                admittedBy[subjectNode] = statement;
                queue.Enqueue(subjectNode);
            }
        }

        return reached;
    }

    private static StoredMeshStatement? Latest(IEnumerable<StoredMeshStatement> statements, StoredMeshStatementKind kind, TrustedPeer peer) =>
        statements
            .Where(item => item.Kind == kind && item.SubjectPeerId == peer.PeerId && item.SubjectFingerprint.AsSpan().SequenceEqual(peer.PublicKeyFingerprint))
            .MaxBy(item => item.IssuedAt);

    private static DateTimeOffset Later(DateTimeOffset now, StoredMeshStatement? existing) =>
        existing is not null && existing.IssuedAt >= now ? existing.IssuedAt.AddMilliseconds(1) : now;

    private static string? Name(Dictionary<string, string?> names, string peerId) =>
        names.TryGetValue(peerId, out var name) ? name : null;

    private static string IssuerNode(MeshStatement statement) =>
        statement.IssuerPeerId + "|" + Convert.ToHexString(statement.IssuerPublicKeySha256.Span);

    private static string Node(PeerPublicIdentity identity) => Node(identity.PeerId, identity.PublicKeyFingerprint);

    private static string Node(string peerId, byte[] fingerprint) => peerId + "|" + Convert.ToHexString(fingerprint);
}

/// <summary>Trust derived from a statement set: who is trusted (and which admission reached them first), who is removed.</summary>
public sealed record MeshDerivation(
    IReadOnlyList<PeerPublicIdentity> Trusted,
    IReadOnlyDictionary<string, MeshStatement> AdmittedBy,
    IReadOnlyList<(PeerPublicIdentity Subject, MeshStatement By)> Removed,
    MeshStatement? LocalRemovedBy);
