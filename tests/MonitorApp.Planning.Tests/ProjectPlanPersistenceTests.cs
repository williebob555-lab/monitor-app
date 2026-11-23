using System.Text.Json;
using System.Text.Json.Nodes;
using MonitorApp.Planning.Audio;
using MonitorApp.Planning.Projects;
using MonitorApp.Planning.Racks;
using Xunit;

namespace MonitorApp.Planning.Tests;

[Trait("Category", "Unit")]
public sealed class ProjectPlanPersistenceTests
{
    private static readonly DateTimeOffset SavedAt = new(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VersionedCodecRoundTripsRackAudioOffersTasksAndFindings()
    {
        var scenario = Scenario(4);
        var codec = new ProjectPlanCodec();

        var imported = codec.Import(ProjectPlanCodec.Export(scenario, SavedAt));

        Assert.Equal(ProjectPlanCodec.CurrentSchemaVersion, imported.SchemaVersion);
        Assert.Equal(SavedAt, imported.SavedAt);
        Assert.Equal(scenario.ProjectId, imported.Scenario.ProjectId);
        Assert.Equal(scenario.RackPlan!.ScenarioId, imported.Scenario.RackPlan!.ScenarioId);
        Assert.Equal(Assert.Single(scenario.RackPlan.Zones), Assert.Single(imported.Scenario.RackPlan.Zones));
        Assert.Equal(Assert.Single(scenario.RackPlan.Equipment), Assert.Single(imported.Scenario.RackPlan.Equipment));
        Assert.Equal(Assert.Single(scenario.RackPlan.Placements), Assert.Single(imported.Scenario.RackPlan.Placements));
        Assert.NotNull(imported.Scenario.AudioPlan);
        Assert.Equal("patch", Assert.Single(imported.Scenario.AudioPlan!.Patches).Id);
        Assert.False(imported.Evaluation.HasErrors);
    }

    [Fact]
    public void ImportRejectsTamperingFutureVersionsAndUnregisteredLegacyVersions()
    {
        var codec = new ProjectPlanCodec();
        var root = JsonNode.Parse(ProjectPlanCodec.Export(Scenario(4), SavedAt))!.AsObject();
        root["scenario"]!["name"] = "tampered";
        Assert.Throws<ProjectPlanFormatException>(() => codec.Import(JsonSerializer.SerializeToUtf8Bytes(root)));

        root = JsonNode.Parse(ProjectPlanCodec.Export(Scenario(4), SavedAt))!.AsObject();
        root["schemaVersion"] = ProjectPlanCodec.CurrentSchemaVersion + 1;
        Assert.Throws<ProjectPlanVersionException>(() => codec.Import(JsonSerializer.SerializeToUtf8Bytes(root)));

        root["schemaVersion"] = 0;
        var legacy = JsonSerializer.SerializeToUtf8Bytes(root);
        Assert.Throws<ProjectPlanVersionException>(() => codec.Import(legacy));
        var migrated = new ProjectPlanCodec([new VersionZeroMigration()]).Import(legacy);
        Assert.Equal(ProjectPlanCodec.CurrentSchemaVersion, migrated.SchemaVersion);
    }

    [Fact]
    public void ImportRejectsMalformedStructureAndOversizedInput()
    {
        var codec = new ProjectPlanCodec();
        var root = JsonNode.Parse(ProjectPlanCodec.Export(Scenario(4), SavedAt))!.AsObject();
        root["scenario"]!.AsObject().Remove("parts");
        Assert.Throws<ProjectPlanFormatException>(() =>
            codec.Import(JsonSerializer.SerializeToUtf8Bytes(root)));

        root = JsonNode.Parse(ProjectPlanCodec.Export(Scenario(4), SavedAt))!.AsObject();
        root["format"] = 42;
        Assert.Throws<ProjectPlanFormatException>(() =>
            codec.Import(JsonSerializer.SerializeToUtf8Bytes(root)));

        Assert.Throws<ProjectPlanFormatException>(() =>
            codec.Import(new byte[ProjectPlanCodec.MaximumDocumentBytes + 1]));
    }

    [Fact]
    public void ImportRejectsUnknownNumericAndNullNestedContentAsFormatErrors()
    {
        var codec = new ProjectPlanCodec();

        var root = JsonNode.Parse(ProjectPlanCodec.Export(Scenario(4), SavedAt))!.AsObject();
        root["unexpected"] = true;
        Assert.Throws<ProjectPlanFormatException>(() =>
            codec.Import(JsonSerializer.SerializeToUtf8Bytes(root)));

        root = JsonNode.Parse(ProjectPlanCodec.Export(Scenario(4), SavedAt))!.AsObject();
        root["scenario"]!["tasks"]![0]!["state"] = (int)ProjectWorkState.Completed;
        Assert.Throws<ProjectPlanFormatException>(() =>
            codec.Import(JsonSerializer.SerializeToUtf8Bytes(root)));

        root = JsonNode.Parse(ProjectPlanCodec.Export(Scenario(4), SavedAt))!.AsObject();
        root["scenario"]!["tasks"]![0]!["dependsOn"] = null;
        Assert.Throws<ProjectPlanFormatException>(() =>
            codec.Import(JsonSerializer.SerializeToUtf8Bytes(root)));

        root = JsonNode.Parse(ProjectPlanCodec.Export(Scenario(4), SavedAt))!.AsObject();
        root["scenario"] = null;
        Assert.Throws<ProjectPlanFormatException>(() =>
            codec.Import(JsonSerializer.SerializeToUtf8Bytes(root)));
    }

    [Fact]
    public void ExportRejectsDefaultSaveTimestamp()
    {
        Assert.Throws<ProjectPlanFormatException>(() =>
            ProjectPlanCodec.Export(Scenario(4), default(DateTimeOffset)));
    }

    [Fact]
    public async Task StoreKeepsImmutableHistoryLoadsLatestAndRejectsRevisionGaps()
    {
        var root = CreateTestDirectory();
        try
        {
            var time = new FixedTimeProvider(SavedAt);
            var store = new ProjectPlanStore(root, timeProvider: time);
            await store.SaveAsync(Scenario(4), expectedPreviousRevision: 0);
            time.UtcNow = SavedAt.AddMinutes(1);
            await store.SaveAsync(Scenario(5), expectedPreviousRevision: 4);

            var revisions = await store.ListRevisionsAsync("project:rack", "candidate-a");
            Assert.Equal([4, 5], revisions.Select(x => x.Revision));
            Assert.Equal(SavedAt.AddMinutes(1), revisions[1].SavedAt);
            var latest = await store.LoadLatestAsync("project:rack", "candidate-a");
            Assert.Equal(5, latest!.Scenario.Revision);
            Assert.Equal(4, (await store.LoadAsync("project:rack", "candidate-a", 4)).Scenario.Revision);

            await Assert.ThrowsAsync<ProjectPlanConcurrencyException>(() =>
                store.SaveAsync(Scenario(7), expectedPreviousRevision: 5));
            await Assert.ThrowsAsync<ProjectPlanConcurrencyException>(() =>
                store.SaveAsync(Scenario(6), expectedPreviousRevision: 4));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    [Fact]
    public async Task ConcurrentWritersCannotCreateTwoCopiesOfOneRevision()
    {
        var root = CreateTestDirectory();
        try
        {
            var store = new ProjectPlanStore(root, timeProvider: new FixedTimeProvider(SavedAt));
            await store.SaveAsync(Scenario(4), expectedPreviousRevision: 0);

            async Task<Exception?> AttemptAsync()
            {
                try
                {
                    await store.SaveAsync(Scenario(5), expectedPreviousRevision: 4);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            var attempts = await Task.WhenAll(Task.Run(AttemptAsync), Task.Run(AttemptAsync));
            Assert.Single(attempts, x => x is null);
            Assert.Single(attempts, x => x is ProjectPlanConcurrencyException);
            Assert.Equal([4, 5], (await store.ListRevisionsAsync("project:rack", "candidate-a"))
                .Select(x => x.Revision));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    [Fact]
    public async Task CorruptStoredRevisionFailsClosedWithoutReplacingEarlierHistory()
    {
        var root = CreateTestDirectory();
        try
        {
            var store = new ProjectPlanStore(root, timeProvider: new FixedTimeProvider(SavedAt));
            await store.SaveAsync(Scenario(4), expectedPreviousRevision: 0);
            await store.SaveAsync(Scenario(5), expectedPreviousRevision: 4);
            var newest = Directory.EnumerateFiles(root, "revision-*.json", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Last();
            await File.WriteAllTextAsync(newest, "{ truncated");

            await Assert.ThrowsAsync<ProjectPlanFormatException>(() =>
                store.LoadAsync("project:rack", "candidate-a", 5));
            Assert.Equal(4, (await store.LoadAsync("project:rack", "candidate-a", 4)).Scenario.Revision);
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    [Fact]
    public async Task StoreRejectsOversizedRevisionBeforeReadingIt()
    {
        var root = CreateTestDirectory();
        try
        {
            var store = new ProjectPlanStore(root, timeProvider: new FixedTimeProvider(SavedAt));
            await store.SaveAsync(Scenario(4), expectedPreviousRevision: 0);
            var revision = Assert.Single(Directory.EnumerateFiles(
                root, "revision-*.json", SearchOption.AllDirectories));
            await using (var file = new FileStream(revision, FileMode.Open, FileAccess.Write, FileShare.None))
                file.SetLength(ProjectPlanCodec.MaximumDocumentBytes + 1L);

            await Assert.ThrowsAsync<ProjectPlanFormatException>(() =>
                store.LoadAsync("project:rack", "candidate-a", 4));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    [Fact]
    public void ComparisonReportsObjectCostAndNewOrResolvedFindingChanges()
    {
        var before = Scenario(4) with
        {
            Offers = [Offer(shipping: null, tax: null)],
            RackPlan = Scenario(4).RackPlan! with
            {
                Equipment = [Assert.Single(Scenario(4).RackPlan!.Equipment) with { BodyDepthMm = null }]
            },
        };
        var afterBase = Scenario(5);
        var after = afterBase with
        {
            Parts = [.. afterBase.Parts, new PlanPart("patchbay", "Patchbay", 1, null, null, null, null, Optional: true)],
            Tasks = [Assert.Single(afterBase.Tasks) with { State = ProjectWorkState.Completed }],
            AudioPlan = afterBase.AudioPlan! with
            {
                Ports = afterBase.AudioPlan.Ports.Select(x => x.Id == "out" ? x with { Channels = null } : x).ToArray()
            },
        };

        var comparison = ProjectPlanComparer.Compare(before, after);

        Assert.Contains(comparison.ObjectChanges,
            x => x.Category == "part" && x.ObjectId == "patchbay" && x.Kind == PlanChangeKind.Added);
        Assert.Contains(comparison.ObjectChanges,
            x => x.Category == "task" && x.Kind == PlanChangeKind.Modified);
        Assert.Contains(comparison.FindingChanges,
            x => x.Kind == PlanFindingChangeKind.Resolved && x.Before?.Code == "unknown-equipment-dimensions");
        Assert.Contains(comparison.FindingChanges,
            x => x.Kind == PlanFindingChangeKind.Introduced && x.After?.Code == "unknown-channels");
        var dollars = Assert.Single(comparison.CostChanges, x => x.Currency == "USD");
        Assert.False(dollars.BeforeComplete);
        Assert.True(dollars.AfterComplete);
        Assert.Equal(115m, dollars.AfterGrandTotal);
    }

    private static ProjectScenario Scenario(int revision)
    {
        var rack = new RackLayout("candidate-a", revision,
            [new RackZone
            {
                Id = "rack",
                Format = RackFormat.Eia19,
                WidthMm = 482.6m,
                HeightMm = 4 * RackDimensions.RackUnitMm,
                UsableDepthMm = 500,
                RackUnits = 4,
            }],
            [new RackEquipment
            {
                Id = "interface",
                Name = "Audio interface",
                MountFormat = RackFormat.Eia19,
                MountMethod = MountingMethod.FrontRails,
                WidthMm = 440,
                HeightMm = 43,
                BodyDepthMm = 220,
                RackUnitsTall = 1,
                WeightKg = 3,
                PowerWatts = 20,
                HeatWatts = 18,
            }],
            [RackDimensions.AtRackUnits("interface-placement", "rack", "interface", 1)]);
        var audio = new AudioPlan("candidate-a", revision,
            [new AudioDevice("source", "Source"), new AudioDevice("interface", "Interface", "interface")],
            [
                Port("out", "source", AudioPortDirection.Output),
                Port("in", "interface", AudioPortDirection.Input),
            ],
            [new AudioCable
            {
                Id = "cable",
                EndA = AudioConnector.Trs635,
                EndB = AudioConnector.Trs635,
                MeasuredLengthMm = 1000,
                RequiredRoutedLengthMm = 900,
                Shielded = true,
            }],
            [new AudioPatch("patch", "out", "in", "cable")],
            [], [], []);
        return new ProjectScenario("project:rack", "candidate-a", revision, "Rack reorganization",
            [new PlanRequirement("r1", "Route audio")],
            [new PlanPart("interface", "Audio interface", 1, "product:interface", "offer:interface", null, "r1")],
            [new PlanTask("install", "Install interface", ProjectWorkState.NotStarted, [], ["interface"])],
            [Offer(shipping: 5, tax: 10)], rack, audio);
    }

    private static AudioPort Port(string id, string deviceId, AudioPortDirection direction) => new()
    {
        Id = id,
        DeviceId = deviceId,
        Direction = direction,
        Kind = AudioSignalKind.AnalogAudio,
        Connector = AudioConnector.Trs635,
        Level = AudioLevel.ProfessionalLine,
        Channels = 1,
        Balanced = true,
        ImpedanceOhms = direction == AudioPortDirection.Input ? 10_000 : 100,
        MaximumLevelDbu = 18,
        ProcessingLatencyMs = 0,
    };

    private static PlanOfferSnapshot Offer(decimal? shipping, decimal? tax) => new(
        "offer:interface", "product:interface", "fixture seller", "USD", 100, shipping, tax,
        OfferAvailability.InStock, SavedAt, "fixture", SourceKind.UserEntered, null);

    private static string CreateTestDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "monitor-app-planning-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static void DeleteTestDirectory(string root)
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "monitor-app-planning-tests")) +
                             Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(root);
        Assert.StartsWith(expectedParent, resolved, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class VersionZeroMigration : IProjectPlanMigration
    {
        public int FromVersion => 0;
        public int ToVersion => 1;
        public JsonObject Migrate(JsonObject document)
        {
            document["schemaVersion"] = ToVersion;
            return document;
        }
    }
}
