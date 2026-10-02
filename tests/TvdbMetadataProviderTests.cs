using System.Reflection;
using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Stub;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The provider as the core sees it: what it claims, when it is not
/// configured or paused, how
/// it cleans up, matches episodes and finds what to link anime to.
/// </summary>
public class TvdbMetadataProviderTests
{
    private static readonly MetadataGuid _seriesID = TvdbUtility.SeriesGuid(81797);

    #region Provider

    [Fact]
    public void ItClaimsTheTvdbSource_WhichTheCoreDoesNotReserve()
    {
        using var harness = new ServiceHarness();
        var provider = harness.Get<TvdbMetadataProvider>();

        Assert.Same(MetadataSource.Tvdb, provider.Source);
        Assert.DoesNotContain(provider.Source, (MetadataSource[])[MetadataSource.AniDB, MetadataSource.TMDB, MetadataSource.Shoko, MetadataSource.User]);
        Assert.Equal("TvDB", provider.Name);
        Assert.Equal(4, provider.MaxConcurrentJobs);
        Assert.True(((IMetadataProvider)provider).AutoLinkByDefault);
        Assert.True(provider.IsConfigured);
        Assert.Null(provider.NotConfiguredReason);
    }

    [Fact]
    public void WithoutAnApiKey_ItIsNotConfiguredAndSaysWhy()
    {
        using var harness = new ServiceHarness(new() { ApiKey = null });
        var provider = (IMetadataProvider)harness.Get<TvdbMetadataProvider>();

        Assert.False(provider.IsConfigured);
        Assert.Equal("No TvDB API key is configured.", provider.NotConfiguredReason);
    }

    [Fact]
    public void ASeasonIsNotSomethingAUserCanLinkByHand()
    {
        using var harness = new ServiceHarness();

        Assert.Equal([MetadataEntityType.Episode, MetadataEntityType.Series], harness.Get<TvdbMetadataProvider>().LinkableEntityTypes.OrderBy(type => type.Value, StringComparer.Ordinal));
    }

    [Fact]
    public void ItServesSeriesAndImagesAndLinksOnItsOwn_ButNoMovies()
    {
        var interfaces = typeof(TvdbMetadataProvider).GetInterfaces();

        Assert.Contains(typeof(IMetadataSeriesLinkingProvider), interfaces);
        Assert.Contains(typeof(IMetadataAutoLinkingProvider), interfaces);
        Assert.Contains(typeof(IMetadataImageProvider), interfaces);
        Assert.Contains(typeof(IPausableMetadataProvider), interfaces);
        Assert.DoesNotContain(typeof(IMetadataMovieProvider), interfaces);
    }

    [Fact]
    public void ItHasNoGetters_TheCoreReadsTheStoresInstead()
    {
        var methods = typeof(TvdbMetadataProvider).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(methods, method => method.Name.StartsWith("Get", StringComparison.Ordinal) && method.Name is not nameof(TvdbMetadataProvider.GetImages));
    }

    #endregion

    #region Pausing

    [Fact]
    public void WithoutAnApiKey_ItIsNotPaused()
    {
        using var harness = new ServiceHarness(new() { ApiKey = null });

        Assert.Same(MetadataProviderPauseStatus.NotPaused, harness.Get<TvdbMetadataProvider>().PauseStatus);
    }

    [Fact]
    public void WithAKey_ItIsNotPaused()
    {
        using var harness = new ServiceHarness();

        Assert.Same(MetadataProviderPauseStatus.NotPaused, harness.Get<TvdbMetadataProvider>().PauseStatus);
    }

    [Fact]
    public void WhileTheRateLimiterIsPaused_ItIsPausedUntilItResumes()
    {
        using var harness = new ServiceHarness();
        var provider = harness.Get<TvdbMetadataProvider>();
        var raised = 0;
        provider.PauseStatusChanged += (_, _) => raised++;

        harness.RateLimiter.Pause(TimeSpan.FromMinutes(5), TvdbApiClient.RateLimitedReason);

        var status = provider.PauseStatus;
        Assert.True(status.IsPaused);
        Assert.Equal(TvdbApiClient.RateLimitedReason, status.Reason);
        Assert.InRange(status.ResumesAt!.Value, DateTime.UtcNow.AddMinutes(4), DateTime.UtcNow.AddMinutes(6));
        Assert.Equal(1, raised);

        harness.RateLimiter.Resume();

        Assert.False(provider.PauseStatus.IsPaused);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void SavingTheSettings_LiftsAPauseForARefusedKey()
    {
        using var harness = new ServiceHarness();
        var provider = harness.Get<TvdbMetadataProvider>();
        var raised = 0;
        provider.PauseStatusChanged += (_, _) => raised++;
        harness.RateLimiter.Pause(TimeSpan.FromHours(1), TvdbApiClient.RefusedCredentialsReason);

        harness.ConfigurationService.RaiseSaved();

        Assert.False(provider.PauseStatus.IsPaused);
        Assert.True(raised >= 2);
    }

    [Fact]
    public void SavingTheSettings_LeavesARateLimitAlone()
    {
        using var harness = new ServiceHarness();
        var provider = harness.Get<TvdbMetadataProvider>();
        harness.RateLimiter.Pause(TimeSpan.FromMinutes(5), TvdbApiClient.RateLimitedReason);

        harness.ConfigurationService.RaiseSaved();

        Assert.True(provider.PauseStatus.IsPaused);
    }

    #endregion

    #region Clean-Up

    [Fact]
    public async Task CleaningUp_ForgetsTheShowAndThePortraitsOfItsPeople_AndLeavesTheOrderingsToTheCore()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        Assert.NotNull(harness.Store.GetPortrait(TvdbUtility.CreatorGuid(412417)));
        harness.Series.RemoveSeries(_seriesID);
        harness.People.RemoveCast(_seriesID);
        harness.People.RemoveCrew(_seriesID);

        await harness.Get<TvdbMetadataProvider>().CleanUp(_seriesID, TestContext.Current.CancellationToken);

        Assert.Null(harness.Store.GetShow(81797));
        // The core removes a purged series' orderings before it calls this.
        Assert.Equal(2, harness.Orderings.Orderings.Count);
        // The people store still holds the people until its own purge, but
        // nothing credits them, so their portraits go now.
        Assert.NotNull(harness.People.GetCreator(TvdbUtility.CreatorGuid(412417)));
        Assert.Null(harness.Store.GetPortrait(TvdbUtility.CreatorGuid(412417)));
    }

    [Fact]
    public async Task CleaningUpAnotherEntry_TouchesNothing()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();

        await harness.Get<TvdbMetadataProvider>().CleanUp(new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, "81797"), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Store.GetShow(81797));
        Assert.Equal(2, harness.Orderings.Orderings.Count);
    }

    #endregion

    #region Matching

    [Fact]
    public async Task Matching_RunsTheCoresMatcherOverTheStoredEpisodesWithinSeasons()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        var calls = harness.MatchByPosition();
        var (anime, episodes) = harness.AddAnime(1, 2);

        var matches = await harness.Get<TvdbMetadataProvider>().MatchEpisodes(anime.Object, episodes, _seriesID, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.Equal(TvdbUtility.EpisodeGuid(361887), matches[0].Candidate?.ID);
        var call = Assert.Single(calls);
        Assert.Equal(3, call.Provider.Count);
        Assert.Equal(EpisodeMatchStrategy.DateAndTitleWithinSeasons, call.Options?.Strategy);
    }

    [Fact]
    public async Task Matching_WithinOneSeason_OffersOnlyItsEpisodes()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        var calls = harness.MatchByPosition();
        var (anime, episodes) = harness.AddAnime(1, 2);

        await harness.Get<TvdbMetadataProvider>().MatchEpisodes(anime.Object, episodes, _seriesID, TvdbUtility.SeasonGuid(31892), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([TvdbUtility.EpisodeGuid(619671)], Assert.Single(calls).Provider.Select(episode => episode.ID));
    }

    [Fact]
    public async Task Matching_WithinASeasonOfAnotherShow_IsRefused()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        var calls = harness.MatchByPosition();
        var (anime, episodes) = harness.AddAnime(1, 2);
        var other = new Mock<ISeason>();
        other.SetupGet(season => season.SeriesID).Returns(TvdbUtility.SeriesGuid(1));
        harness.MetadataService.Setup(service => service.GetSeason(TvdbUtility.SeasonGuid(5))).Returns(other.Object);

        var matches = await harness.Get<TvdbMetadataProvider>().MatchEpisodes(anime.Object, episodes, _seriesID, TvdbUtility.SeasonGuid(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(matches);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Matching_WithinAGroupOfAnOrdering_OffersTheGroupsEpisodes()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        var calls = harness.MatchByPosition();
        var (anime, episodes) = harness.AddAnime(1, 2);
        var groupID = TvdbUtility.SeasonTypeSeasonGuid(81797, "dvd", 2);
        var episode = harness.Series.GetEpisode(TvdbUtility.EpisodeGuid(361887))!;
        var group = new Mock<ISeason>();
        group.SetupGet(season => season.SeriesID).Returns(_seriesID);
        group.SetupGet(season => season.Episodes).Returns([episode]);
        harness.MetadataService.Setup(service => service.GetSeason(groupID)).Returns(group.Object);

        await harness.Get<TvdbMetadataProvider>().MatchEpisodes(anime.Object, episodes, _seriesID, groupID, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([TvdbUtility.EpisodeGuid(361887)], Assert.Single(calls).Provider.Select(candidate => candidate.ID));
    }

    [Fact]
    public async Task Matching_CanLeaveOutTheEpisodesOtherAnimeClaim()
    {
        using var harness = new ServiceHarness();
        await harness.Refresh();
        var calls = harness.MatchByPosition();
        var (anime, episodes) = harness.AddAnime(1, 2);
        harness.CrossReferences.AddEpisode(2, 2001, 81797, 361887).AddEpisode(1, 1001, 81797, 361888);

        await harness.Get<TvdbMetadataProvider>().MatchEpisodes(anime.Object, episodes, _seriesID, considerOtherLinks: true, cancellationToken: TestContext.Current.CancellationToken);
        await harness.Get<TvdbMetadataProvider>().MatchEpisodes(anime.Object, episodes, _seriesID, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([361888, 619671], calls[0].Provider.Select(episode => episode.ID.GetNumericID<int>()));
        Assert.Equal(3, calls[1].Provider.Count);
    }

    [Fact]
    public async Task Matching_AShowThatIsNotStored_FindsNothing()
    {
        using var harness = new ServiceHarness();
        var calls = harness.MatchByPosition();
        var (anime, episodes) = harness.AddAnime(1, 2);

        Assert.Empty(await harness.Get<TvdbMetadataProvider>().MatchEpisodes(anime.Object, episodes, _seriesID, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await harness.Get<TvdbMetadataProvider>().MatchEpisodes(anime.Object, episodes, new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, "1"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(calls);
    }

    #endregion

    #region Search & Auto-Linking

    [Fact]
    public async Task Search_AnswersPagesOfShowsOnTheTvdbSource()
    {
        using var harness = new ServiceHarness(http: new RoutingHttpMessageHandler()
            .Route("login", Fixture.Read("login-success.json"))
            .Route("search?query=one%20piece&type=series&limit=6", Fixture.Read("search-one-piece.json")));

        var (page, total) = await harness.Get<TvdbMetadataProvider>().SearchSeries(new() { Query = "one piece" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, total);
        Assert.Equal(["tvdb://series/81797", "tvdb://series/999999"], page.Select(result => result.ID.ToString()));
    }

    [Fact]
    public async Task AutoLinking_HandsBackEveryShowFound_BestFirst_AndLinksNothing()
    {
        using var harness = new ServiceHarness(http: new RoutingHttpMessageHandler()
            .Route("login", Fixture.Read("login-success.json"))
            .Route("search?query=One%20Piece&type=series&limit=10", Fixture.Read("search-one-piece.json")));
        harness.AddAnime(1, 2, "One Piece", new DateOnly(1999, 10, 20));
        var calls = harness.JudgeSeriesBy(new Dictionary<int, MatchRating> { [81797] = MatchRating.DateAndTitleMatches });

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.Equal([_seriesID, TvdbUtility.SeriesGuid(999999)], candidates.Select(candidate => candidate.ID));
        Assert.Equal((1, MatchRating.DateAndTitleMatches, true, (MetadataAutoLinkRejection?)null), (candidates[0].AnidbAnimeID, candidates[0].MatchRating, candidates[0].IsRemote, candidates[0].Rejection));
        Assert.Equal(new MetadataAutoLinkRejection { Reason = MatchRejectionReason.TitleMismatch, Details = "Searched for \"One Piece\"." }, candidates[1].Rejection);
        Assert.Equal(["ワンピース", "Wan Pisu"], candidates[0].Result.AlternateTitles);

        // Every title of the anime is tried, and one entry holds every season.
        var options = Assert.Single(calls).Options;
        Assert.NotNull(options);
        Assert.Null(options.Query);
        Assert.False(options.SeasonsAreSeparateEntries);

        // The core links what is taken; the provider only asks it for hints
        // and writes nothing.
        Assert.Empty(harness.CrossReferences.GetSeriesLinks(1));
        // An expression tree can't read an extension property, so take it first.
        var tvdb = MetadataSource.Tvdb;
        harness.LinkingService.Verify(service => service.GetCrossSourceHints(tvdb, 1), Times.Once());
        harness.LinkingService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AutoLinking_SearchesTheNextTitle_UntilAShowMatchesOnBothTitleAndDate()
    {
        using var harness = new ServiceHarness(http: new RoutingHttpMessageHandler()
            .Route("login", Fixture.Read("login-success.json"))
            .Route("search?query=One%20Piece&type=series&limit=10", Fixture.Read("search-one-piece.json"))
            .Route("search?query=Wan%20Pisu&type=series&limit=10", Fixture.Read("search-one-piece.json")));
        var (anime, _) = harness.AddAnime(1, 2);
        anime.SetupGet(a => a.Titles).Returns(
        [
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.English, LanguageCode = "en", Value = "One Piece", Type = TitleType.Official },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Wan Pisu", Type = TitleType.Main },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Japanese, LanguageCode = "ja", Value = "ワンピース", Type = TitleType.Official },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.German, LanguageCode = "de", Value = "One Piece", Type = TitleType.Official },
        ]);
        var ratings = new Dictionary<int, MatchRating> { [81797] = MatchRating.TitleMatches };
        var calls = harness.JudgeSeriesBy(ratings);

        await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        // A title match alone is worth a second search, and a show found twice
        // is judged once.
        Assert.Contains("search?query=Wan%20Pisu&type=series&limit=10", harness.Http.Paths);
        Assert.Equal(2, Assert.Single(calls).Candidates.Count);

        ratings[81797] = MatchRating.DateAndTitleMatches;
        harness.Http.Requests.Clear();
        calls.Clear();

        await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.Equal(["search?query=One%20Piece&type=series&limit=10"], harness.Http.Paths.Where(path => path.StartsWith("search", StringComparison.Ordinal)));
        Assert.Single(calls);
    }

    [Theory]
    [InlineData(MatchRating.DateMatches)]
    [InlineData(MatchRating.TitleKindaMatches)]
    [InlineData(MatchRating.DateAndTitleKindaMatches)]
    public async Task AutoLinking_TakesNoShowWhoseNamesOnlyCameClose(MatchRating rating)
    {
        using var harness = new ServiceHarness(http: new RoutingHttpMessageHandler()
            .Route("login", Fixture.Read("login-success.json"))
            .Route("search?query=One%20Piece&type=series&limit=10", Fixture.Read("search-one-piece.json")));
        harness.AddAnime(1, 2, "One Piece", new DateOnly(1999, 10, 20));
        harness.JudgeSeriesBy(new Dictionary<int, MatchRating> { [81797] = rating, [999999] = MatchRating.DateMatches });

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.Equal(_seriesID, candidates[0].ID);
        Assert.Equal((rating, MatchRejectionReason.TitleMismatch), (candidates[0].MatchRating, candidates[0].Rejection?.Reason));

        // The show it outranked loses on its name too, not to a show nothing links.
        Assert.Equal((MatchRating.DateMatches, MatchRejectionReason.TitleMismatch), (candidates[1].MatchRating, candidates[1].Rejection?.Reason));
        Assert.All(candidates, candidate => Assert.NotNull(candidate.Rejection));
    }

    [Fact]
    public async Task AutoLinking_WhileTvDBFails_SaysItCannotBeReached()
    {
        using var harness = new ServiceHarness(http: new RoutingHttpMessageHandler()
            .Route("login", Fixture.Read("login-success.json"))
            .Route("search?query=One%20Piece&type=series&limit=10", "{}", System.Net.HttpStatusCode.BadGateway));
        harness.AddAnime(1, 2, "One Piece");

        var exception = await Assert.ThrowsAsync<MetadataProviderUnavailableException>(() => harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken));

        Assert.Same(MetadataSource.Tvdb, exception.MetadataSource);
        Assert.IsType<TvdbApiException>(exception.InnerException);
    }

    [Fact]
    public async Task AutoLinking_AnAnimeNotHeldLocally_FindsNothing()
    {
        using var harness = new ServiceHarness();

        Assert.Empty(await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken));
        Assert.Empty(await harness.Get<TvdbMetadataProvider>().FindAutoLinks(0, TestContext.Current.CancellationToken));
        Assert.Empty(harness.Http.Requests);
    }

    [Fact]
    public async Task AutoLinking_WithoutAnApiKey_NeverReachesTvDB()
    {
        using var harness = new ServiceHarness(new() { ApiKey = null });
        harness.AddAnime(1, 2, "One Piece");

        Assert.Empty(await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken));
        Assert.Empty(harness.Http.Requests);
    }

    #endregion
}
