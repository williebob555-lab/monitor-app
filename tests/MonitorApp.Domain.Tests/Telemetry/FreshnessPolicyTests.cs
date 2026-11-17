using MonitorApp.Domain.Telemetry;

namespace MonitorApp.Domain.Tests.Telemetry;

public sealed class FreshnessPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 16, 14, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FutureSkewTolerance = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateReturnsUnknownWhenNoObservationExists()
    {
        var policy = CreatePolicy();

        var result = policy.Evaluate(NumericObservation.Unknown());

        Assert.Equal(FreshnessState.Unknown, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateReturnsUnavailableWhenCollectorCannotProvideMetric()
    {
        var policy = CreatePolicy();

        var result = policy.Evaluate(NumericObservation.Unavailable());

        Assert.Equal(FreshnessState.Unavailable, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateReturnsOfflineForUnknownObservationWithExpiredContact()
    {
        var observation = NumericObservation.Unknown(
            Now - OfflineAfter - TimeSpan.FromTicks(1));

        var result = CreatePolicy().Evaluate(observation);

        Assert.Equal(FreshnessState.Offline, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateReturnsOfflineForUnavailableObservationWithExpiredContact()
    {
        var observation = NumericObservation.Unavailable(
            Now - OfflineAfter - TimeSpan.FromTicks(1));

        var result = CreatePolicy().Evaluate(observation);

        Assert.Equal(FreshnessState.Offline, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluatePreservesValidZeroAsFreshMeasurement()
    {
        var observation = NumericObservation.Valid(
            value: 0,
            observedAtUtc: Now,
            lastContactAtUtc: Now);

        var result = CreatePolicy().Evaluate(observation);

        Assert.Equal(0, observation.Value);
        Assert.Equal(FreshnessState.Fresh, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateTreatsExactStaleBoundaryAsFresh()
    {
        var observation = NumericObservation.Valid(
            value: 42,
            observedAtUtc: Now - StaleAfter,
            lastContactAtUtc: Now);

        var result = CreatePolicy().Evaluate(observation);

        Assert.Equal(FreshnessState.Fresh, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateTreatsObservationPastStaleBoundaryAsStale()
    {
        var observation = NumericObservation.Valid(
            value: 42,
            observedAtUtc: Now - StaleAfter - TimeSpan.FromTicks(1),
            lastContactAtUtc: Now);

        var result = CreatePolicy().Evaluate(observation);

        Assert.Equal(FreshnessState.Stale, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateTreatsExactOfflineBoundaryAsStale()
    {
        var observation = NumericObservation.Valid(
            value: 42,
            observedAtUtc: Now - OfflineAfter,
            lastContactAtUtc: Now - OfflineAfter);

        var result = CreatePolicy().Evaluate(observation);

        Assert.Equal(FreshnessState.Stale, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateTreatsContactPastOfflineBoundaryAsOffline()
    {
        var age = OfflineAfter + TimeSpan.FromTicks(1);
        var observation = NumericObservation.Valid(
            value: 42,
            observedAtUtc: Now - age,
            lastContactAtUtc: Now - age);

        var result = CreatePolicy().Evaluate(observation);

        Assert.Equal(FreshnessState.Offline, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateClampsTimestampWithinFutureSkewTolerance()
    {
        var future = Now + FutureSkewTolerance;
        var observation = NumericObservation.Valid(
            value: 42,
            observedAtUtc: future,
            lastContactAtUtc: future);

        var result = CreatePolicy().Evaluate(observation);

        Assert.Equal(FreshnessState.Fresh, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluatePreservesUnknownWithinFutureSkewTolerance()
    {
        var observation = NumericObservation.Unknown(Now + FutureSkewTolerance);

        var result = CreatePolicy().Evaluate(observation);

        Assert.Equal(FreshnessState.Unknown, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateRejectsObservedTimestampBeyondFutureSkewTolerance()
    {
        var future = Now + FutureSkewTolerance + TimeSpan.FromTicks(1);
        var observation = NumericObservation.Valid(
            value: 42,
            observedAtUtc: future,
            lastContactAtUtc: future);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreatePolicy().Evaluate(observation));

        Assert.Equal("ObservedAtUtc", exception.ParamName);
        Assert.Contains("allowed future skew", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateRejectsLastContactBeyondFutureSkewTolerance()
    {
        var observation = NumericObservation.Unavailable(
            Now + FutureSkewTolerance + TimeSpan.FromTicks(1));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreatePolicy().Evaluate(observation));

        Assert.Equal("LastContactAtUtc", exception.ParamName);
        Assert.Contains("allowed future skew", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidRejectsContactBeforeObservation()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            NumericObservation.Valid(
                value: 42,
                observedAtUtc: Now,
                lastContactAtUtc: Now - TimeSpan.FromSeconds(1)));

        Assert.Equal("lastContactAtUtc", exception.ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [Trait("Category", "Unit")]
    public void ValidRejectsNonFiniteValue(double value)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            NumericObservation.Valid(value, Now, Now));

        Assert.Equal("value", exception.ParamName);
    }

    [Theory]
    [InlineData("valid-observed")]
    [InlineData("valid-contact")]
    [InlineData("unknown-contact")]
    [InlineData("unavailable-contact")]
    [Trait("Category", "Unit")]
    public void ObservationFactoriesRejectNonUtcTimestamps(string scenario)
    {
        var nonUtc = new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.FromHours(1));

        var exception = Assert.Throws<ArgumentException>(() => scenario switch
        {
            "valid-observed" => NumericObservation.Valid(42, nonUtc, nonUtc),
            "valid-contact" => NumericObservation.Valid(42, Now, nonUtc),
            "unknown-contact" => NumericObservation.Unknown(nonUtc),
            "unavailable-contact" => NumericObservation.Unavailable(nonUtc),
            _ => throw new InvalidOperationException($"Unknown test scenario: {scenario}"),
        });

        Assert.Contains("UTC", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    [Trait("Category", "Unit")]
    public void ConstructorRejectsInvalidThresholds(
        int staleMinutes,
        int offlineMinutes)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
            new FreshnessPolicy(
                TimeSpan.FromMinutes(staleMinutes),
                TimeSpan.FromMinutes(offlineMinutes),
                FutureSkewTolerance,
                new FixedTimeProvider(Now)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConstructorRejectsNegativeFutureSkewTolerance()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FreshnessPolicy(
                StaleAfter,
                OfflineAfter,
                TimeSpan.FromTicks(-1),
                new FixedTimeProvider(Now)));

        Assert.Equal("futureSkewTolerance", exception.ParamName);
    }

    private static FreshnessPolicy CreatePolicy() =>
        new(
            StaleAfter,
            OfflineAfter,
            FutureSkewTolerance,
            new FixedTimeProvider(Now));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
