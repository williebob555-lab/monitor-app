using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Agent.Core;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Collection;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Collection.Lan;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.LocalApi;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using MonitorApp.Peer.Service;

namespace MonitorApp.Peer.Node;

/// <summary>Tunables for live behaviour; defaults suit production, tests shorten them.</summary>
/// <param name="CollectSelfLan">Collect this host's own interfaces on a schedule (default on): link observations through
/// the ingestion path plus the self-observed LAN entities and relationships.</param>
/// <param name="LanInterval">How often the LAN collection runs after its startup run; default one minute.</param>
/// <param name="LanSource">The host interface reader; default is the platform collector.</param>
/// <param name="RetentionInterval">How often the retention pass runs after its startup run; default one hour.</param>
/// <param name="Retention">Retention thresholds; default 7 days raw, hourly rollups 1 year, events 5 years.</param>
/// <param name="PeerStateInterval">How often peer-state history is reconciled (pairing, revocation, offline to stale); default 15 seconds.</param>
/// <param name="Time">Clock for the background schedules; default is the system clock.</param>
public sealed record NodeLiveOptions(
    TimeSpan? CollectionInterval = null,
    LiveInterestPolicy? InterestPolicy = null,
    TimeSpan? HeartbeatInterval = null,
    bool CollectSelfLan = true,
    TimeSpan? LanInterval = null,
    ISelfInterfaceCollector? LanSource = null,
    TimeSpan? RetentionInterval = null,
    PeerRetentionPolicy? Retention = null,
    TimeProvider? Time = null,
    TimeSpan? PeerStateInterval = null);

public sealed class PeerNodeRuntime : IAsyncDisposable
{
    private readonly WebApplication peerApplication;
    private readonly LocalApiHost localApi;
    private readonly INodeIdentityProvider identity;
    private readonly NodePeerConnectionManager connections;
    private readonly NodeCollectionScheduler collection;
    private readonly PeerStore store;
    private readonly LiveInterestRegistry interests;
    private readonly AlertEngine alertEngine;
    private readonly NodePeriodicTask? lanTask;
    private readonly NodePeriodicTask retentionTask;
    private readonly PeerRetentionService retention;
    private readonly NodePeriodicTask peerStateTask;
    private readonly PeerStateTracker peerStates;

    private PeerNodeRuntime(
        WebApplication peerApplication,
        LocalApiHost localApi,
        INodeIdentityProvider identity,
        NodePeerConnectionManager connections,
        NodeCollectionScheduler collection,
        PeerStore store,
        LiveInterestRegistry interests,
        int peerPort,
        AlertEngine alertEngine,
        NodePeriodicTask? lanTask,
        NodePeriodicTask retentionTask,
        PeerRetentionService retention,
        NodePeriodicTask peerStateTask,
        PeerStateTracker peerStates)
    {
        this.peerStateTask = peerStateTask;
        this.peerStates = peerStates;
        this.lanTask = lanTask;
        this.retentionTask = retentionTask;
        this.retention = retention;
        PeerPort = peerPort;
        this.store = store;
        this.interests = interests;
        this.alertEngine = alertEngine;
        this.peerApplication = peerApplication;
        this.localApi = localApi;
        this.identity = identity;
        this.connections = connections;
        this.collection = collection;
    }

    public Uri? AdminAddress => localApi.Address;

    public PeerPublicIdentity Identity => identity.PublicIdentity;

    public PeerStore Store => store;

    /// <summary>The port the peer (gRPC) listener was asked to bind.</summary>
    public int PeerPort { get; }

    /// <summary>The sampling interval currently in force for this node's own collection.</summary>
    public TimeSpan CurrentCollectionInterval => collection.CurrentInterval;

    /// <summary>The scheduled self-LAN collection, or null when disabled.</summary>
    public NodePeriodicTask? LanCollection => lanTask;

    /// <summary>The scheduled retention pass (rollups, pruning).</summary>
    public NodePeriodicTask RetentionSchedule => retentionTask;

    /// <summary>Records peer connectivity transitions (the history feed's source) and the schedule that reconciles them.</summary>
    public PeerStateTracker PeerStates => peerStates;

    public NodePeriodicTask PeerStateSchedule => peerStateTask;

    /// <summary>The retention service the schedule runs (policy in force).</summary>
    public PeerRetentionService Retention => retention;

    public int ActiveLiveInterestCount => interests.ActiveInterestCount;

    /// <summary>Interests held only to pass on to another peer's session (diagnostics and tests).</summary>
    public int RelayedLiveInterestCount => interests.RelayedInterestCount;

    /// <summary>
    /// Newly stored observations (locally collected or replicated) as they commit; the in-process feed a UI subscribes to.
    /// </summary>
    public IAsyncEnumerable<StoredOriginRecord> LiveObservations(CancellationToken cancellationToken = default) =>
        store.Live.StreamAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// A local UI is viewing live data: with no argument this node's own data (switching its collection to the fast
    /// interval), otherwise a paired remote peer (declared over the held-open session). With <paramref name="signalIds"/>
    /// only those signals are sampled fast (empty or null means all). Dispose to stop viewing.
    /// </summary>
    public IDisposable BeginViewing(string? remotePeerId = null, IReadOnlyCollection<string>? signalIds = null) =>
        remotePeerId is null
            ? interests.WatchLocal(identity.PublicIdentity.PeerId, signalIds)
            : interests.WatchRemote(remotePeerId, signalIds);

    /// <summary>Held-open push sessions to a remote peer and live records pushed to it so far (diagnostics).</summary>
    public (int Sessions, long RecordsPushed) LivePushDiagnostics(string remotePeerId) =>
        (interests.PushChannels.SessionCount(remotePeerId), interests.PushChannels.RecordsPushed(remotePeerId));

    /// <summary>Schedules (and dials) a held-open session to an already paired peer at the given address.</summary>
    public Task TrackPeerAsync(string peerId, System.Net.IPAddress address, int port, CancellationToken cancellationToken = default) =>
        connections.TrackAsync(peerId, address, port, cancellationToken);

    public Task<PeerCollectionRunResult> CollectOnceAsync(
        CancellationToken cancellationToken = default) => collection.RunOnceAsync(cancellationToken);

    public static Task<PeerNodeRuntime> StartAsync(
        NodeOptions options,
        CancellationToken cancellationToken = default) => StartAsync(options, null, null, cancellationToken);

    public static async Task<PeerNodeRuntime> StartAsync(
        NodeOptions options,
        NodeLiveOptions? live,
        ILocalUpdateController? updates = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var interests = new LiveInterestRegistry(live?.InterestPolicy);
        // Fails clearly on unsupported OSes and Linux distributions before any state is created.
        var platform = PlatformDetector.DetectCurrent();
        Directory.CreateDirectory(options.DataDirectory);
        var databasePath = Path.Combine(options.DataDirectory, "peer.db");
        var peerStore = new PeerStore(PeerStoreOptions.Create(databasePath));
        var pairingStore = new PairingApplicationStore(PairingApplicationStoreOptions.Create(databasePath));
        await peerStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await pairingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var identity = await NodeIdentityProvider.LoadOrCreateAsync(
            options.DataDirectory,
            peerStore,
            cancellationToken).ConfigureAwait(false);
        var clock = new SystemPeerClock();
        var pairing = new PeerPairingCoordinator(
            peerStore,
            pairingStore,
            identity,
            clock,
            new CryptographicPeerRandomSource(),
            new PeerPairingCoordinatorOptions(
                new PeerProtocolRange(1, 1, 1),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(2)));
        await pairing.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var replication = new OriginReplicationCoordinator(peerStore);
        var alertStore = new AlertStore(AlertStoreOptions.Create(databasePath));
        await alertStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var alertRules = AlertRulesConfig.Default;
        var controlStore = new ControlStore(ControlStoreOptions.Create(databasePath));
        await controlStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var monitoring = new MonitoringControlState();
        var controlService = new ControlService(
            controlStore, peerStore, pairingStore, pairing, alertStore, monitoring, identity,
            updates: updates is null ? null : new MonitorApp.Peer.Node.Update.RemoteUpdateGateway(updates),
            alertRules: alertRules);
        await controlService.RestoreMonitoringAsync(cancellationToken).ConfigureAwait(false);
        var alertEngine = new AlertEngine(new AlertEvaluator(peerStore, alertStore, alertRules), alertStore);
        var capabilityRegistry = new PeerCapabilityRegistry(peerStore);
        await capabilityRegistry.RegisterAsync(
            SystemCapabilityDeclaration.Create(),
            cancellationToken).ConfigureAwait(false);
        var connections = new NodePeerConnectionManager(identity, peerStore, replication, interests, live?.HeartbeatInterval);
        var invitationService = new NodePeerInvitationService(identity, peerStore, connections);
        var collection = new NodeCollectionScheduler(
            new PeerSystemMetricsIngestionBridge(
                peerStore,
                identity,
                new SystemSnapshotCollector(platform),
                PeerCollectionOptions.Default(
                    $"peer:{identity.PublicIdentity.PeerId}",
                    Guid.NewGuid().ToString("N"))),
            live?.CollectionInterval,
            interests,
            identity.PublicIdentity.PeerId,
            monitoring);
        var time = live?.Time ?? TimeProvider.System;
        var retentionService = new PeerRetentionService(
            peerStore,
            new TimeProviderPeerClock(time),
            live?.Retention);
        var retentionTask = new NodePeriodicTask(
            "retention",
            live?.RetentionInterval ?? TimeSpan.FromHours(1),
            async token => await retentionService.RunAsync(token).ConfigureAwait(false),
            time);
        var peerStateTracker = new PeerStateTracker(
            peerStore,
            new TimeProviderPeerClock(time),
            alertRules.PeerSilentAfter,
            peerId => interests.PushChannels.SessionCount(peerId) > 0);
        interests.PushChannels.ConnectivityChanged += (peerId, up) => peerStateTracker.NotifySession(peerId, up);
        var peerStateTask = new NodePeriodicTask(
            "peer-state",
            live?.PeerStateInterval ?? TimeSpan.FromSeconds(15),
            async token => await peerStateTracker.ReconcileAsync(token).ConfigureAwait(false),
            time);
        NodePeriodicTask? lanTask = null;
        if (live?.CollectSelfLan ?? true)
        {
            var lanSource = live?.LanSource ?? SelfInterfaceCollectorFactory.Create();
            var selfPeerId = identity.PublicIdentity.PeerId;
            var lanTopology = new SelfLanCollector(selfPeerId, lanSource, peerStore, time);
            var lanLinks = new PeerSystemMetricsIngestionBridge(
                peerStore,
                identity,
                new LanLinkSnapshotCollector(lanSource, time),
                PeerCollectionOptions.Default($"peer:{selfPeerId}", Guid.NewGuid().ToString("N")) with
                {
                    CollectorId = LanLinkObservations.CollectorId,
                },
                () => time.GetUtcNow());
            lanTask = new NodePeriodicTask(
                "lan",
                live?.LanInterval ?? TimeSpan.FromMinutes(1),
                async token =>
                {
                    // Topology first so a link observation never refers to an interface the store has not seen.
                    await lanTopology.CollectAndPersistAsync(token).ConfigureAwait(false);
                    await lanLinks.RunOnceAsync(token).ConfigureAwait(false);
                },
                time);
        }

        var peerArgs = new[]
        {
            "--MonitorApp:Peer:BindAddress", options.PeerBindAddress.ToString(),
            "--MonitorApp:Peer:Port", options.PeerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--MonitorApp:Peer:AllowLoopback", System.Net.IPAddress.IsLoopback(options.PeerBindAddress).ToString(),
        };
        var pairingCodes = new PairingShortCodeRegistry();
        var peerApplication = await PeerHostApplication.CreateAsync(
            peerArgs,
            services =>
            {
                services.AddSingleton(peerStore);
                services.AddSingleton(pairingStore);
                services.AddSingleton<ILocalPeerIdentityProvider>(identity);
                services.AddSingleton(identity);
                services.AddSingleton<IPeerClock>(clock);
                services.AddSingleton(pairing);
                services.AddSingleton(replication);
                services.AddSingleton(interests);
                services.AddSingleton(new AlertDeliveryService(peerStore, alertStore, alertRules));
                services.AddSingleton(controlService);
                services.AddProductionPeerSessionCoordinator();
                services.AddSingleton(new PeerPairingServiceOptions(Environment.MachineName));
                services.AddProductionPeerPairingCoordinator();
                services.AddSingleton(pairingCodes);
            }, cancellationToken).ConfigureAwait(false);
        var localApi = LocalApiHost.Create(
            new LocalApiOptions(
                databasePath,
                System.Net.IPAddress.Loopback,
                options.AdminPort),
            peerStore,
            pairingStore,
            pairing,
            clock,
            invitationService,
            new PhonePairingOptions([options.PeerBindAddress], options.PeerPort, pairingCodes),
            alertStore,
            interests,
            updates,
            controlService,
            monitoring,
            LocalPresenceService.Create(
                Path.Combine(options.DataDirectory, "local-presence.json"), options.LocalPresence, clock));
        try
        {
            await peerApplication.StartAsync(cancellationToken).ConfigureAwait(false);
            await localApi.StartAsync(cancellationToken).ConfigureAwait(false);
            await connections.StartAsync(cancellationToken).ConfigureAwait(false);
            collection.Start();
            alertEngine.Start();
            lanTask?.Start();
            retentionTask.Start();
            peerStateTask.Start();
            return new PeerNodeRuntime(peerApplication, localApi, identity, connections, collection, peerStore, interests, options.PeerPort, alertEngine, lanTask, retentionTask, retentionService, peerStateTask, peerStateTracker);
        }
        catch
        {
            await peerStateTask.DisposeAsync().ConfigureAwait(false);
            await retentionTask.DisposeAsync().ConfigureAwait(false);
            if (lanTask is not null)
            {
                await lanTask.DisposeAsync().ConfigureAwait(false);
            }

            await alertEngine.DisposeAsync().ConfigureAwait(false);
            await collection.DisposeAsync().ConfigureAwait(false);
            await connections.DisposeAsync().ConfigureAwait(false);
            await localApi.DisposeAsync().ConfigureAwait(false);
            await peerApplication.DisposeAsync().ConfigureAwait(false);
            identity.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Every step runs even if an earlier one throws: a faulted background task must not leave the host, the local API
        // or the identity (and through them the database files) open. Failures are reported once everything is released.
        List<Exception>? failures = null;
        async ValueTask Step(Func<ValueTask> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        peerStates.Suspend();
        await Step(() => peerStateTask.DisposeAsync()).ConfigureAwait(false);
        await Step(() => retentionTask.DisposeAsync()).ConfigureAwait(false);
        if (lanTask is not null)
        {
            await Step(() => lanTask.DisposeAsync()).ConfigureAwait(false);
        }

        await Step(() => alertEngine.DisposeAsync()).ConfigureAwait(false);
        await Step(() => collection.DisposeAsync()).ConfigureAwait(false);
        await Step(() => connections.DisposeAsync()).ConfigureAwait(false);
        await Step(() => localApi.DisposeAsync()).ConfigureAwait(false);
        await Step(async () =>
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await peerApplication.StopAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }

            // Kestrel may return from a timed-out stop while a session handler (and its push loops) is still unwinding;
            // those use the stores below, so wait for them (they were cancelled by ApplicationStopping) before disposing.
            if (peerApplication.Services.GetService<PeerSessionHandlerTracker>() is { } handlers)
            {
                await handlers.WaitForIdleAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await Step(() => peerApplication.DisposeAsync()).ConfigureAwait(false);
        await Step(() =>
        {
            identity.Dispose();
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        if (failures is { Count: > 0 })
        {
            throw new AggregateException("Peer node shutdown completed with failures.", failures);
        }
    }
}

/// <summary>Adapts a <see cref="TimeProvider"/> to the peer clock so retention follows the node's schedule clock.</summary>
internal sealed class TimeProviderPeerClock(TimeProvider time) : IPeerClock
{
    public DateTimeOffset UtcNow => time.GetUtcNow();
}
