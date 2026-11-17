using Android.Content;
using MonitorApp.Android.Core;

namespace MonitorApp.Android;

public sealed class PairingReceiptStore(Context context)
{
    private const string PreferencesName = "monitor-app-identity";

    public void Save(PairingReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Android identity storage is unavailable.");
        using var editor = preferences.Edit()
            ?? throw new InvalidOperationException("Android identity storage cannot be edited.");
        editor.PutString("agent_id", receipt.AgentId);
        editor.PutString("node_id", receipt.NodeId);
        editor.PutString("public_key_sha256", receipt.PublicKeySha256Base64);
        editor.PutLong("completed_at_unix_ms", receipt.CompletedAt.ToUnixTimeMilliseconds());
        if (!editor.Commit())
        {
            throw new InvalidOperationException("Android identity receipt could not be saved.");
        }
    }

    public PairingReceipt? Load()
    {
        using var preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
        var agentId = preferences?.GetString("agent_id", null);
        var nodeId = preferences?.GetString("node_id", null);
        var fingerprint = preferences?.GetString("public_key_sha256", null);
        var completedAt = preferences?.GetLong("completed_at_unix_ms", long.MinValue) ?? long.MinValue;
        if (string.IsNullOrWhiteSpace(agentId) ||
            string.IsNullOrWhiteSpace(nodeId) ||
            string.IsNullOrWhiteSpace(fingerprint) ||
            completedAt == long.MinValue)
        {
            return null;
        }

        return new PairingReceipt(
            agentId,
            nodeId,
            fingerprint,
            DateTimeOffset.FromUnixTimeMilliseconds(completedAt));
    }
}
