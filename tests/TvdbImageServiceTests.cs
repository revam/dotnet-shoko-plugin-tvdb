using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The images the provider offers for each kind of entity, and the template
/// they complete.
/// </summary>
public class TvdbImageServiceTests
{
    private static async Task<(ServiceHarness Harness, TvdbMetadataProvider Provider)> Refreshed()
    {
        var harness = new ServiceHarness();
        await harness.Refresh();
        return (harness, harness.Get<TvdbMetadataProvider>());
    }

    [Fact]
    public async Task AShow_OffersItsArtworkWithItsDefaultPosterMarked()
    {
        var (harness, provider) = await Refreshed();
        using var _ = harness;

        var images = await provider.GetImages(TvdbUtility.SeriesGuid(81797), TestContext.Current.CancellationToken);

        Assert.NotNull(images);
        Assert.Equal(
            [
                ("series/81797/posters/5eec847d52a04.jpg", ImageEntityType.Primary, true),
                ("v4/series/81797/posters/63ab23cde990b.jpg", ImageEntityType.Primary, false),
                ("v4/series/81797/backgrounds/616009a8bd688.jpg", ImageEntityType.Backdrop, false),
                ("graphical/81797-g.jpg", ImageEntityType.Banner, false),
                ("v4/series/81797/clearlogo/611b6189d88b6.png", ImageEntityType.Logo, false),
            ],
            images.Select(image => (image.ResourceID, image.ImageType, image.IsDefault))
        );
        Assert.Equal(["ja", null, null, "en", "en"], images.Select(image => image.LanguageCode));
        Assert.Equal((680, 1000), (images[0].Width, images[0].Height));
    }

    [Fact]
    public async Task TheTemplate_IsRegisteredOnceBeforeTheFirstImage()
    {
        var (harness, provider) = await Refreshed();
        using var _ = harness;

        await provider.GetImages(TvdbUtility.SeriesGuid(81797), TestContext.Current.CancellationToken);
        await provider.GetImages(TvdbUtility.EpisodeGuid(361887), TestContext.Current.CancellationToken);

        harness.ImageManager.Verify(manager => manager.RegisterTemplateUrl(TvdbSources.Tvdb, "https://artworks.thetvdb.com/banners/{0}"), Times.Once);
    }

    [Fact]
    public async Task ASeason_OffersItsOwnArtworkWithItsPosterFirst()
    {
        var (harness, provider) = await Refreshed();
        using var _ = harness;

        var images = await provider.GetImages(TvdbUtility.SeasonGuid(31893), TestContext.Current.CancellationToken);

        Assert.Equal([("seasons/81797-1-3.jpg", true), ("seasons/81797-1-2.jpg", false)], images!.Select(image => (image.ResourceID, image.IsDefault)));
        Assert.All(images!, image => Assert.Equal(ImageEntityType.Primary, image.ImageType));
        Assert.Empty((await provider.GetImages(TvdbUtility.SeasonGuid(31895), TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task AnEpisode_OffersItsThumbnailAsItsBackdrop()
    {
        var (harness, provider) = await Refreshed();
        using var _ = harness;

        var image = Assert.Single((await provider.GetImages(TvdbUtility.EpisodeGuid(361887), TestContext.Current.CancellationToken))!);

        Assert.Equal(("v4/episode/361887/screencap/604df7d3ecf3a.jpg", ImageEntityType.Backdrop, true), (image.ResourceID, image.ImageType, image.IsDefault));
        Assert.Empty((await provider.GetImages(TvdbUtility.EpisodeGuid(361888), TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task APersonOrCharacter_OffersTheirPortrait()
    {
        var (harness, provider) = await Refreshed();
        using var _ = harness;

        Assert.Equal("person/412417/primary.jpg", Assert.Single((await provider.GetImages(TvdbUtility.CreatorGuid(412417), TestContext.Current.CancellationToken))!).ResourceID);
        Assert.Equal("person/412417/65afe1871bc9d.jpg", Assert.Single((await provider.GetImages(TvdbUtility.CharacterGuid(65111900), TestContext.Current.CancellationToken))!).ResourceID);
        Assert.Empty((await provider.GetImages(TvdbUtility.CreatorGuid(602), TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task WhatThePluginKeepsNoImagesFor_IsLeftAlone()
    {
        var (harness, provider) = await Refreshed();
        using var _ = harness;

        Assert.Null(await provider.GetImages(TvdbUtility.NetworkGuid(50), TestContext.Current.CancellationToken));
        Assert.Null(await provider.GetImages(TvdbUtility.SeriesGuid(1), TestContext.Current.CancellationToken));
        Assert.Null(await provider.GetImages(TvdbUtility.SeasonGuid(1), TestContext.Current.CancellationToken));
        Assert.Null(await provider.GetImages(new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, "81797"), TestContext.Current.CancellationToken));
    }
}
