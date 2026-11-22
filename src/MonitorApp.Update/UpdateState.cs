using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorApp.Update;

/// <summary>Lifecycle of one verified update. Persisted so a restart resumes from an explicit state.</summary>
public enum UpdateStage
{
    Available,
    Staged,
    Applying,
    Applied,
    RolledBack,
    Failed,
}

public sealed record UpdateState(
    UpdateStage Stage,
    string Component,
    string Platform,
    string Architecture,
    string TargetVersion,
    string PreviousVersion,
    Uri ArtifactUrl,
    long ArtifactSize,
    string ArtifactSha256,
    string? StagedPath,
    string? Detail,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Persisted document: the active update plus the anti-rollback high-water mark.</summary>
public sealed record UpdateStateSnapshot(string? HighestSeenVersion, UpdateState? Active);

public sealed class UpdateStateCorruptException : Exception
{
    public UpdateStateCorruptException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// File-backed state and append-only audit log. Writes are atomic (temp file + rename). A corrupt state
/// file fails closed: it never silently resets the anti-rollback high-water mark.
/// </summary>
public sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _statePath;
    private readonly string _auditPath;

    public UpdateStateStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _statePath = Path.Combine(directory, "update-state.json");
        _auditPath = Path.Combine(directory, "update-audit.jsonl");
    }

    public UpdateStateSnapshot Read()
    {
        if (!File.Exists(_statePath))
        {
            return new UpdateStateSnapshot(null, null);
        }

        try
        {
            return JsonSerializer.Deserialize<UpdateStateSnapshot>(File.ReadAllBytes(_statePath), Json)
                   ?? throw new UpdateStateCorruptException("Update state file is empty.");
        }
        catch (JsonException ex)
        {
            throw new UpdateStateCorruptException("Update state file is not valid; refusing to reset anti-rollback state.", ex);
        }
    }

    public void Write(UpdateStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, snapshot, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _statePath, overwrite: true);
    }

    public void Audit(DateTimeOffset at, string eventName, string detail)
    {
        var line = JsonSerializer.Serialize(new { at, @event = eventName, detail });
        File.AppendAllText(_auditPath, line + "\n");
    }

    public IReadOnlyList<string> ReadAudit() =>
        File.Exists(_auditPath) ? File.ReadAllLines(_auditPath) : [];
}
