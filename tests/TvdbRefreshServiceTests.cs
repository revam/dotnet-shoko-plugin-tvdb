using System.Net;
using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// What a refresh writes into the core's stores, and what it leaves out.
/// </summary>
public class TvdbRefreshServiceTests
{
    private static readonly MetadataGuid _seriesID = TvdbUtility.SeriesGuid(81797);

    [Fact]
    public async Task ARefresh_WritesTheShowWithItsSeasonsAndEpisodes()
    {
        using var harness = new ServiceHarness();

        await harness.Refresh();

        var series = harness.Series.Series[_seriesID];
        Assert.Equal([1, 2, 0], series.Seasons.Select(season => season.SeasonNumber));
        Assert.Equal([361887, 361888, 619671], series.Episodes.Select(episode => episode.ID.GetNumericID<int>()));
        Assert.Contains(series.Titles, title => title is { Type: TitleType.Main, Value: "ワンピース" });
        Assert.Contains(series.Episodes[0].Titles, title => title is { Type: TitleType.Official, Value: "I'm Luffy! The Man Who's Gonna Be King of the Pirates!", LanguageCode: "en" });
        Assert.Equal(["BR", "ES", "US"], series.ContentRatings.Select(rating => rating.CountryCode));
        Assert.Contains(series.Resources, resource => resource.ID == "tt0388629");
    }

    [Fact]
    public async Task ARefresh_WritesWhatTheOtherStoresHoldForTheShow()
    {
        using var harness = new ServiceHarness();

        await harness.Refresh();

        Assert.Equal(["Action", "Anime", "Story-based Drama"], harness.Tags.GetTags(_seriesID).Select(tag => tag.Name));
        Assert.Equal(["Toei Animation", "Shueisha"], harness.Studios.GetStudios(_seriesID).Select(studio => studio.Name));
        Assert.Equal(["Fuji TV", "Adult Swim"], harness.Studios.GetNetworks(_seriesID).Select(network => network.Name));
        Assert.Equal(2, harness.People.GetCast(_seriesID).Count);
        Assert.Equal(2, harness.People.GetCrew(_seriesID).Count);
        Assert.Equal("person/412417/primary.jpg", harness.Store.GetPortrait(TvdbUtility.CreatorGuid(412417)));
    }

    [Fact]
    public async Task ARefresh_KeepsTheShowsOwnDocument()
    {
        using var harness = new ServiceHarness();

        await harness.Refresh();

        var show = harness.Store.GetShow(81797);
        Assert.NotNull(show);
        Assert.Equal("one-piece", show.Slug);
        Assert.Equal(["absolute", "dvd"], show.AlternateSeasonTypes);
    }

    [Fact]
    public async Task TheOtherSeasonTypes_AreStoredAsOrderingsOfTheShow()
    {
        using var harness = new ServiceHarness();

        await harness.Refresh();

        Assert.Equal(["tvdb://ordering/81797-absolute", "tvdb://ordering/81797-dvd"], harness.Orderings.Orderings.Keys.Select(id => id.ToString()).Order());
        Assert.All(harness.Orderings.Orderings.Values, ordering => Assert.Equal(_seriesID, ordering.SeriesID));
    }

    [Fact]
    public async Task AnOrderingTheShowNoLongerHas_IsRemoved()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        harness.Orderings.SaveOrdering(new() { ID = TvdbUtility.OrderingGuid(81797, "alternate"), SeriesID = _seriesID });

        await harness.Refresh();

        Assert.DoesNotContain(TvdbUtility.OrderingGuid(81797, "alternate"), harness.Orderings.Orderings.Keys);
        Assert.Equal(2, harness.Orderings.Orderings.Count);
    }

    [Fact]
    public async Task TheShowsTranslations_ComeWithItsRecordAndItsEpisodesAreFetchedInTheOtherLanguages()
    {
        using var harness = new ServiceHarness();

        await harness.Refresh();

        Assert.Contains("series/81797/extended?meta=translations", harness.Http.Paths);
        Assert.DoesNotContain(harness.Http.Paths, path => path.Contains("/translations/", StringComparison.Ordinal));
        Assert.Contains("series/81797/episodes/default/eng?page=0", harness.Http.Paths);
        Assert.DoesNotContain("series/81797/episodes/default/jpn?page=0", harness.Http.Paths);
        Assert.Contains(harness.Series.Series[_seriesID].Titles, title => title is { Type: TitleType.Official, Value: "One Piece", LanguageCode: "en" });
    }

    [Fact]
    public async Task AQuickRefreshInThreeLanguages_AsksForTheRecordAndTheEpisodePagesOnly()
    {
        using var harness = new ServiceHarness();
        harness.OverviewOrder.AddRange([TitleLanguage.Japanese, TitleLanguage.German]);

        await harness.Refresh(new() { QuickRefresh = true });

        Assert.Equal(
            [
                "login",
                "series/81797/extended?meta=translations",
                "series/81797/episodes/default?page=0",
                "series/81797/episodes/default?page=1",
                "series/81797/episodes/default/eng?page=0",
                "series/81797/episodes/default/deu?page=0",
            ],
            harness.Http.Paths
        );
        Assert.Contains(harness.Series.Series[_seriesID].Overviews, description => description.LanguageCode == "de");
    }

    [Fact]
    public async Task TheShow_KeepsItsNamesAndOverviewsInTheCoresLanguagesAndEnglishOnly()
    {
        using var harness = new ServiceHarness();
        harness.SeriesTitleOrder.Clear();
        harness.SeriesTitleOrder.AddRange([TitleLanguage.Main, TitleLanguage.German]);
        harness.OverviewOrder.Clear();

        await harness.Refresh();

        var series = harness.Series.Series[_seriesID];
        Assert.Contains(series.Titles, title => title is { Type: TitleType.Official, LanguageCode: "de" });
        Assert.Contains(series.Titles, title => title is { Type: TitleType.Official, LanguageCode: "en" });
        Assert.DoesNotContain(series.Titles, title => title is { Type: TitleType.Official, LanguageCode: "it" });
        Assert.Contains(series.Overviews, overview => overview.LanguageCode is "en");
        Assert.DoesNotContain(series.Overviews, overview => overview.LanguageCode is "de" or "it");
    }

    [Fact]
    public async Task TheDownloadAllSwitches_KeepEveryTextOfTheShowButFetchNoMoreEpisodes()
    {
        using var all = new ServiceHarness(new() { ApiKey = "api-key", DownloadAllTitles = true, DownloadAllOverviews = true });
        using var ordered = new ServiceHarness();

        await all.Refresh(new() { QuickRefresh = true });
        await ordered.Refresh(new() { QuickRefresh = true });

        var series = all.Series.Series[_seriesID];
        Assert.Contains(series.Titles, title => title is { Type: TitleType.Official, LanguageCode: "it" });
        Assert.Contains(series.Overviews, overview => overview.LanguageCode is "it");
        Assert.Equal(ordered.Http.Paths, all.Http.Paths);
    }

    [Fact]
    public async Task TheEpisodes_AreFetchedInTheCoresEpisodeTitleAndDescriptionLanguagesOnly()
    {
        using var harness = new ServiceHarness();
        harness.EpisodeTitleOrder.Clear();
        harness.EpisodeTitleOrder.AddRange([TitleLanguage.Main, TitleLanguage.Romaji, TitleLanguage.German]);
        harness.OverviewOrder.Clear();
        harness.OverviewOrder.Add(TitleLanguage.French);

        await harness.Refresh(new() { QuickRefresh = true });

        var translated = harness.Http.Paths.Where(path => path.StartsWith("series/81797/episodes/default/", StringComparison.Ordinal));
        Assert.Equal(["series/81797/episodes/default/deu?page=0", "series/81797/episodes/default/fra?page=0"], translated);
    }

    [Fact]
    public async Task AQuickRefresh_LeavesOutTheCastTheOrderingsAndTheMatching()
    {
        using var harness = new ServiceHarness();
        harness.CrossReferences.AddSeries(1, 81797);

        await harness.Refresh(new() { QuickRefresh = true });

        Assert.True(harness.Series.Series.ContainsKey(_seriesID));
        Assert.Empty(harness.People.GetCast(_seriesID));
        Assert.Empty(harness.Orderings.Orderings);
        Assert.DoesNotContain("series/81797/episodes/dvd?page=0", harness.Http.Paths);
        harness.LinkingService.Verify(service => service.MatchEpisodes(It.IsAny<int>(), It.IsAny<MetadataGuid>(), It.IsAny<MetadataGuid?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheOptions_OverrideTheSettings()
    {
        using var harness = new ServiceHarness();

        await harness.Refresh(new() { DownloadNetworks = false, DownloadCrewAndCast = false, DownloadAlternateOrdering = false });

        Assert.Empty(harness.Studios.GetNetworks(_seriesID));
        Assert.Empty(harness.People.GetCast(_seriesID));
        Assert.Empty(harness.Orderings.Orderings);
    }

    [Fact]
    public async Task TheSettings_DecideWhereTheOptionsLeaveItOpen()
    {
        using var harness = new ServiceHarness(new() { ApiKey = "api-key", AutoDownloadNetworks = false, AutoDownloadCastAndCrew = false, AutoDownloadAlternateOrderings = false });

        await harness.Refresh();

        Assert.Empty(harness.Studios.GetNetworks(_seriesID));
        Assert.Empty(harness.People.GetCrew(_seriesID));
        Assert.Empty(harness.Orderings.Orderings);

        await harness.Refresh(new() { DownloadAlternateOrdering = true });

        Assert.Equal(2, harness.Orderings.Orderings.Count);
    }

    [Fact]
    public async Task ARefresh_MatchesTheEpisodesOfTheAnimeLinkedToTheShow()
    {
        using var harness = new ServiceHarness();
        harness.CrossReferences.AddSeries(1, 81797).AddSeries(2, 12345);

        await harness.Refresh();

        harness.LinkingService.Verify(service => service.MatchEpisodes(1, _seriesID, null, true, true, null, It.IsAny<CancellationToken>()), Times.Once);
        harness.LinkingService.Verify(service => service.MatchEpisodes(2, It.IsAny<MetadataGuid>(), It.IsAny<MetadataGuid?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnEpisodeTheShowNoLongerHas_IsDroppedFromTheSave_AndItsLinksLeftToTheCore()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        harness.CrossReferences.AddEpisode(1, 1001, 81797, 619671).AddEpisode(1, 1002, 81797, 361887);
        harness.Http.Route("series/81797/episodes/default?page=0", LastPage(Fixture.Read("series-81797-episodes-page0.json")));

        await harness.Refresh();

        // The core's series store removes the links to what a save drops;
        // the fake store does not, so both links are still there.
        Assert.DoesNotContain(TvdbUtility.EpisodeGuid(619671), harness.Series.Series[_seriesID].Episodes.Select(episode => episode.ID));
        Assert.Equal(2, harness.CrossReferences.Links.Count);
    }

    [Fact]
    public async Task AnAnswerWithoutEpisodes_FailsTheRefreshAndLeavesTheShowAlone()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        harness.CrossReferences.AddEpisode(1, 1001, 81797, 361887);
        harness.Http
            .Route("series/81797/episodes/default?page=0", Fixture.Read("not-found.json"), HttpStatusCode.NotFound)
            .Route("series/81797/episodes/default/eng?page=0", Fixture.Read("not-found.json"), HttpStatusCode.NotFound)
            .Route("series/81797/episodes/dvd?page=0", Fixture.Read("not-found.json"), HttpStatusCode.NotFound)
            .Route("series/81797/episodes/absolute?page=0", Fixture.Read("not-found.json"), HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<TvdbApiException>(() => harness.Refresh());

        AssertTheShowIsIntact(harness);
    }

    [Fact]
    public async Task AKeyRefusedPartwayThrough_FailsTheRefreshAndLeavesTheShowAlone()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        harness.CrossReferences.AddEpisode(1, 1001, 81797, 619671);
        harness.Http
            .Route("series/81797/episodes/default?page=1", "", HttpStatusCode.Unauthorized)
            .Route("login", Fixture.Read("login-failure.json"), HttpStatusCode.Unauthorized);

        var exception = await Assert.ThrowsAsync<TvdbApiException>(() => harness.Refresh());

        Assert.True(exception.IsAuthenticationFailure);
        AssertTheShowIsIntact(harness);
        Assert.True(harness.Get<TvdbMetadataProvider>().PauseStatus.IsPaused);
    }

    [Fact]
    public async Task AnOrderingWithNothingToPlace_IsKeptWhileTheShowHasTheSeasonType()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        harness.Http.Route("series/81797/episodes/dvd?page=0", Fixture.Read("not-found.json"), HttpStatusCode.NotFound);

        await harness.Refresh();

        Assert.Contains(TvdbUtility.OrderingGuid(81797, "dvd"), harness.Orderings.Orderings.Keys);
        Assert.Equal(["absolute", "dvd"], harness.Store.GetShow(81797)?.AlternateSeasonTypes);
    }

    private static void AssertTheShowIsIntact(ServiceHarness harness)
    {
        Assert.Equal([361887, 361888, 619671], harness.Series.Series[_seriesID].Episodes.Select(episode => episode.ID.GetNumericID<int>()));
        Assert.Contains(harness.Series.Series[_seriesID].Episodes[0].Titles, title => title is { Value: "I'm Luffy! The Man Who's Gonna Be King of the Pirates!", LanguageCode: "en" });
        Assert.Equal(["tvdb://ordering/81797-absolute", "tvdb://ordering/81797-dvd"], harness.Orderings.Orderings.Keys.Select(id => id.ToString()).Order());
        Assert.Single(harness.CrossReferences.Links);
    }

    private static string LastPage(string page)
        => page.Replace("\"https://api4.thetvdb.com/v4/series/81797/episodes/default?page=1\"", "null", StringComparison.Ordinal);

    [Fact]
    public async Task AnOrderingTheCoreRefuses_DoesNotFailTheRefresh()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        harness.Orderings.RemoveOrdering(TvdbUtility.OrderingGuid(81797, "dvd"));
        harness.Orderings.SaveOrdering(new()
        {
            ID = TvdbUtility.OrderingGuid(81797, "custom"),
            SeriesID = _seriesID,
            Groups = [new() { ID = TvdbUtility.SeasonTypeSeasonGuid(81797, "dvd", 1), Episodes = [TvdbUtility.EpisodeGuid(361887)] }],
        });

        await harness.Refresh();

        Assert.Contains(TvdbUtility.OrderingGuid(81797, "absolute"), harness.Orderings.Orderings.Keys);
        Assert.DoesNotContain(TvdbUtility.OrderingGuid(81797, "dvd"), harness.Orderings.Orderings.Keys);
        Assert.Equal(["absolute"], harness.Store.GetShow(81797)?.AlternateSeasonTypes);
    }

    [Fact]
    public async Task AShowTvDBDoesNotHave_IsLeftAsItWasStored()
    {
        using var harness = new ServiceHarness(http: new RoutingHttpMessageHandler().Route("login", Fixture.Read("login-success.json")));

        await harness.Refresh();

        Assert.Empty(harness.Series.Series);
        Assert.Null(harness.Store.GetShow(81797));
    }

    [Fact]
    public async Task WithoutAnApiKey_ARefreshFailsAsNotConfigured()
    {
        using var harness = new ServiceHarness(new() { ApiKey = null });

        var exception = await Assert.ThrowsAsync<MetadataProviderNotConfiguredException>(() => harness.Refresh());

        Assert.Same(MetadataSource.Tvdb, exception.MetadataSource);
        Assert.Equal("No TvDB API key is configured.", exception.Message);
        Assert.Null(exception.RetryAfter);
        Assert.Empty(harness.Http.Requests);
    }

    [Fact]
    public async Task ARefusedKey_FailsTheRefreshAndPausesTheProvider()
    {
        using var harness = new ServiceHarness(http: RoutingHttpMessageHandler.OnePiece().Route("login", Fixture.Read("login-failure.json"), HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAsync<TvdbApiException>(() => harness.Refresh());

        Assert.True(exception.IsAuthenticationFailure);
        var status = harness.Get<TvdbMetadataProvider>().PauseStatus;
        Assert.True(status.IsPaused);
        Assert.Equal("TvDB refused the API key or subscriber PIN.", status.Reason);
        Assert.NotNull(status.ResumesAt);
    }

    [Fact]
    public async Task ASeriesOfAnotherSource_IsNotRefreshed()
    {
        using var harness = new ServiceHarness();

        await harness.Get<TvdbMetadataProvider>().RefreshSeries(new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, "1"), new(), TestContext.Current.CancellationToken);

        Assert.Empty(harness.Http.Requests);
    }
}
