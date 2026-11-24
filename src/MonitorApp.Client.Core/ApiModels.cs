namespace MonitorApp.Client.Core;

// Client-side copies of the local API wire shapes. The client deliberately does not reference server projects:
// the HTTP contract is the boundary, so a node and a client can evolve and be diagnosed independently.

public sealed record RuntimeDto(
    string Status,
    string Scope,
    LocalPeerDto? LocalPeer,
    int TrustedPeerCount,
    int PendingPairingCount);

public sealed record LocalPeerDto(string PeerId, string FingerprintSha256);

public sealed record TrustedPeerDto(
    string PeerId,
    string? DisplayName,
    string FingerprintSha256,
    string TrustState,
    DateTimeOffset PairedAt,
    DateTimeOffset? RevokedAt,
    string? Provenance = null);

public sealed record PendingPairingDto(
    string PairingId,
    string RemotePeerId,
    string? DisplayName,
    string FingerprintSha256,
    string ApprovalState,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>What a trusted peer may ask this desktop to do. A peer with no stored grant is denied everything.</summary>
public sealed record ControlGrantDto(
    string PeerId,
    bool MonitoringControls,
    bool PairingApproval,
    bool UpdateApproval,
    DateTimeOffset? UpdatedAt)
{
    public bool AllowsAnything => MonitoringControls || PairingApproval || UpdateApproval;

    public static ControlGrantDto Default(string peerId) => new(peerId, false, false, false, null);
}

/// <summary>A phone's presence-key enrollment awaiting approval on this desktop. Compare <see cref="FingerprintSha256"/> with the phone.</summary>
public sealed record PresenceEnrollmentDto(
    string EnrollmentId,
    string PeerId,
    string? DisplayName,
    DateTimeOffset RequestedAt,
    string FingerprintSha256);

public sealed record ObservationValueDto(string Kind, double? Number, string? Text, bool? Boolean);

public sealed record HistoryPointDto(
    string OriginPeerId,
    string OriginStreamId,
    ulong Sequence,
    string SubjectEntityId,
    string SignalId,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    string Source,
    string Provenance,
    string Quality,
    string Freshness,
    double AgeSeconds,
    ObservationValueDto? Value,
    string? Unit,
    string? Detail);

public sealed record HistoryPageDto(
    DateTimeOffset From,
    DateTimeOffset To,
    int Limit,
    string Resolution,
    int Count,
    bool Truncated,
    string? NextCursor,
    IReadOnlyList<HistoryPointDto> Points);

public sealed record StateDto(
    DateTimeOffset AsOf,
    DateTimeOffset LookbackFrom,
    int Limit,
    string Resolution,
    bool Truncated,
    IReadOnlyList<HistoryPointDto> Points);

public sealed record CatalogSignalDto(
    string SignalId,
    string? Unit,
    string Kind,
    string LatestQuality,
    DateTimeOffset LatestObservedAt);

public sealed record CatalogSubjectDto(string SubjectEntityId, IReadOnlyList<CatalogSignalDto> Signals);

public sealed record CatalogDto(IReadOnlyList<CatalogSubjectDto> Subjects, IReadOnlyList<string> OriginPeers);

/// <summary>One change of a peer's connectivity state (Paired, Online, Stale, Offline, Revoked) for the history timeline.</summary>
public sealed record PeerStateTransitionDto(string PeerId, string State, DateTimeOffset At, string? Reason = null);

/// <summary>One update install outcome (Applied, RolledBack, Failed) for the history timeline.</summary>
/// <summary>Whether paired phones can reach this computer (GET /api/v1/network/reach).</summary>
public sealed record PeerReachDto(
    bool ReachableByPhone,
    string ListeningOn,
    string? TailscaleAddress,
    string ConfiguredBy,
    bool CanChange,
    bool RestartPending,
    string Explanation);

/// <summary>The current phone invitation; <see cref="QrRows"/> are rows of '1' (dark) and '0' (light) modules.</summary>
public sealed record PhoneInvitationDto(
    string Address, int Port, string ShortCode, string Payload, DateTimeOffset ExpiresAt, IReadOnlyList<string> QrRows);

public sealed record PhonePairingDto(bool Configured, IReadOnlyList<string> Addresses, PhoneInvitationDto? Invitation);

/// <summary>This computer's own update state (GET /api/v1/update).</summary>
public sealed record LocalUpdateStatusDto(
    string Policy,
    string InstalledVersion,
    string Stage,
    string? TargetVersion,
    string? Detail,
    DateTimeOffset? LastCheckedUtc,
    string? LastError,
    bool ApprovalPending,
    string? TargetDigest = null);

public sealed record UpdateEventDto(string Version, string Outcome, DateTimeOffset At, string? Detail = null);

public sealed record AlertDto(
    string AlertId,
    uint Revision,
    string RuleId,
    string SubjectId,
    string OriginPeerId,
    string Severity,
    string State,
    string Title,
    string Detail,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ResolvedAt,
    bool Acknowledged = false,
    string? AcknowledgedBy = null,
    DateTimeOffset? AcknowledgedAt = null,
    bool Escalated = false);

/// <summary>
/// A recipient's alert preferences on this desktop (T049). <see cref="MinSeverity"/> is "All", "Info", "Warning" or "Critical";
/// <see cref="MutedRuleKinds"/> are rule ids the recipient never receives. <see cref="UpdatedAt"/> is null for the default.
/// </summary>
public sealed record AlertPreferencesDto(
    string PeerId,
    string MinSeverity,
    IReadOnlyList<string> MutedRuleKinds,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// One alert rule as the user can edit it (T054). <see cref="Unit"/> is "percent" (CPU, memory, disk) or "minutes" (peer not seen);
/// <see cref="Minimum"/> and <see cref="Maximum"/> are the range the server accepts. <see cref="SubjectPeerId"/> is the peer the rule
/// applies to (the owning peer for CPU, memory and disk; a watched peer for peer not seen).
/// </summary>
public sealed record AlertRuleDto(
    string SubjectPeerId,
    string RuleId,
    string Title,
    string Unit,
    double DefaultThreshold,
    double Threshold,
    bool Enabled,
    bool Overridden,
    double Minimum,
    double Maximum,
    DateTimeOffset? UpdatedAt);

/// <summary>A requested rule override: <see cref="Threshold"/> null keeps the built-in default.</summary>
public sealed record AlertRuleOverrideDto(string RuleId, bool Enabled, double? Threshold);

/// <summary>Query for a bounded history window. Paging is handled by the client.</summary>
public sealed record HistoryQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    string? SubjectEntityId = null,
    string? SignalId = null);

/// <summary>All pages fetched for a history query. <see cref="MoreAvailable"/> is true when the page cap stopped paging.</summary>
public sealed record HistoryResult(
    IReadOnlyList<HistoryPointDto> Points,
    string Resolution,
    int PagesFetched,
    bool MoreAvailable);

/// <summary>A lease on live (fast) sampling granted by the node; renew it before it lapses.</summary>
public sealed record LiveInterestLeaseDto(string LeaseId, DateTimeOffset ExpiresAt, int RenewAfterSeconds);

/// <summary>One value of a thing. <see cref="Evidence"/> is Measured, Specified, Imported, Entered, Estimated or Unknown.</summary>
public sealed record ThingFactDto(string Key, string Label, string Value, string Evidence, string Source, DateTimeOffset RecordedAt, int Others);

/// <summary>
/// A thing in the shared model. <see cref="ReadingSubjects"/> are the monitoring subjects whose live readings belong to it; for a part,
/// <see cref="SignalPrefix"/> narrows them to the part's own readings ("system.memory.").
/// </summary>
public sealed record ThingDto(
    string Id,
    string Name,
    string KindId,
    string KindLabel,
    string Lifecycle,
    IReadOnlyList<string> Tags,
    bool Monitored,
    IReadOnlyList<string> ReadingSubjects,
    IReadOnlyList<ThingFactDto> Facts,
    string? InsideId,
    IReadOnlyList<string> PartIds,
    DateTimeOffset CreatedAt,
    string? SignalPrefix = null,
    IReadOnlyList<ThingLinkDto>? Links = null,
    IReadOnlyList<ThingPortDto>? Ports = null,
    string? Slot = null);

/// <summary>A port of a thing: connector, plug or socket, direction, signals, its own values, and what it is plugged into.</summary>
public sealed record ThingPortDto(
    string PortId,
    string Name,
    string? ConnectorTypeId,
    string Gender,
    string Direction,
    IReadOnlyList<string> Signals,
    IReadOnlyList<ThingFactDto> Values,
    string? ConnectionId = null,
    string? OtherId = null,
    string? OtherName = null,
    string? OtherPortName = null);

/// <summary>A port to add; quantities carry their unit ("9 V", "500 mA").</summary>
public sealed record ThingPortEditDto(
    string Name,
    string? ConnectorTypeId = null,
    string? Gender = null,
    string? Direction = null,
    IReadOnlyList<string>? Signals = null,
    string? Level = null,
    string? Voltage = null,
    string? Polarity = null,
    string? CurrentMax = null,
    string? CurrentDraw = null,
    bool FromSpec = false);

/// <summary>Plug a port of this thing (by id, or by the name of a port added in the same edit) into another thing's port.</summary>
public sealed record ThingConnectEditDto(string PortId, string OtherId, string OtherPortId);

/// <summary>A connector from the shared connector library.</summary>
public sealed record ConnectorChoiceDto(string Id, string Label, string Domain);

public sealed record WiringFindingDto(string Code, string Certainty, string Explanation);

/// <summary>A connection path at a thing with the connection engine's verdict and its reasons.</summary>
public sealed record ThingWiringDto(string ThingId, string Route, string Verdict, string VerdictText, IReadOnlyList<WiringFindingDto> Findings);

public sealed record ThingKindDto(string Id, string Label);

/// <summary>A change to one thing; null fields are left alone.</summary>
public sealed record ThingEditDto(
    string Id,
    string? Name = null,
    string? KindId = null,
    string? Note = null,
    string? InsideId = null,
    string? Lifecycle = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<ThingValueEditDto>? Values = null,
    IReadOnlyList<ThingLinkEditDto>? AddLinks = null,
    IReadOnlyList<string>? RemoveLinks = null,
    IReadOnlyList<ThingPortEditDto>? AddPorts = null,
    IReadOnlyList<string>? RemovePorts = null,
    IReadOnlyList<ThingConnectEditDto>? Connect = null,
    IReadOnlyList<string>? Disconnect = null,
    string? Slot = null);

/// <summary>A value the person adds ("Max power" = "5 W"); an empty value removes theirs. FromSpec marks it as read from a spec sheet.</summary>
public sealed record ThingValueEditDto(string Label, string? Value, bool FromSpec = false);

public sealed record ThingsDto(
    IReadOnlyList<ThingDto> Things, IReadOnlyList<ThingKindDto> Kinds, IReadOnlyList<ThingKindDto>? LinkTypes = null,
    IReadOnlyList<ConnectorChoiceDto>? Connectors = null, IReadOnlyList<ThingWiringDto>? Wiring = null, IReadOnlyList<MissingItemDto>? Missing = null, IReadOnlyList<RackLayoutDto>? Racks = null, IReadOnlyList<SurfaceLayoutDto>? Surfaces = null,
    IReadOnlyList<ProjectPartsDto>? Projects = null);

/// <summary>A project's parts list: each part with its stage and price, how many are in hand, and what the rest costs.</summary>
public sealed record ProjectPartsDto(string ProjectId, IReadOnlyList<ProjectPartDto> Parts, int Have, int Ordered, int ToBuy, string Summary, string CostText);

/// <summary>State is ToBuy, Ordered, Have or Gone. Price is written out ("$45"), or null when none is recorded.</summary>
public sealed record ProjectPartDto(string ThingId, string Name, string Lifecycle, string State, double Quantity, bool QuantityKnown, string? Price, string PriceEvidence);

/// <summary>A pedalboard or desk seen from above, sizes in mm, with what sits where and fit findings.</summary>
public sealed record SurfaceLayoutDto(string SurfaceId, double Width, double Depth, bool SizeKnown, IReadOnlyList<SurfaceItemDto> Items, IReadOnlyList<string> Unplaced, IReadOnlyList<WiringFindingDto> Findings);

public sealed record SurfaceItemDto(string ThingId, string Name, double X, double Y, double Width, double Depth, bool SizeKnown, bool Problem);

/// <summary>A rack drawn top down: blocks of equipment or empty units, what is inside but not placed, and fit findings.</summary>
public sealed record RackLayoutDto(string RackId, int Units, bool UnitsKnown, IReadOnlyList<RackBlockDto> Blocks, IReadOnlyList<string> Unplaced, IReadOnlyList<WiringFindingDto> Findings);

public sealed record RackBlockDto(int TopUnit, int Units, string? ThingId, string? Name, bool HeightKnown, bool Problem);

/// <summary>Something a setup still needs ("Power for Tuner"), with how sure the app is and the things involved.</summary>
public sealed record MissingItemDto(string Code, string Need, string Explanation, string Certainty, IReadOnlyList<string> Things);

/// <summary>A link on a thing, phrased from its side ("Powered by" Studio PSU).</summary>
public sealed record ThingLinkDto(string RelationshipId, string TypeId, string Phrase, string OtherId, string OtherName, bool Outgoing);

/// <summary>A link to add: "this thing &lt;type&gt; other".</summary>
public sealed record ThingLinkEditDto(string TypeId, string OtherId);
