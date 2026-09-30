using System;
using System.Threading.Tasks;
using Shoko.Plugin.Tvdb.Api;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The politeness budget: how fast requests are let through and how the bucket
/// refills.
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
    public void APause_LastsUntilItRunsOutAndSaysWhy()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var limiter = new TvdbRateLimiter(timeProvider: time);
        var raised = 0;
        limiter.PauseStateChanged += (_, _) => raised++;

        limiter.Pause(TimeSpan.FromSeconds(30), "Because.");

        Assert.True(limiter.IsPaused);
        Assert.Equal("Because.", limiter.PauseReason);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), limiter.ResumesAt);
        Assert.Equal(1, raised);

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.False(limiter.IsPaused);
        Assert.Null(limiter.PauseReason);
        Assert.Null(limiter.ResumesAt);
    }

    [Fact]
    public void AShorterPause_NeverCutsALongerOneShort()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var limiter = new TvdbRateLimiter(timeProvider: time);

        limiter.Pause(TimeSpan.FromMinutes(5), "Long.");
        limiter.Pause(TimeSpan.FromSeconds(5), "Short.");

        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(5), limiter.ResumesAt);
        Assert.Equal("Short.", limiter.PauseReason);
    }

    [Fact]
    public void Resuming_LiftsThePauseAtOnce()
    {
        using var limiter = new TvdbRateLimiter();
        var raised = 0;
        limiter.PauseStateChanged += (_, _) => raised++;
        limiter.Pause(TimeSpan.FromHours(1), "Because.");

        limiter.Resume();
        limiter.Resume();

        Assert.False(limiter.IsPaused);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void ANothingPause_IsNoPause()
    {
        using var limiter = new TvdbRateLimiter();

        limiter.Pause(TimeSpan.Zero, "Because.");

        Assert.False(limiter.IsPaused);
        Assert.Throws<ArgumentException>(() => limiter.Pause(TimeSpan.FromSeconds(1), " "));
    }

    [Fact]
    public async Task APause_RaisesItsEndWhenItRunsOut()
    {
        using var limiter = new TvdbRateLimiter();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        limiter.Pause(TimeSpan.FromMilliseconds(50), "Because.");
        limiter.PauseStateChanged += (_, _) =>
        {
            if (!limiter.IsPaused)
                ended.TrySetResult();
        };

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(limiter.IsPaused);
    }
}
