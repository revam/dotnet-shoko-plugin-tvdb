using System.Reflection;
using Moq;
using Shoko.Abstractions.Actions;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Plugin.Tvdb.Actions;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Services;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The rules the core enforces on actions at start-up, which take the whole
/// server down when broken, and when the actions refuse to run.
/// </summary>
public class TvdbActionTests
{
    private static IEnumerable<Type> Actions()
        => typeof(Plugin).Assembly.GetTypes().Where(type => type is { IsPublic: true, IsAbstract: false } && typeof(IExecutableAction).IsAssignableFrom(type));

    [Fact]
    public void ThereAreThreeActions()
        => Assert.Equal(3, Actions().Count());

    [Fact]
    public void EveryAction_DeclaresItsPermissionItself()
        => Assert.All(Actions(), type => Assert.Equal(type, type.GetProperty(nameof(IExecutableAction.Permission), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.DeclaringType));

    [Fact]
    public void EveryScopedAction_DerivesDirectlyFromItsBase()
        => Assert.All(Actions(), type => Assert.Equal(typeof(SeriesAction), type.BaseType));

    [Fact]
    public async Task WhileTheProviderIsSwitchedOff_NoActionRuns()
    {
        using var harness = new ServiceHarness();
        var provider = harness.Get<TvdbMetadataProvider>();
        harness.ProviderManager.Setup(manager => manager.IsProviderEnabled(provider)).Returns(false);

        var refusal = await new UnlinkTvdbSeriesAction(provider, harness.ProviderManager.Object, harness.Get<TvdbLinkingService>()).Validate(TestContext.Current.CancellationToken);

        Assert.Equal("The TheTVDB metadata provider is switched off.", refusal?.Reason);
    }

    [Fact]
    public async Task WhileTheProviderIsNotConfigured_OnlyTheActionsReachingTheTVDBAreRefused()
    {
        using var harness = new ServiceHarness(new() { ApiKey = null });
        var provider = harness.Get<TvdbMetadataProvider>();
        harness.ProviderManager.Setup(manager => manager.IsProviderEnabled(provider)).Returns(true);

        var refresh = await new RefreshTvdbSeriesAction(provider, harness.ProviderManager.Object, harness.RefreshService.Object).Validate(TestContext.Current.CancellationToken);
        var unlink = await new UnlinkTvdbSeriesAction(provider, harness.ProviderManager.Object, harness.Get<TvdbLinkingService>()).Validate(TestContext.Current.CancellationToken);

        Assert.Equal("No TheTVDB API key is configured.", refresh?.Reason);
        Assert.Null(unlink);
    }

    [Fact]
    public async Task WhileTheProviderIsPaused_OnlyTheActionsReachingTheTVDBAreRefused()
    {
        using var harness = new ServiceHarness();
        var provider = harness.Get<TvdbMetadataProvider>();
        harness.ProviderManager.Setup(manager => manager.IsProviderEnabled(provider)).Returns(true);
        harness.RateLimiter.Pause(TimeSpan.FromMinutes(5), TvdbApiClient.RateLimitedReason);

        var refresh = await new RefreshTvdbSeriesAction(provider, harness.ProviderManager.Object, harness.RefreshService.Object).Validate(TestContext.Current.CancellationToken);
        var unlink = await new UnlinkTvdbSeriesAction(provider, harness.ProviderManager.Object, harness.Get<TvdbLinkingService>()).Validate(TestContext.Current.CancellationToken);

        Assert.Equal(TvdbApiClient.RateLimitedReason, refresh?.Reason);
        Assert.Null(unlink);
    }
}
