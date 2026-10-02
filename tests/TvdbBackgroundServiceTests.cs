using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Services;
using Shoko.Plugin.Tvdb.Storage;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// What the plugin does once at start-up after an upgrade.
/// </summary>
public sealed class TvdbBackgroundServiceTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "tvdb-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [Fact]
    public async Task LinkedShowsWithNothingStored_AreAllRefreshedOnce()
    {
        using var harness = new ServiceHarness();
        harness.CrossReferences.AddSeries(1, 81797);
        harness.RefreshService
            .Setup(service => service.RefreshAllLinked(TvdbSources.Tvdb, false, It.IsAny<MetadataRefreshOptions?>(), MetadataEntityType.Series, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        Assert.Equal(1, await harness.Get<TvdbBackgroundService>().RefreshIfNothingStored(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithAShowStoredAlready_NothingIsRefreshed()
    {
        using var harness = new ServiceHarness();
        harness.CrossReferences.AddSeries(1, 81797);
        await harness.Refresh();

        Assert.Equal(0, await harness.Get<TvdbBackgroundService>().RefreshIfNothingStored(TestContext.Current.CancellationToken));
        harness.RefreshService.Verify(service => service.RefreshAllLinked(It.IsAny<MetadataSource>(), It.IsAny<bool>(), It.IsAny<MetadataRefreshOptions?>(), It.IsAny<MetadataEntityType?>(), It.IsAny<IProgress<decimal>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WithNothingLinked_NothingIsRefreshed()
    {
        using var harness = new ServiceHarness();

        Assert.Equal(0, await harness.Get<TvdbBackgroundService>().RefreshIfNothingStored(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheOldStoreFile_IsDeletedWithItsDirectory()
    {
        using var harness = new ServiceHarness();
        harness.ApplicationPaths.SetupGet(paths => paths.DataPath).Returns(_dataPath);
        Directory.CreateDirectory(Path.Combine(_dataPath, "Tvdb"));
        File.WriteAllText(Path.Combine(_dataPath, "Tvdb", "store.json"), "{}");
        var service = harness.Get<TvdbBackgroundService>();

        Assert.True(service.RemoveOldStore());
        Assert.False(Directory.Exists(Path.Combine(_dataPath, "Tvdb")));
        Assert.False(service.RemoveOldStore());
    }
}
