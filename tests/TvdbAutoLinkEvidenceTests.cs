using System.Globalization;
using System.Text.Json;
using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Services;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The evidence the auto-linker weighs besides a show's names: the shows the
/// anime's links on other sources name, and the air dates of a show's
/// episodes, which decide whether a show whose names only came close is
/// taken.
/// </summary>
/// <remarks>
/// The search hits and episode listings made up here are hand-written in
/// TvDB's shapes, for shows the fixtures do not have.
/// </remarks>
public class TvdbAutoLinkEvidenceTests
{
    private static readonly MetadataGuid _onePiece = TvdbUtility.SeriesGuid(81797);

    private static readonly MetadataGuid _tmdbShow = new(MetadataSource.TMDB, MetadataEntityType.Series, "37854");

    private static readonly EpisodeAlignment _conclusive = new() { SeasonNumber = 1, Offset = 0, MatchedEpisodes = 2, DatedEpisodes = 2, MatchedDays = 2, IsConclusive = true };

    private static readonly EpisodeAlignment _inconclusive = new() { SeasonNumber = 1, Offset = 0, MatchedEpisodes = 1, DatedEpisodes = 2, MatchedDays = 1, IsConclusive = false };

    #region Episode Lists

    [Fact]
    public async Task AutoLinking_LinesUpTheFirstThreeShowsRatedAnything_ByTheirEpisodes()
    {
        var http = Searching("Some Show", 11, 12, 13, 14, 15);
        foreach (var id in (int[])[11, 12, 13, 14, 15])
            http.Route($"series/{id}/episodes/default?page=0", Listing(id, (1, 1, "2020-01-05"), (1, 2, "2020-01-12"), (0, 1, "2019-12-29")));
        using var harness = new ServiceHarness(http: http);
        harness.AddAnime(1, 2, "Some Show", new DateOnly(2020, 1, 5));
        var calls = Judge(harness, candidate => new(Id(candidate) is 15 ? MatchRating.None : MatchRating.TitleMatches));

        await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        // The first three rated anything are fetched, once each; the fourth
        // is past the cap, and one rated nothing is not worth a call.
        Assert.Equal(
            ["series/11/episodes/default?page=0", "series/12/episodes/default?page=0", "series/13/episodes/default?page=0"],
            harness.Http.Paths.Where(path => path.Contains("/episodes/", StringComparison.Ordinal))
        );

        // They are judged again with their regular seasons' episodes.
        Assert.Equal(2, calls.Count);
        var judged = calls[1].Candidates.ToDictionary(Id);
        Assert.All((int[])[11, 12, 13], id =>
        {
            var season = Assert.Single(judged[id].Seasons!);
            Assert.Equal((1, 2), (season.SeasonNumber, season.EpisodeCount));
            Assert.Equal([(1, (DateOnly?)new DateOnly(2020, 1, 5)), (2, new DateOnly(2020, 1, 12))], season.Episodes!.Select(episode => (episode.EpisodeNumber, episode.AiredAt)));
        });
        Assert.Null(judged[14].Seasons);
        Assert.Null(judged[15].Seasons);
    }

    [Fact]
    public async Task AutoLinking_FetchesAShowsEpisodesOncePerAnime_HoweverManyTitlesFindIt()
    {
        var http = Searching("Some Show", 11, 12)
            .Route("search?query=Another%20Name&type=series&limit=10", SearchHits(12, 11))
            .Route("series/11/episodes/default?page=0", Listing(11, (1, 1, "2020-01-05")))
            .Route("series/12/episodes/default?page=0", Listing(12, (1, 1, "2020-01-05")));
        using var harness = new ServiceHarness(http: http);
        var (anime, _) = harness.AddAnime(1, 2, "Some Show");
        anime.SetupGet(a => a.Titles).Returns(
        [
            Title("Some Show"),
            Title("Another Name"),
        ]);
        Judge(harness, _ => new(MatchRating.TitleMatches));

        await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.Contains("search?query=Another%20Name&type=series&limit=10", harness.Http.Paths);
        Assert.Equal(1, harness.Http.Paths.Count(path => path == "series/11/episodes/default?page=0"));
        Assert.Equal(1, harness.Http.Paths.Count(path => path == "series/12/episodes/default?page=0"));
    }

    [Fact]
    public async Task AutoLinking_SendsAStoredShowWithEverySeason_WithoutAskingTvDB()
    {
        using var harness = new ServiceHarness();
        harness.Http.Route("search?query=One%20Piece&type=series&limit=10", Fixture.Read("search-one-piece.json"));
        await harness.Refresh();
        harness.Http.Requests.Clear();
        harness.AddAnime(1, 2, "One Piece", new DateOnly(1999, 10, 20));
        var calls = Judge(harness, candidate => new(candidate.ID == _onePiece ? MatchRating.TitleMatches : MatchRating.None));

        await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(harness.Http.Paths, path => path.Contains("/episodes/", StringComparison.Ordinal));
        var onePiece = Assert.Single(calls).Candidates.Single(candidate => candidate.ID == _onePiece);
        var season = Assert.Single(onePiece.Seasons!);
        Assert.Equal([1, 2], season.Episodes!.Select(episode => episode.EpisodeNumber));
        Assert.Equal(new DateOnly(1999, 10, 20), season.FirstEpisodeAiredAt);
    }

    #endregion

    #region Taking a Show

    [Theory]
    [InlineData(MatchRating.TitleKindaMatches)]
    [InlineData(MatchRating.DateAndTitleKindaMatches)]
    public async Task AutoLinking_TakesAShowWhoseNamesCameClose_WhenItsEpisodesLineUpConclusively(MatchRating rating)
    {
        using var harness = SearchingOnePiece();
        Judge(harness, candidate => candidate.ID == _onePiece
            ? new(rating, HasEpisodes(candidate) ? _conclusive : null)
            : new(MatchRating.None));

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.Equal(_onePiece, candidates[0].ID);
        Assert.Equal((rating, (MetadataAutoLinkRejection?)null), (candidates[0].MatchRating, candidates[0].Rejection));
    }

    [Fact]
    public async Task AutoLinking_TurnsDownAShowWhoseNamesCameClose_WhenItsEpisodesDoNotLineUpConclusively()
    {
        using var harness = SearchingOnePiece();
        Judge(harness, candidate => candidate.ID == _onePiece
            ? new(MatchRating.DateAndTitleKindaMatches, HasEpisodes(candidate) ? _inconclusive : null, Details: "1 of 2 dated episodes aired the same day.")
            : new(MatchRating.None));

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.Equal(MatchRejectionReason.TitleMismatch, candidates[0].Rejection?.Reason);
        var details = candidates[0].Rejection!.Details!;
        Assert.StartsWith("Searched for \"One Piece\". Rated DateAndTitleKindaMatches. TvDB's search spans every kind of show", details, StringComparison.Ordinal);
        Assert.Contains("line up conclusively", details, StringComparison.Ordinal);
        Assert.EndsWith("1 of 2 dated episodes aired the same day.", details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoLinking_TurnsDownAShowMatchedOnItsDatesAlone_EvenWhenTheyLineUp()
    {
        using var harness = SearchingOnePiece();
        Judge(harness, candidate => candidate.ID == _onePiece
            ? new(MatchRating.DateMatches, HasEpisodes(candidate) ? _conclusive : null)
            : new(MatchRating.None));

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.Equal((MatchRating.DateMatches, MatchRejectionReason.TitleMismatch), (candidates[0].MatchRating, candidates[0].Rejection?.Reason));
        Assert.Contains("its episodes lining up with the anime's by air date", candidates[0].Rejection!.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoLinking_TakesAShowWhoseNamesCameClose_WhenAnotherSourceNamesIt()
    {
        using var harness = SearchingOnePiece();
        harness.CrossSourceHints.Add(new() { ID = _onePiece, AnidbAnimeID = 1, NamedBy = [_tmdbShow] });
        var calls = Judge(harness, candidate => candidate.ID == _onePiece ? new(MatchRating.TitleKindaMatches) : new(MatchRating.None));

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.Equal((_onePiece, MetadataAutoLinkOrigin.Search, (MetadataAutoLinkRejection?)null), (candidates[0].ID, candidates[0].Origin, candidates[0].Rejection));
        Assert.Equal([_onePiece], calls[0].Options!.HintedIDs);

        // The hint is listed as well, for the core to weigh against the pick.
        var hint = Assert.Single(candidates, candidate => candidate.Origin is MetadataAutoLinkOrigin.CrossSourceLink);
        Assert.Equal(_onePiece, hint.ID);
    }

    [Fact]
    public async Task AutoLinking_KeepsTheShowsARefusedPickOutranked_WhenTheyWouldHaveBeenTaken()
    {
        using var harness = SearchingOnePiece();
        harness.Http.Route("series/999999/episodes/default?page=0", Listing(999999, (1, 1, "2011-04-01"), (1, 2, "2011-04-08")));
        Judge(harness, candidate => candidate.ID == _onePiece
            ? new(MatchRating.DateAndTitleKindaMatches)
            : new(MatchRating.TitleKindaMatches, HasEpisodes(candidate) ? _conclusive : null));

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.Equal((_onePiece, MatchRejectionReason.TitleMismatch), (candidates[0].ID, candidates[0].Rejection?.Reason));
        Assert.Equal(MatchRejectionReason.Outranked, candidates[1].Rejection?.Reason);
    }

    [Fact]
    public void Refusing_LeavesAPickWithMatchingNamesAlone()
    {
        var anime = new Mock<IAnidbAnime>().Object;
        SeriesMatch Match(MatchRating rating, MatchRejectionReason rejection)
            => new() { AnidbAnime = anime, Candidate = new() { ID = _onePiece, Title = "One Piece" }, Rating = rating, Rejection = rejection };

        var ranked = TvdbSearchService.Refuse([Match(MatchRating.TitleMatches, MatchRejectionReason.None), Match(MatchRating.DateMatches, MatchRejectionReason.Outranked)], []);

        Assert.All(ranked, pair => Assert.Null(pair.Refusal));
        Assert.Equal([MatchRejectionReason.None, MatchRejectionReason.Outranked], ranked.Select(pair => pair.Match.Rejection));
    }

    #endregion

    #region Hints

    [Fact]
    public async Task Hints_AStoredShowAnotherSourceNames_IsHandedBackAsACrossSourceLink_WithEverySeason()
    {
        using var harness = new ServiceHarness();
        harness.Http.Route("search?query=Something%20Else&type=series&limit=10", SearchHits());
        await harness.Refresh();
        harness.Http.Requests.Clear();
        harness.AddAnime(1, 2, "Something Else", new DateOnly(1999, 10, 20));
        harness.CrossSourceHints.Add(new() { ID = _onePiece, AnidbAnimeID = 1, NamedBy = [_tmdbShow] });
        var calls = Judge(harness, _ => new(MatchRating.DateAndTitleKindaMatches, _conclusive));

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        var hint = Assert.Single(candidates);
        Assert.Equal(
            (_onePiece, MetadataAutoLinkOrigin.CrossSourceLink, MatchRating.DateAndTitleKindaMatches, true, false, (MetadataAutoLinkRejection?)null),
            (hint.ID, hint.Origin, hint.MatchRating, hint.IsLocal, hint.IsRemote, hint.Rejection)
        );
        Assert.Equal(1, hint.AnidbAnimeID);
        var result = Assert.IsType<MetadataSeriesSearchResult>(hint.Result);
        Assert.Equal([1, 2], Assert.Single(result.Seasons!).Episodes!.Select(episode => episode.EpisodeNumber));

        // Judged as a hint, from what is stored.
        Assert.Equal([_onePiece], Assert.Single(calls).Options!.HintedIDs);
        Assert.DoesNotContain(harness.Http.Paths, path => path.StartsWith("series/", StringComparison.Ordinal));
        // An expression tree can't read an extension property, so take it first.
        var tvdb = MetadataSource.Tvdb;
        harness.LinkingService.Verify(service => service.GetCrossSourceHints(tvdb, 1), Times.Once());
    }

    [Fact]
    public async Task Hints_AShowNotStored_IsFetchedWithItsEpisodes_WhenTheEngineRatesIt()
    {
        using var harness = new ServiceHarness();
        harness.Http.Route("search?query=Something%20Else&type=series&limit=10", SearchHits());
        harness.AddAnime(1, 2, "Something Else", new DateOnly(1999, 10, 20));
        harness.CrossSourceHints.Add(new() { ID = _onePiece, AnidbAnimeID = 1, NamedBy = [_tmdbShow] });
        var calls = Judge(harness, _ => new(MatchRating.TitleMatches));

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        var hint = Assert.Single(candidates);
        Assert.Equal((MetadataAutoLinkOrigin.CrossSourceLink, false, true), (hint.Origin, hint.IsLocal, hint.IsRemote));
        Assert.Equal(("One Piece", "ワンピース"), (hint.Result.Title, hint.Result.OriginalTitle));
        Assert.Contains("series/81797/extended?meta=translations", harness.Http.Paths);
        Assert.Contains("series/81797/episodes/default?page=0", harness.Http.Paths);
        Assert.Equal(2, calls.Count);
        Assert.NotNull(Assert.Single(calls[1].Candidates).Seasons);
    }

    [Fact]
    public async Task Hints_AnEpisodeAnotherSourceNames_StandsForItsShow_NamedOnce()
    {
        using var harness = new ServiceHarness();
        harness.Http
            .Route("search?query=Something%20Else&type=series&limit=10", SearchHits())
            .Route("episodes/361887/extended", Fixture.Read("episode-361887-extended.json"));
        harness.AddAnime(1, 2, "Something Else", new DateOnly(1999, 10, 20));
        harness.CrossSourceHints.Add(new() { ID = TvdbUtility.EpisodeGuid(361887), AnidbAnimeID = 1, NamedBy = [new(MetadataSource.TMDB, MetadataEntityType.Episode, "1")] });
        harness.CrossSourceHints.Add(new() { ID = _onePiece, AnidbAnimeID = 1, NamedBy = [_tmdbShow] });
        Judge(harness, _ => new(MatchRating.TitleMatches, Filter: MatchRejectionReason.Restricted, Details: "It is restricted."));

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        var hint = Assert.Single(candidates);
        Assert.Equal(_onePiece, hint.ID);
        Assert.Equal(
            new MetadataAutoLinkRejection
            {
                Reason = MatchRejectionReason.Restricted,
                Details = $"tmdb://episode/1 and 1 more, linked to the anime, name TvDB series 81797. It is restricted.",
            },
            hint.Rejection
        );
    }

    [Fact]
    public async Task Hints_RatedNothing_MayStillBeTaken_AsFirstAvailable()
    {
        using var harness = new ServiceHarness();
        harness.Http.Route("search?query=Something%20Else&type=series&limit=10", SearchHits());
        harness.AddAnime(1, 2, "Something Else");
        harness.CrossSourceHints.Add(new() { ID = _onePiece, AnidbAnimeID = 1, NamedBy = [_tmdbShow] });
        Judge(harness, _ => new(MatchRating.None));

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        var hint = Assert.Single(candidates);
        Assert.Equal((MatchRating.FirstAvailable, (MetadataAutoLinkRejection?)null), (hint.MatchRating, hint.Rejection));

        // Rated nothing, it is not worth fetching its episodes for.
        Assert.DoesNotContain("series/81797/episodes/default?page=0", harness.Http.Paths);
    }

    [Fact]
    public async Task Hints_ThatMayBeTaken_ComeFirst_TheBestRatedLeading()
    {
        using var harness = new ServiceHarness();
        harness.Http
            .Route("search?query=Something%20Else&type=series&limit=10", SearchHits())
            .Route("series/5/extended?meta=translations", """{"status": "success", "data": {"id": 5, "name": "Five"}}""")
            .Route("series/6/extended?meta=translations", """{"status": "success", "data": {"id": 6, "name": "Six"}}""");
        harness.AddAnime(1, 2, "Something Else");
        harness.CrossSourceHints.Add(new() { ID = TvdbUtility.SeriesGuid(6), AnidbAnimeID = 1, NamedBy = [_tmdbShow] });
        harness.CrossSourceHints.Add(new() { ID = TvdbUtility.SeriesGuid(5), AnidbAnimeID = 1, NamedBy = [_tmdbShow] });
        harness.CrossSourceHints.Add(new() { ID = _onePiece, AnidbAnimeID = 1, NamedBy = [_tmdbShow] });
        Judge(harness, candidate => Id(candidate) switch
        {
            6 => new(MatchRating.TitleMatches, Filter: MatchRejectionReason.Restricted),
            5 => new(MatchRating.DateMatches),
            _ => new(MatchRating.TitleMatches),
        });

        var candidates = await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken);

        Assert.Equal([81797, 5, 6], candidates.Select(candidate => Id(candidate.Result)));
        Assert.Equal([null, null, MatchRejectionReason.Restricted], candidates.Select(candidate => candidate.Rejection?.Reason));
    }

    [Fact]
    public async Task Hints_OfAnotherKindOrSource_AreLeftOut()
    {
        using var harness = new ServiceHarness();
        harness.Http.Route("search?query=Something%20Else&type=series&limit=10", SearchHits());
        harness.AddAnime(1, 2, "Something Else");
        harness.CrossSourceHints.Add(new() { ID = new(MetadataSource.Tvdb, MetadataEntityType.Movie, "12"), AnidbAnimeID = 1, NamedBy = [_tmdbShow] });
        harness.CrossSourceHints.Add(new() { ID = _tmdbShow, AnidbAnimeID = 1, NamedBy = [_onePiece] });
        Judge(harness, _ => new(MatchRating.TitleMatches));

        Assert.Empty(await harness.Get<TvdbMetadataProvider>().FindAutoLinks(1, TestContext.Current.CancellationToken));
        Assert.Equal(["login", "search?query=Something%20Else&type=series&limit=10"], harness.Http.Paths);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// How the engine rates one show.
    /// </summary>
    /// <param name="Rating">The rating.</param>
    /// <param name="Alignment">How its episodes lined up, if they did.</param>
    /// <param name="Filter">The engine's own reason to refuse it, if any.</param>
    /// <param name="Details">What the engine says it compared.</param>
    private sealed record Verdict(MatchRating Rating, EpisodeAlignment? Alignment = null, MatchRejectionReason? Filter = null, string? Details = null);

    /// <summary>
    /// Makes the engine rate each show by a verdict of its own, the best rated
    /// first and otherwise in the order handed, and take the first unless
    /// nothing agreed or it filtered it out, recording what it was handed.
    /// </summary>
    private static List<(IReadOnlyList<MetadataSeriesSearchResult> Candidates, SeriesMatchOptions? Options)> Judge(ServiceHarness harness, Func<MetadataSeriesSearchResult, Verdict> verdict)
    {
        var calls = new List<(IReadOnlyList<MetadataSeriesSearchResult>, SeriesMatchOptions?)>();
        harness.MatchingEngine
            .Setup(engine => engine.MatchSeries(It.IsAny<IAnidbAnime>(), It.IsAny<IReadOnlyList<MetadataSeriesSearchResult>>(), It.IsAny<SeriesMatchOptions?>()))
            .Returns((IAnidbAnime anime, IReadOnlyList<MetadataSeriesSearchResult> candidates, SeriesMatchOptions? options) =>
            {
                calls.Add((candidates, options));
                var judged = candidates
                    .Select(candidate => (Candidate: candidate, Verdict: verdict(candidate)))
                    .OrderBy(pair => TvdbSearchService.Priority(pair.Verdict.Rating))
                    .ToList();
                return [.. judged.Select((pair, index) => new SeriesMatch
                {
                    AnidbAnime = anime,
                    Candidate = pair.Candidate,
                    Rating = pair.Verdict.Rating,
                    EpisodeAlignment = pair.Verdict.Alignment,
                    Details = pair.Verdict.Details,
                    Rejection = pair.Verdict.Filter
                        ?? (pair.Verdict.Rating is MatchRating.None ? MatchRejectionReason.TitleMismatch : index is 0 ? MatchRejectionReason.None : MatchRejectionReason.Outranked),
                })];
            });
        return calls;
    }

    /// <summary>
    /// A harness searching for One Piece, with the fixtures for its show.
    /// </summary>
    private static ServiceHarness SearchingOnePiece()
    {
        var harness = new ServiceHarness();
        harness.Http.Route("search?query=One%20Piece&type=series&limit=10", Fixture.Read("search-one-piece.json"));
        harness.AddAnime(1, 2, "One Piece", new DateOnly(1999, 10, 20));
        return harness;
    }

    /// <summary>
    /// A handler answering a search for a title with the shows given.
    /// </summary>
    private static RoutingHttpMessageHandler Searching(string title, params int[] seriesIDs)
        => new RoutingHttpMessageHandler()
            .Route("login", Fixture.Read("login-success.json"))
            .Route($"search?query={Uri.EscapeDataString(title)}&type=series&limit=10", SearchHits(seriesIDs));

    /// <summary>
    /// A search answer listing the shows given, each named after its ID.
    /// </summary>
    private static string SearchHits(params int[] seriesIDs)
        => JsonSerializer.Serialize(new
        {
            status = "success",
            data = seriesIDs.Select(id => new { id = $"series-{id}", tvdb_id = id.ToString(CultureInfo.InvariantCulture), name = $"Show {id}", type = "series" }),
        });

    /// <summary>
    /// A show's default episode listing, on one page.
    /// </summary>
    private static string Listing(int seriesID, params (int Season, int Number, string Aired)[] episodes)
        => JsonSerializer.Serialize(new
        {
            status = "success",
            data = new
            {
                episodes = episodes.Select((episode, index) => new
                {
                    id = (seriesID * 1000) + index + 1,
                    seriesId = seriesID,
                    seasonNumber = episode.Season,
                    number = episode.Number,
                    aired = episode.Aired,
                }),
            },
            links = new { next = (string?)null },
        });

    private static Shoko.Abstractions.Metadata.Stub.TitleStub Title(string value)
        => new() { Source = MetadataSource.AniDB, Language = TitleLanguage.English, LanguageCode = "en", Value = value, Type = TitleType.Official };

    private static int Id(MetadataSearchResult candidate)
        => int.Parse(candidate.ID.ID, CultureInfo.InvariantCulture);

    private static bool HasEpisodes(MetadataSeriesSearchResult candidate)
        => candidate.Seasons?.Any(season => season.Episodes is not null) is true;

    #endregion
}
