namespace MonitorApp.Android.Core;

public sealed record PairingReceipt(
    string AgentId,
    string NodeId,
    string PublicKeySha256Base64,
    DateTimeOffset CompletedAt);
