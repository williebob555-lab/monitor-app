using Xunit;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Core.Tests;

public sealed class PairingShortCodeTests
{
    private static readonly byte[] Fingerprint = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void GeneratedCodeIsGroupedCrockfordAndNormalizesFromSloppyTyping()
    {
        var registry = new PairingShortCodeRegistry();
        var shown = registry.Issue("desktop", Fingerprint, TimeSpan.FromMinutes(10)).Code;

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$", shown);
        var normalized = PairingShortCode.Normalize(shown.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal));
        Assert.Equal(shown.Replace("-", string.Empty, StringComparison.Ordinal), normalized);
        Assert.Equal("0112345678", PairingShortCode.Normalize("oIl23-45678"));
        Assert.Null(PairingShortCode.Normalize("ABCDE-FGHIU"));
        Assert.Null(PairingShortCode.Normalize("ABCDE"));
        Assert.Null(PairingShortCode.Normalize("ABCDE-FGHJK-M"));
        Assert.Null(PairingShortCode.Normalize(null));
    }

    [Fact]
    public void CodeRedeemsExactlyOnceAndTheProofVerifiesOnlyWithTheRightCode()
    {
        var registry = new PairingShortCodeRegistry();
        var issue = registry.Issue("desktop", Fingerprint, TimeSpan.FromMinutes(10));
        var keys = PairingShortCode.Derive(PairingShortCode.Normalize(issue.Code)!);

        var first = registry.Redeem(keys.LookupId);
        var second = registry.Redeem(keys.LookupId);

        Assert.NotNull(first);
        Assert.Equal("desktop", first.PeerId);
        Assert.Equal(Fingerprint, first.FingerprintSha256);
        Assert.Equal(PairingShortCode.ComputeProof(keys.ProofKey, keys.LookupId, "desktop", Fingerprint), first.Proof);
        var other = PairingShortCode.Derive("0000000000");
        Assert.NotEqual(PairingShortCode.ComputeProof(other.ProofKey, keys.LookupId, "desktop", Fingerprint), first.Proof);
        Assert.NotEqual(PairingShortCode.ComputeProof(keys.ProofKey, keys.LookupId, "impostor", Fingerprint), first.Proof);
        Assert.Null(second);
    }

    [Fact]
    public void ExpiredCodeIsRefusedIdenticallyToAnUnknownOne()
    {
        var time = new ManualTime();
        var registry = new PairingShortCodeRegistry(time);
        var start = time.GetUtcNow();
        var issue = registry.Issue("desktop", Fingerprint, TimeSpan.FromMinutes(10));
        var keys = PairingShortCode.Derive(PairingShortCode.Normalize(issue.Code)!);
        time.Advance(TimeSpan.FromMinutes(9));
        Assert.NotNull(registry.Redeem(keys.LookupId));
        var late = registry.Issue("desktop", Fingerprint, TimeSpan.FromMinutes(10));
        var lateKeys = PairingShortCode.Derive(PairingShortCode.Normalize(late.Code)!);
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(start + TimeSpan.FromMinutes(10), issue.ExpiresAt);
        keys = lateKeys;
        Assert.Null(registry.Redeem(keys.LookupId));
        Assert.Null(registry.Redeem(new byte[PairingShortCode.LookupIdSize]));
        Assert.Null(registry.Redeem(new byte[3]));
    }

    [Fact]
    public void RepeatedWrongGuessesLockRedemptionEvenForTheCorrectCodeUntilTheWindowPasses()
    {
        var time = new ManualTime();
        var registry = new PairingShortCodeRegistry(time, maximumFailuresPerWindow: 3, failureWindow: TimeSpan.FromMinutes(1));
        var issue = registry.Issue("desktop", Fingerprint, TimeSpan.FromMinutes(10));
        var keys = PairingShortCode.Derive(PairingShortCode.Normalize(issue.Code)!);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(registry.Redeem(Enumerable.Repeat((byte)attempt, PairingShortCode.LookupIdSize).ToArray()));
        }

        Assert.Null(registry.Redeem(keys.LookupId));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(registry.Redeem(keys.LookupId));
    }

    [Fact]
    public void LiveCodeCountIsBoundedAndOldestIsDropped()
    {
        var time = new ManualTime();
        var registry = new PairingShortCodeRegistry(time, maximumLiveCodes: 2);
        var codes = new List<byte[]>();
        for (var index = 0; index < 3; index++)
        {
            codes.Add(PairingShortCode.Derive(PairingShortCode.Normalize(
                registry.Issue("desktop", Fingerprint, TimeSpan.FromMinutes(10)).Code)!).LookupId);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Null(registry.Redeem(codes[0]));
        Assert.NotNull(registry.Redeem(codes[1]));
        Assert.NotNull(registry.Redeem(codes[2]));
    }

    internal sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
