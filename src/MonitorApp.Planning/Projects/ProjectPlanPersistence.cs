using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MonitorApp.Planning.Projects;

public class ProjectPlanException : Exception
{
    public ProjectPlanException() { }
    public ProjectPlanException(string message) : base(message) { }
    public ProjectPlanException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ProjectPlanFormatException : ProjectPlanException
{
    public ProjectPlanFormatException() { }
    public ProjectPlanFormatException(string message) : base(message) { }
    public ProjectPlanFormatException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ProjectPlanVersionException : ProjectPlanException
{
    public ProjectPlanVersionException() { }
    public ProjectPlanVersionException(string message) : base(message) { }
    public ProjectPlanVersionException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ProjectPlanConcurrencyException : ProjectPlanException
{
    public ProjectPlanConcurrencyException() { }
    public ProjectPlanConcurrencyException(string message) : base(message) { }
    public ProjectPlanConcurrencyException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>An explicit, one-version-at-a-time migration for a planning document envelope.</summary>
public interface IProjectPlanMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    JsonObject Migrate(JsonObject document);
}

public sealed record ProjectPlanImport(
    ProjectScenario Scenario,
    ProjectEvaluation Evaluation,
    int SchemaVersion,
    DateTimeOffset SavedAt);

/// <summary>
/// Strict, bounded planning JSON. Unsupported versions fail closed unless an explicit sequential migration is supplied.
/// The checksum detects accidental or partial content changes; it is integrity metadata, not an authenticity signature.
/// </summary>
public sealed class ProjectPlanCodec
{
    public const string Format = "monitorapp-planning";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumDocumentBytes = 16 * 1024 * 1024;

    private readonly Dictionary<int, IProjectPlanMigration> migrations;
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(writeIndented: false);
    private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(writeIndented: true);

    public ProjectPlanCodec(IEnumerable<IProjectPlanMigration>? migrations = null)
    {
        var index = new Dictionary<int, IProjectPlanMigration>();
        foreach (var migration in migrations ?? [])
        {
            ArgumentNullException.ThrowIfNull(migration);
            if (migration.FromVersion < 0 || migration.ToVersion != migration.FromVersion + 1 ||
                !index.TryAdd(migration.FromVersion, migration))
                throw new ArgumentException("Migrations must be unique, nonnegative, and advance exactly one version.",
                    nameof(migrations));
        }
        this.migrations = index;
    }

    public static byte[] Export(ProjectScenario scenario, DateTimeOffset? savedAt = null)
    {
        ValidateScenarioIdentity(scenario);
        var saveTime = savedAt ?? DateTimeOffset.UtcNow;
        if (saveTime == default)
            throw new ProjectPlanFormatException("A planning document requires a valid save timestamp.");
        var scenarioHash = ComputeScenarioHash(scenario);
        var envelope = new ProjectPlanEnvelope(
            Format,
            CurrentSchemaVersion,
            saveTime.ToUniversalTime(),
            scenarioHash,
            scenario);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, IndentedOptions);
        if (bytes.Length > MaximumDocumentBytes)
            throw new ProjectPlanFormatException($"Planning document exceeds {MaximumDocumentBytes} bytes.");
        return bytes;
    }

    public ProjectPlanImport Import(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumDocumentBytes)
            throw new ProjectPlanFormatException($"Planning document must contain 1 to {MaximumDocumentBytes} bytes.");

        JsonObject root;
        try
        {
            root = JsonNode.Parse(utf8Json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            }) as JsonObject ?? throw new ProjectPlanFormatException("Planning document root must be an object.");
        }
        catch (JsonException ex)
        {
            throw new ProjectPlanFormatException("Planning document is not valid bounded JSON.", ex);
        }

        var version = ReadEnvelopeVersion(root);
        while (version < CurrentSchemaVersion)
        {
            if (!migrations.TryGetValue(version, out var migration))
                throw new ProjectPlanVersionException(
                    $"Planning schema {version} requires an unavailable explicit migration.");
            try
            {
                root = migration.Migrate((JsonObject)root.DeepClone()) ??
                       throw new ProjectPlanVersionException($"Migration from schema {version} returned no document.");
            }
            catch (ProjectPlanException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProjectPlanVersionException($"Migration from schema {version} failed.", ex);
            }

            var migratedVersion = ReadEnvelopeVersion(root);
            if (migratedVersion != migration.ToVersion)
                throw new ProjectPlanVersionException(
                    $"Migration from schema {version} did not produce schema {migration.ToVersion}.");
            version = migratedVersion;
        }

        if (version != CurrentSchemaVersion)
            throw new ProjectPlanVersionException(
                $"Planning schema {version} is newer than supported schema {CurrentSchemaVersion}.");

        ProjectPlanEnvelope envelope;
        try
        {
            envelope = root.Deserialize<ProjectPlanEnvelope>(CompactOptions) ??
                       throw new ProjectPlanFormatException("Planning document has no envelope.");
        }
        catch (JsonException ex)
        {
            throw new ProjectPlanFormatException("Planning document does not match its declared schema.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new ProjectPlanFormatException("Planning document contains an unsupported value.", ex);
        }

        if (!string.Equals(envelope.Format, Format, StringComparison.Ordinal))
            throw new ProjectPlanFormatException("Planning document format identifier is not recognized.");
        if (envelope.SavedAt == default)
            throw new ProjectPlanFormatException("Planning document has no valid save timestamp.");
        ValidateScenarioIdentity(envelope.Scenario);
        if (!FixedTimeHashEquals(envelope.ScenarioSha256, ComputeScenarioHash(envelope.Scenario)))
            throw new ProjectPlanFormatException("Planning document scenario checksum does not match its content.");

        ProjectEvaluation evaluation;
        try
        {
            evaluation = ProjectScenarioEvaluator.Evaluate(envelope.Scenario);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            throw new ProjectPlanFormatException("Planning document contains malformed nested content.", ex);
        }

        return new ProjectPlanImport(
            envelope.Scenario,
            evaluation,
            envelope.SchemaVersion,
            envelope.SavedAt.ToUniversalTime());
    }

    private static int ReadEnvelopeVersion(JsonObject root)
    {
        if (root["format"] is not JsonValue formatValue ||
            !formatValue.TryGetValue<string>(out var format) ||
            !string.Equals(format, Format, StringComparison.Ordinal))
            throw new ProjectPlanFormatException("Planning document format identifier is not recognized.");
        if (root["schemaVersion"] is not JsonValue value || !value.TryGetValue<int>(out var version) || version < 0)
            throw new ProjectPlanVersionException("Planning document has no valid schema version.");
        return version;
    }

    private static void ValidateScenarioIdentity(ProjectScenario scenario)
    {
        if (scenario is null)
            throw new ProjectPlanFormatException("Planning document has no scenario.");
        if (string.IsNullOrWhiteSpace(scenario.ProjectId) || string.IsNullOrWhiteSpace(scenario.ScenarioId) ||
            string.IsNullOrWhiteSpace(scenario.Name) || scenario.Revision <= 0)
            throw new ProjectPlanFormatException("A planning document requires project, scenario, name, and revision identity.");
        if (scenario.Requirements is null || scenario.Parts is null || scenario.Tasks is null || scenario.Offers is null)
            throw new ProjectPlanFormatException("Planning document collections cannot be null.");
        if (scenario.Requirements.Any(static item => item is null) ||
            scenario.Parts.Any(static item => item is null) ||
            scenario.Tasks.Any(static item => item is null) ||
            scenario.Offers.Any(static item => item is null))
            throw new ProjectPlanFormatException("Planning document collections cannot contain null entries.");
        if (scenario.Tasks.Any(static task =>
                task.DependsOn is null || task.RelatedPartIds is null ||
                task.DependsOn.Any(static id => id is null) ||
                task.RelatedPartIds.Any(static id => id is null)))
            throw new ProjectPlanFormatException("Planning task references cannot be null.");
        if (scenario.RackPlan is not null &&
            (scenario.RackPlan.ScenarioId != scenario.ScenarioId || scenario.RackPlan.Revision != scenario.Revision))
            throw new ProjectPlanFormatException("Rack plan identity must match the enclosing scenario revision.");
        if (scenario.RackPlan is not null &&
            (scenario.RackPlan.Zones is null || scenario.RackPlan.Equipment is null ||
             scenario.RackPlan.Placements is null))
            throw new ProjectPlanFormatException("Rack plan collections cannot be null.");
        if (scenario.RackPlan is not null &&
            (scenario.RackPlan.Zones.Any(static item => item is null) ||
             scenario.RackPlan.Equipment.Any(static item => item is null) ||
             scenario.RackPlan.Placements.Any(static item => item is null)))
            throw new ProjectPlanFormatException("Rack plan collections cannot contain null entries.");
        if (scenario.AudioPlan is not null &&
            (scenario.AudioPlan.ScenarioId != scenario.ScenarioId || scenario.AudioPlan.Revision != scenario.Revision))
            throw new ProjectPlanFormatException("Audio plan identity must match the enclosing scenario revision.");
        if (scenario.AudioPlan is not null &&
            (scenario.AudioPlan.Devices is null || scenario.AudioPlan.Ports is null ||
             scenario.AudioPlan.Cables is null || scenario.AudioPlan.Patches is null ||
             scenario.AudioPlan.DcLoads is null || scenario.AudioPlan.DcOutputs is null ||
             scenario.AudioPlan.DcAssignments is null))
            throw new ProjectPlanFormatException("Audio plan collections cannot be null.");
        if (scenario.AudioPlan is not null &&
            (scenario.AudioPlan.Devices.Any(static item => item is null) ||
             scenario.AudioPlan.Ports.Any(static item => item is null) ||
             scenario.AudioPlan.Cables.Any(static item => item is null) ||
             scenario.AudioPlan.Patches.Any(static item => item is null) ||
             scenario.AudioPlan.DcLoads.Any(static item => item is null) ||
             scenario.AudioPlan.DcOutputs.Any(static item => item is null) ||
             scenario.AudioPlan.DcAssignments.Any(static item => item is null)))
            throw new ProjectPlanFormatException("Audio plan collections cannot contain null entries.");
    }

    private static string ComputeScenarioHash(ProjectScenario scenario) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scenario, CompactOptions)))
            .ToLowerInvariant();

    private static bool FixedTimeHashEquals(string provided, string expected)
    {
        if (string.IsNullOrWhiteSpace(provided)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(provided), Convert.FromHexString(expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = writeIndented,
            MaxDepth = 64,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed record ProjectPlanEnvelope(
        string Format,
        int SchemaVersion,
        DateTimeOffset SavedAt,
        string ScenarioSha256,
        ProjectScenario Scenario);
}

public sealed record ProjectPlanRevisionInfo(
    string ProjectId,
    string ScenarioId,
    int Revision,
    string Name,
    DateTimeOffset SavedAt,
    bool HasErrors,
    bool HasUnresolved);

/// <summary>Immutable per-revision files with same-directory atomic rename and optimistic revision checks.</summary>
public sealed class ProjectPlanStore
{
    private const string RevisionPrefix = "revision-";
    private readonly string rootDirectory;
    private readonly ProjectPlanCodec codec;
    private readonly TimeProvider timeProvider;

    public ProjectPlanStore(string rootDirectory, ProjectPlanCodec? codec = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        this.codec = codec ?? new ProjectPlanCodec();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProjectPlanRevisionInfo> SaveAsync(
        ProjectScenario scenario,
        int? expectedPreviousRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var bytes = ProjectPlanCodec.Export(scenario, timeProvider.GetUtcNow());
        var directory = GetScenarioDirectory(scenario.ProjectId, scenario.ScenarioId);
        Directory.CreateDirectory(directory);

        await using var writeLock = AcquireWriteLock(directory);
        var revisions = ReadRevisionNumbers(directory);
        var latest = revisions.Count == 0 ? 0 : revisions[^1];
        if (expectedPreviousRevision is { } expected && expected != latest)
            throw new ProjectPlanConcurrencyException(
                $"Expected previous revision {expected}, but stored revision is {latest}.");
        if (latest > 0 && scenario.Revision != latest + 1)
            throw new ProjectPlanConcurrencyException(
                $"Revision {scenario.Revision} must immediately follow stored revision {latest}.");

        var destination = GetRevisionPath(directory, scenario.Revision);
        if (File.Exists(destination))
            throw new ProjectPlanConcurrencyException($"Revision {scenario.Revision} already exists.");

        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.planning.tmp");
        try
        {
            await using (var output = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            }))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException ex) when (File.Exists(destination))
        {
            throw new ProjectPlanConcurrencyException($"Revision {scenario.Revision} was written concurrently.", ex);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return ToInfo(codec.Import(bytes));
    }

    public async Task<ProjectPlanImport> LoadAsync(
        string projectId,
        string scenarioId,
        int revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        var path = GetRevisionPath(GetScenarioDirectory(projectId, scenarioId), revision);
        if (!File.Exists(path)) throw new FileNotFoundException("Planning revision was not found.", path);
        var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        var imported = codec.Import(bytes);
        if (imported.Scenario.ProjectId != projectId || imported.Scenario.ScenarioId != scenarioId ||
            imported.Scenario.Revision != revision)
            throw new ProjectPlanFormatException("Stored planning revision identity does not match its lookup key.");
        return imported;
    }

    public async Task<ProjectPlanImport?> LoadLatestAsync(
        string projectId,
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        var directory = GetScenarioDirectory(projectId, scenarioId);
        var revisions = ReadRevisionNumbers(directory);
        return revisions.Count == 0
            ? null
            : await LoadAsync(projectId, scenarioId, revisions[^1], cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectPlanRevisionInfo>> ListRevisionsAsync(
        string projectId,
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ProjectPlanRevisionInfo>();
        foreach (var revision in ReadRevisionNumbers(GetScenarioDirectory(projectId, scenarioId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(ToInfo(await LoadAsync(projectId, scenarioId, revision, cancellationToken).ConfigureAwait(false)));
        }
        return result;
    }

    private static FileStream AcquireWriteLock(string directory)
    {
        try
        {
            return new FileStream(Path.Combine(directory, ".write.lock"), new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            });
        }
        catch (IOException ex)
        {
            throw new ProjectPlanConcurrencyException("Another writer is saving this planning scenario.", ex);
        }
    }

    private string GetScenarioDirectory(string projectId, string scenarioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            projectId + "\n" + scenarioId))).ToLowerInvariant();
        return Path.Combine(rootDirectory, key[..2], key);
    }

    private static string GetRevisionPath(string directory, int revision) =>
        Path.Combine(directory, $"{RevisionPrefix}{revision.ToString("D10", CultureInfo.InvariantCulture)}.json");

    private static List<int> ReadRevisionNumbers(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var revisions = new List<int>();
        foreach (var path in Directory.EnumerateFiles(directory, $"{RevisionPrefix}*.json", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == RevisionPrefix.Length + 10 &&
                int.TryParse(name.AsSpan(RevisionPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var revision) && revision > 0)
                revisions.Add(revision);
        }
        revisions.Sort();
        return revisions;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        var length = input.Length;
        if (length is <= 0 or > ProjectPlanCodec.MaximumDocumentBytes)
            throw new ProjectPlanFormatException("Stored planning document has an invalid size.");
        var bytes = GC.AllocateUninitializedArray<byte>((int)length);
        try
        {
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new ProjectPlanFormatException("Stored planning document changed while it was read.", ex);
        }
        return bytes;
    }

    private static ProjectPlanRevisionInfo ToInfo(ProjectPlanImport imported) => new(
        imported.Scenario.ProjectId,
        imported.Scenario.ScenarioId,
        imported.Scenario.Revision,
        imported.Scenario.Name,
        imported.SavedAt,
        imported.Evaluation.HasErrors,
        imported.Evaluation.HasUnresolved);
}
