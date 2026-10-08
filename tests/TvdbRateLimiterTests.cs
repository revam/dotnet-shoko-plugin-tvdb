using System;
using System.Linq;
using System.Threading.Tasks;
using Shoko.Abstractions.Connectivity.Suspensions;
using Shoko.Plugin.Tvdb.Api;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The politeness budget: how fast requests are let through and how the bucket
/// refills, and the suspensions the limiter reports.
/// </summary>
public class TvdbRateLimiterTests
{
    [Fact]
    public void ABadBudget_IsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TvdbRateLimiter(maxTokens: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TvdbRateLimiter(tokensPerSecond: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TvdbRateLimiter(maxTokens: -1));
    }

    [Fact]
    public async Task ABurstUpToTheCapacity_IsLetStraightThrough()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var limiter = new TvdbRateLimiter(maxTokens: 3, tokensPerSecond: 1, timeProvider: time);

        for (var i = 0; i < 3; i++)
        {
            await limiter.WaitAsync(TestContext.Current.CancellationToken);
            limiter.Release();
        }

        Assert.Equal(0, limiter.AvailableTokens, precision: 5);
    }

    [Fact]
    public async Task TheBucketRefillsWithTime()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var limiter = new TvdbRateLimiter(maxTokens: 3, tokensPerSecond: 2, timeProvider: time);
        for (var i = 0; i < 3; i++)
        {
            await limiter.WaitAsync(TestContext.Current.CancellationToken);
            limiter.Release();
        }

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(2, limiter.AvailableTokens, precision: 5);
    }

    [Fact]
    public void TheBucketNeverRefillsPastItsCapacity()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var limiter = new TvdbRateLimiter(maxTokens: 3, tokensPerSecond: 2, timeProvider: time);

        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(3, limiter.AvailableTokens, precision: 5);
    }

    [Fact]
    public async Task ACancelledWait_ReleasesTheSlotItWasHoldingOnTo()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var limiter = new TvdbRateLimiter(maxTokens: 1, tokensPerSecond: 1, timeProvider: time);
        await limiter.WaitAsync(TestContext.Current.CancellationToken);
        limiter.Release();

        using var cancellation = new System.Threading.CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(cancellation.Token));

        // Had the slot leaked, this would deadlock rather than return.
        time.Advance(TimeSpan.FromSeconds(1));
        await limiter.WaitAsync(TestContext.Current.CancellationToken);
        limiter.Release();
    }

    [Fact]
    public void ARateLimit_IsReportedWithItsEnd_AndRunsOut()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var reporter = new FakeSuspensionReporter<TvdbSuspensionProvider>();
        using var limiter = new TvdbRateLimiter(timeProvider: time, reporter: reporter);

        limiter.NotifyRateLimited(TimeSpan.FromSeconds(30));

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), limiter.ResumesAt);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(30), reporter.Active[SuspensionKind.RateLimited].ResumesAt);
        Assert.False(reporter.Active[SuspensionKind.RateLimited].IsLiftable);

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.Null(limiter.ResumesAt);
    }

    [Fact]
    public void TheKinds_AreKeptApart_AndTheLatestIsWaitedFor()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var reporter = new FakeSuspensionReporter<TvdbSuspensionProvider>();
        using var limiter = new TvdbRateLimiter(timeProvider: time, reporter: reporter);

        limiter.NotifyServerError(TimeSpan.FromMinutes(5));
        limiter.NotifyRateLimited(TimeSpan.FromSeconds(5));

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(5), limiter.RateLimitedUntil);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(5), limiter.ServerErrorsUntil);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(5), limiter.ResumesAt);
        Assert.Equal([SuspensionKind.RateLimited, SuspensionKind.ServerErrors], reporter.Active.Keys.Order());
    }

    [Fact]
    public void AShorterWait_NeverCutsALongerOneShort()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var reporter = new FakeSuspensionReporter<TvdbSuspensionProvider>();
        using var limiter = new TvdbRateLimiter(timeProvider: time, reporter: reporter);

        limiter.NotifyRateLimited(TimeSpan.FromMinutes(5));
        limiter.NotifyRateLimited(TimeSpan.FromSeconds(5));

        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(5), limiter.ResumesAt);
        Assert.Equal(DateTime.UnixEpoch.AddMinutes(5), reporter.Active[SuspensionKind.RateLimited].ResumesAt);
    }

    [Fact]
    public void ANothingWait_IsNoSuspension()
    {
        var reporter = new FakeSuspensionReporter<TvdbSuspensionProvider>();
        using var limiter = new TvdbRateLimiter(reporter: reporter);

        limiter.NotifyServerError(TimeSpan.Zero);

        Assert.Null(limiter.ResumesAt);
        Assert.Empty(reporter.Active);
    }

    [Fact]
    public async Task ARefusedKey_IsSuspendedUntilLifted()
    {
        var reporter = new FakeSuspensionReporter<TvdbSuspensionProvider>();
        using var limiter = new TvdbRateLimiter(reporter: reporter);
        limiter.RefusedPin = ("key", "pin");

        limiter.RefuseCredentials(("key", null));

        var suspension = reporter.Active[SuspensionKind.AuthenticationFailed];
        Assert.True(suspension.IsLiftable);
        Assert.Null(suspension.ResumesAt);
        Assert.Null(limiter.ResumesAt);

        await new TvdbSuspensionProvider(limiter).Lift(SuspensionKind.AuthenticationFailed, TestContext.Current.CancellationToken);

        Assert.Empty(reporter.Active);
        Assert.Null(limiter.RefusedCredentials);
        Assert.Null(limiter.RefusedPin);
    }
}
