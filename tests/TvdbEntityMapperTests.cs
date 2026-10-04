using System.Text.Json;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// What the mapper makes of a TvDB response: which fields survive, which are
/// derived, and which are deliberately dropped.
/// </summary>
public class TvdbEntityMapperTests
{
    private static readonly DateTime _fetchedAt = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    internal static TvdbSeriesExtended ReadSeries()
        => JsonSerializer.Deserialize<TvdbResponse<TvdbSeriesExtended>>(Fixture.Read("series-81797-extended.json"), TvdbJson.Options)!.Data!;

    internal static List<TvdbEpisode> ReadEpisodes(string fileName)
        => JsonSerializer.Deserialize<TvdbResponse<TvdbEpisodesData>>(Fixture.Read(fileName), TvdbJson.Options)!.Data!.Episodes!;

    internal static List<TvdbEpisode> ReadDefaultEpisodes()
        => [.. ReadEpisodes("series-81797-episodes-page0.json"), .. ReadEpisodes("series-81797-episodes-page1.json")];

    // The same languages for every kind of text.
    private static TvdbTextLanguages Languages(params string[] codes)
        => new(codes, codes, codes, codes);

    private static Shoko.Abstractions.Metadata.Storage.MetadataSeriesData MapOnePiece()
        => TvdbEntityMapper.ToSeriesData(
            ReadSeries(),
            Languages("eng", "jpn"),
            ReadDefaultEpisodes(),
            new Dictionary<string, IReadOnlyList<TvdbEpisode>> { ["eng"] = ReadEpisodes("series-81797-episodes-default-eng.json") }
        );

    #region Season Types

    [Fact]
    public void TheDefaultSeasonType_IsTheOneTheShowNames()
        => Assert.Equal("official", TvdbEntityMapper.DefaultSeasonType(ReadSeries()));

    [Fact]
    public void AShowNamingAnotherDefault_HasItsSeasonsInThatOne()
    {
        var series = ReadSeries();
        series.DefaultSeasonType = 3;

        Assert.Equal("absolute", TvdbEntityMapper.DefaultSeasonType(series));
        Assert.Equal(["official", "dvd"], TvdbEntityMapper.AlternateSeasonTypes(series, "absolute"));
    }

    [Fact]
    public void AShowNamingNoDefault_IsInTheAiredOrder()
    {
        var series = ReadSeries();
        series.DefaultSeasonType = null;

        Assert.Equal("official", TvdbEntityMapper.DefaultSeasonType(series));
    }

    [Fact]
    public void TheOtherSeasonTypes_AreTheOnesTheShowHasSeasonsIn()
        => Assert.Equal(["absolute", "dvd"], TvdbEntityMapper.AlternateSeasonTypes(ReadSeries(), "official"));

    [Theory]
    [InlineData("official", OrderingType.OriginalAirDate)]
    [InlineData("dvd", OrderingType.DVD)]
    [InlineData("altdvd", OrderingType.DVD)]
    [InlineData("absolute", OrderingType.Absolute)]
    [InlineData("regional", OrderingType.TV)]
    [InlineData("alternate", OrderingType.Unknown)]
    public void ASeasonType_MapsOntoAnOrderingType(string seasonType, OrderingType expected)
        => Assert.Equal(expected, TvdbEntityMapper.ToOrderingType(seasonType));

    [Fact]
    public void ASeasonType_IsNamedAsTheShowNamesIt()
    {
        var series = ReadSeries();
        series.SeasonTypes =
        [
            new() { ID = 4, Type = "alternate", Name = "Alternate Order", AlternateName = "Story Order" },
            new() { ID = 7, Type = "alttwo", Name = "Alternate Order 2", AlternateName = " " },
        ];

        Assert.Equal("Story Order", TvdbEntityMapper.SeasonTypeName(series, "alternate"));
        Assert.Equal("Alternate Order 2", TvdbEntityMapper.SeasonTypeName(series, "alttwo"));
        Assert.Equal("DVD Order", TvdbEntityMapper.SeasonTypeName(series, "dvd"));
    }

    #endregion

    #region Series

    [Fact]
    public void Series_CarriesTheScalarsAcross()
    {
        var series = MapOnePiece();

        Assert.Equal(TvdbUtility.SeriesGuid(81797), series.ID);
        Assert.Equal("tvdb://series/81797", series.ID.ToString());
        Assert.Equal(AnimeType.TV, series.Type);
        Assert.Equal(new PartialDateOnly(new DateOnly(1999, 10, 20)), series.AirDate);
        Assert.Equal(ReleaseStatus.Releasing, series.ReleaseStatus);
        Assert.Equal("ja", series.OriginalLanguageCode);
        Assert.Equal(3236299, series.Popularity);
        Assert.Equal(0, series.Rating);
    }

    [Fact]
    public void AContinuingShow_HasNoEndDateYet()
    {
        Assert.Null(MapOnePiece().EndDate);

        var ended = ReadSeries();
        ended.Status = new() { Name = "Ended" };
        var mapped = TvdbEntityMapper.ToSeriesData(ended, null, []);

        Assert.Equal(ReleaseStatus.Finished, mapped.ReleaseStatus);
        Assert.Equal(new PartialDateOnly(new DateOnly(2026, 9, 27)), mapped.EndDate);
    }

    [Fact]
    public void Series_ItsOwnNameIsTheMainTitleInItsOriginalLanguage()
    {
        var main = Assert.Single(MapOnePiece().Titles, title => title.Type is TitleType.Main);

        Assert.Equal("ワンピース", main.Value);
        Assert.Equal("ja", main.LanguageCode);
        Assert.Equal(TitleLanguage.Japanese, main.Language);
        Assert.Equal(MetadataSource.Tvdb, main.Source);
    }

    [Fact]
    public void Series_TakesTranslationsAsOfficialTitlesAndAliasesAsSynonyms()
    {
        var titles = MapOnePiece().Titles;

        Assert.Contains(titles, title => title is { Type: TitleType.Official, Value: "One Piece", LanguageCode: "en" });
        Assert.Contains(titles, title => title is { Type: TitleType.Synonym, Value: "One Piece (1998)", LanguageCode: "en" });
        Assert.Contains(titles, title => title is { Type: TitleType.Synonym, Value: "One Piece Log: Fish-Man Island Saga", LanguageCode: "en" });
        Assert.Contains(titles, title => title is { Type: TitleType.Synonym, Value: "One Piece - All'arrembaggio!", LanguageCode: "it" });
    }

    [Fact]
    public void Series_TakesTranslationsOnlyInTheLanguagesAsked()
    {
        var series = MapOnePiece();

        Assert.DoesNotContain(series.Titles, title => title is { Type: TitleType.Official, LanguageCode: "de" or "it" });
        Assert.DoesNotContain(series.Overviews, overview => overview.LanguageCode is "de" or "it");
        Assert.Contains(series.Titles, title => title is { Type: TitleType.Synonym, Value: "Wan Pisu", LanguageCode: "ja" });
    }

    [Fact]
    public void Series_TakesTheLanguagesInTheOrderAsked()
    {
        var overviews = TvdbEntityMapper.SeriesOverviews(ReadSeries(), ["deu", "eng"]);

        Assert.Equal(["ja", "de", "en"], overviews.Select(overview => overview.LanguageCode));
    }

    [Fact]
    public void Series_TakesEveryTranslationWhenNoLanguagesAreNamed()
    {
        var series = ReadSeries();
        var languages = series.Translations!.OverviewTranslations!.Select(translation => TvdbUtility.ToLanguageCode(translation.Language)!).ToHashSet();

        Assert.Equal(languages, TvdbEntityMapper.SeriesOverviews(series, null).Select(overview => overview.LanguageCode).ToHashSet());
        Assert.Contains(TvdbEntityMapper.SeriesTitles(series, null), title => title is { Type: TitleType.Official, LanguageCode: "de" });
    }

    [Fact]
    public void ASeriesWithoutTranslations_HasOnlyItsOwnNameAndAliases()
    {
        var series = ReadSeries();
        series.Translations = null;

        var titles = TvdbEntityMapper.SeriesTitles(series, ["eng", "jpn"]);

        Assert.Equal("ワンピース", titles[0].Value);
        Assert.DoesNotContain(titles, title => title.Type is TitleType.Official);
        Assert.Equal(["ja"], TvdbEntityMapper.SeriesOverviews(series, ["eng", "jpn"]).Select(overview => overview.LanguageCode));
    }

    [Fact]
    public void Series_DoesNotRepeatATitleItAlreadyHas()
    {
        // The Japanese translation's name is the show's own name, which is
        // already the main title.
        var titles = MapOnePiece().Titles;

        Assert.Single(titles, title => title.Value == "ワンピース");
        Assert.Equal(titles.Count, titles.DistinctBy(title => (title.LanguageCode, title.Value)).Count());
    }

    [Fact]
    public void Series_TakesItsOverviewAndTranslatedOnesOncePerLanguage()
    {
        var overviews = MapOnePiece().Overviews;

        Assert.Equal(["ja", "en"], overviews.Select(overview => overview.LanguageCode));
        Assert.All(overviews, overview => Assert.Equal(MetadataSource.Tvdb, overview.Source));
    }

    [Fact]
    public void Series_LinksToItsPageAndItsIDsElsewhereWithBareIDs()
    {
        var resources = MapOnePiece().Resources;

        Assert.Equal(("TvDB", "https://thetvdb.com/series/one-piece", "81797"), (resources[0].Name, resources[0].Url, resources[0].ID));
        Assert.Contains(resources, resource => resource is { Type: ResourceType.CrossReference, Name: "IMDb", ID: "tt0388629", Url: "https://www.imdb.com/title/tt0388629/" });
        Assert.Contains(resources, resource => resource is { Type: ResourceType.CrossReference, Name: "TMDB", ID: "37854" });
        Assert.Contains(resources, resource => resource is { Type: ResourceType.Website, Url: "https://www.toei-anim.co.jp/tv/onep/", ID: null });
        Assert.Contains(resources, resource => resource is { Type: ResourceType.Social, Name: "X", ID: "OPcom_info", Url: "https://x.com/OPcom_info" });
        Assert.Contains(resources, resource => resource is { Type: ResourceType.CrossReference, Name: "TVmaze", ID: "1505", Url: "https://www.tvmaze.com/shows/1505" });
        Assert.DoesNotContain(resources, resource => resource.Name == "Somewhere Unknown");
    }

    [Fact]
    public void Series_NamesItselfOnTheOtherMetadataSourcesTvDBLists()
    {
        var ids = MapOnePiece().CrossSourceIDs;

        // EIDR, Zap2It, the website and the socials stay resources only.
        Assert.Equal(["imdb://series/tt0388629", "tmdb://series/37854", "tvmaze://series/1505"], ids.Select(id => id.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(MetadataSource.TMDB, ids.Single(id => id.ID == "37854").Source);
    }

    [Fact]
    public void AShowsTmdbFilm_IsAFilmOnTmdb_AndAnIDGivenTwiceIsKeptOnce()
    {
        var series = new TvdbSeriesExtended
        {
            ID = 1,
            RemoteIDs =
            [
                new() { ID = "12345", Type = 10, SourceName = "TheMovieDB.com" },
                new() { ID = " tt0000001 ", Type = 2, SourceName = "IMDB" },
                new() { ID = "tt0000001", SourceName = "IMDB" },
                new() { ID = "nm0849028", Type = 16, SourceName = "IMDB" },
                new() { ID = "", Type = 12, SourceName = "TheMovieDB.com" },
            ],
        };

        Assert.Equal(["tmdb://movie/12345", "imdb://series/tt0000001"], TvdbEntityMapper.SeriesCrossSourceIDs(series).Select(id => id.ToString()));
    }

    [Fact]
    public void Episodes_NameThemselvesNowhereElse_SinceTheListingCarriesNoIDs()
        => Assert.All(MapOnePiece().Episodes, episode => Assert.Empty(episode.CrossSourceIDs));

    [Theory]
    [InlineData(2, "IMDB", "tt0388629", "https://www.imdb.com/title/tt0388629/")]
    [InlineData(12, "TheMovieDB.com", "37854", "https://www.themoviedb.org/tv/37854")]
    [InlineData(10, "TheMovieDB.com", "12345", "https://www.themoviedb.org/movie/12345")]
    [InlineData(19, "TV Maze", "1505", "https://www.tvmaze.com/shows/1505")]
    [InlineData(6, "X (Twitter)", "OPcom_info", "https://x.com/OPcom_info")]
    [InlineData(null, "X (Twitter)", "OPcom_info", "https://x.com/OPcom_info")]
    [InlineData(24, "Wikipedia", "One_Piece_(anime)", "https://en.wikipedia.org/wiki/One_Piece_%28anime%29")]
    [InlineData(4, "Official Website", "https://www.toei-anim.co.jp/tv/onep/", "https://www.toei-anim.co.jp/tv/onep/")]
    [InlineData(null, "TheMovieDB.com", "37854", "https://www.themoviedb.org/tv/37854")]
    [InlineData(16, "IMDB", "nm0849028", null)]
    [InlineData(15, "TheMovieDB.com", "65510", null)]
    [InlineData(3, "TMS (Zap2It)", "EP02273408", null)]
    public void AShowsIDElsewhere_IsReadByTvDBsNumberForTheSite(int? type, string sourceName, string id, string? expected)
        => Assert.Equal(expected, TvdbEntityMapper.ToResource(new() { ID = id, Type = type, SourceName = sourceName })?.Url);

    [Theory]
    [InlineData(16, null, false, "imdbperson")]
    [InlineData(12, "IMDB", true, "tmdbtv")]
    [InlineData(null, "IMDB", true, "imdbperson")]
    [InlineData(null, "IMDB", false, "imdb")]
    [InlineData(99, "TheMovieDB.com", true, "tmdbperson")]
    [InlineData(99, "Somewhere Unknown", false, null)]
    public void ASitesSlug_ComesFromItsNumberThenItsName(int? type, string? sourceName, bool person, string? expected)
        => Assert.Equal(expected, TvdbEntityMapper.SourceSlug(type, sourceName, person));

    [Fact]
    public void Series_KeepsOneContentRatingPerCountryUnderItsTwoLetterCode()
    {
        var ratings = MapOnePiece().ContentRatings;

        Assert.Equal([("BR", "14"), ("ES", "12"), ("US", "TV-Y7")], ratings.Select(rating => (rating.CountryCode, rating.Rating)));
        Assert.All(ratings, rating => Assert.Null(rating.LanguageCode));
    }

    #endregion

    #region Seasons & Episodes

    [Fact]
    public void Seasons_AreTheDefaultSeasonTypesWithTheSpecialsLast()
    {
        var seasons = MapOnePiece().Seasons;

        Assert.Equal([1, 2, 0], seasons.Select(season => season.SeasonNumber));
        Assert.Equal([TvdbUtility.SeasonGuid(31893), TvdbUtility.SeasonGuid(31895), TvdbUtility.SeasonGuid(31892)], seasons.Select(season => season.ID));
    }

    [Fact]
    public void ASeason_KeepsOnlyANameOfItsOwn()
    {
        // The seasons on the show's own record carry no name, so one is
        // given here to see it used, and one is given TvDB's generic name.
        var series = ReadSeries();
        series.Seasons!.Single(season => season.ID is 31893).Name = "East Blue";
        series.Seasons!.Single(season => season.ID is 31895).Name = "Season 2";

        var seasons = TvdbEntityMapper.ToSeriesData(series, null, ReadDefaultEpisodes()).Seasons;

        Assert.Equal([("East Blue", TitleType.Main, "ja")], seasons[0].Titles.Select(title => (title.Value, title.Type, title.LanguageCode)));
        Assert.Empty(seasons[1].Titles);
        Assert.Empty(seasons[2].Titles);
    }

    [Fact]
    public void TheShowSeasonsAndEpisodes_PinTheImagesTheirRecordsName()
    {
        var series = MapOnePiece();

        Assert.Equal("series/81797/posters/5eec847d52a04.jpg", series.DefaultImageResourceIDs![ImageEntityType.Primary]);
        Assert.Equal("seasons/81797-1-3.jpg", Assert.Single(series.Seasons, season => season.ID == TvdbUtility.SeasonGuid(31893)).DefaultImageResourceIDs![ImageEntityType.Primary]);
        Assert.Empty(Assert.Single(series.Seasons, season => season.ID == TvdbUtility.SeasonGuid(31895)).DefaultImageResourceIDs!);
        Assert.Equal("v4/episode/361887/screencap/604df7d3ecf3a.jpg", series.Episodes[0].DefaultImageResourceIDs![ImageEntityType.Backdrop]);
    }

    [Fact]
    public void AnEpisodeInASeasonWithNoRecord_GetsASeasonMadeUpForIt()
    {
        var episodes = ReadDefaultEpisodes();
        episodes.Add(new() { ID = 100010, SeriesID = 81797, SeasonNumber = 3, Number = 1 });

        var series = TvdbEntityMapper.ToSeriesData(ReadSeries(), null, episodes);

        var season = Assert.Single(series.Seasons, season => season.SeasonNumber is 3);
        Assert.Equal("tvdb://season/81797-official-3", season.ID.ToString());
        Assert.Equal(season.ID, Assert.Single(series.Episodes, episode => episode.ID == TvdbUtility.EpisodeGuid(100010)).SeasonID);
    }

    [Fact]
    public void Episode_CarriesTheScalarsAcross()
    {
        var episode = MapOnePiece().Episodes[0];

        Assert.Equal(TvdbUtility.EpisodeGuid(361887), episode.ID);
        Assert.Equal(TvdbUtility.SeasonGuid(31893), episode.SeasonID);
        Assert.Equal(1, episode.SeasonNumber);
        Assert.Equal(1, episode.EpisodeNumber);
        Assert.Equal(EpisodeType.Episode, episode.Type);
        Assert.Equal(new DateOnly(1999, 10, 20), episode.AirDate);
        Assert.Equal(TimeSpan.FromMinutes(25), episode.Runtime);
        Assert.Equal(("TvDB", "https://thetvdb.com/series/one-piece/episodes/361887", "361887"), (episode.Resources[0].Name, episode.Resources[0].Url, episode.Resources[0].ID));
    }

    [Fact]
    public void Episode_HasItsOwnNameAsMainAndItsTranslationsAsOfficial()
    {
        var episode = MapOnePiece().Episodes[0];

        Assert.Contains(episode.Titles, title => title is { Type: TitleType.Main, Value: "俺はルフィ！海賊王になる男だ！", LanguageCode: "ja" });
        Assert.Contains(episode.Titles, title => title is { Type: TitleType.Official, Value: "I'm Luffy! The Man Who's Gonna Be King of the Pirates!", LanguageCode: "en" });
        Assert.Equal(["ja", "en"], episode.Overviews.Select(description => description.LanguageCode));
    }

    [Fact]
    public void EpisodeTitles_AreInTheLanguagesTheCoresMatchingSearches()
    {
        // The core's matching engine only searches an episode's English titles
        // naming no country (or the US) and those in the show's original
        // language, comparing two-letter codes.
        var series = MapOnePiece();

        Assert.Equal("ja", series.OriginalLanguageCode);
        Assert.All(series.Episodes.SelectMany(episode => episode.Titles), title =>
        {
            Assert.Contains(title.LanguageCode, (string[])["en", "ja"]);
            Assert.Null(title.CountryCode);
        });
        Assert.Contains(series.Episodes[0].Titles, title => title.LanguageCode is "en");
    }

    [Fact]
    public void AnEpisodesTranslation_KeepsItsNameAndOverviewOnlyInTheirOwnLanguages()
    {
        var episode = TvdbEntityMapper.ToSeriesData(
            ReadSeries(),
            new([], [], [], ["eng"]),
            ReadDefaultEpisodes(),
            new Dictionary<string, IReadOnlyList<TvdbEpisode>> { ["eng"] = ReadEpisodes("series-81797-episodes-default-eng.json") }
        ).Episodes[0];

        Assert.DoesNotContain(episode.Titles, title => title.LanguageCode is "en");
        Assert.Contains(episode.Overviews, overview => overview.LanguageCode is "en");
    }

    [Fact]
    public void Episode_WithoutAnOverview_GetsNone()
        => Assert.Empty(MapOnePiece().Episodes[1].Overviews);

    [Fact]
    public void Episodes_AreInSeasonOrderWithTheSpecialsLast()
    {
        var episodes = MapOnePiece().Episodes;

        Assert.Equal([361887, 361888, 619671], episodes.Select(episode => episode.ID.GetNumericID<int>()));
        Assert.Equal(EpisodeType.Special, episodes[2].Type);
        Assert.Equal(TvdbUtility.SeasonGuid(31892), episodes[2].SeasonID);
    }

    [Theory]
    [InlineData(0, EpisodeType.Special)]
    [InlineData(1, EpisodeType.Episode)]
    [InlineData(7, EpisodeType.Episode)]
    public void EpisodeType_ComesFromTheSeasonNumberBecauseTvDBHasNoSuchField(int seasonNumber, EpisodeType expected)
        => Assert.Equal(expected, TvdbEntityMapper.ToEpisodeType(seasonNumber));

    [Fact]
    public void ASpecial_KeepsWhereTvDBPlacesIt()
    {
        var special = ReadEpisodes("series-81797-episodes-page1.json").Single(episode => episode.ID is 619671);

        Assert.Equal((2, 1, (int?)null), (special.AirsBeforeSeason, special.AirsBeforeEpisode, special.AirsAfterSeason));
    }

    [Theory]
    [InlineData("official", true)]
    [InlineData("OFFICIAL", true)]
    [InlineData("dvd", false)]
    [InlineData("absolute", false)]
    public void ASpecialsPlacement_HoldsOnlyInTheAiredOrder(string seasonType, bool placed)
    {
        var special = ReadEpisodes("series-81797-episodes-page1.json").Single(episode => episode.ID is 619671);

        (int?, int?, int?)? expected = placed ? (2, 1, null) : null;

        Assert.Equal(expected, TvdbEntityMapper.SpecialPlacement(seasonType, special));
    }

    [Fact]
    public void ASpecial_IsStoredWithItsPlacement()
    {
        var special = Assert.Single(MapOnePiece().Episodes, episode => episode.ID == TvdbUtility.EpisodeGuid(619671));

        Assert.Equal((2, 1, (int?)null), (special.AirsBeforeSeasonNumber, special.AirsBeforeEpisodeNumber, special.AirsAfterSeasonNumber));
    }

    [Fact]
    public void AnEpisodeWithNoPlacement_HasNoneInTheAiredOrderEither()
        => Assert.Null(TvdbEntityMapper.SpecialPlacement("official", new TvdbEpisode { ID = 1, SeasonNumber = 1, Number = 1 }));

    #endregion

    #region Orderings

    [Fact]
    public void AnotherSeasonType_BecomesAnOrderingWithAGroupPerSeason()
    {
        var stored = new HashSet<int> { 361887, 361888, 619671 };

        var series = ReadSeries();
        series.Seasons!.Single(season => season.ID is 1785763).Name = "Volume 1";

        var ordering = TvdbEntityMapper.ToOrderingData(series, "dvd", ReadEpisodes("series-81797-episodes-dvd.json"), stored);

        Assert.NotNull(ordering);
        Assert.Equal("tvdb://ordering/81797-dvd", ordering.ID.ToString());
        Assert.Equal(TvdbUtility.SeriesGuid(81797), ordering.SeriesID);
        Assert.Equal("DVD Order", Assert.Single(ordering.Titles).Value);
        Assert.Equal(OrderingType.DVD, ordering.Type);
        Assert.Equal(["tvdb://season/81797-dvd-1", "tvdb://season/81797-dvd-2"], ordering.Groups.Select(group => group.ID.ToString()));
        // A season without a name of its own gets none; the core names it.
        Assert.Equal(["Volume 1", null], ordering.Groups.Select(group => group.Titles.SingleOrDefault()?.Value));
        Assert.Equal([TvdbUtility.EpisodeGuid(361888)], ordering.Groups[0].Episodes);
        // The DVD extra is not one of the show's stored episodes, so an
        // ordering of the show cannot hold it.
        Assert.Equal([TvdbUtility.EpisodeGuid(361887)], ordering.Groups[1].Episodes);
    }

    [Fact]
    public void AnOrderingsSeasonZero_IsItsSpecialGroup_AndComesLast()
    {
        var episodes = ReadEpisodes("series-81797-episodes-dvd.json");
        episodes.Add(new() { ID = 619671, SeriesID = 81797, SeasonNumber = 0, Number = 1 });

        var ordering = TvdbEntityMapper.ToOrderingData(ReadSeries(), "dvd", episodes, new HashSet<int> { 361887, 361888, 619671 })!;

        // The specials come last in viewing order, as the one special group.
        Assert.Equal([false, false, true], ordering.Groups.Select(group => group.IsSpecial));
        Assert.Equal([TvdbUtility.EpisodeGuid(619671)], ordering.Groups[2].Episodes);
    }

    [Fact]
    public void AnOrderingOfNothingStored_IsNoOrderingAtAll()
        => Assert.Null(TvdbEntityMapper.ToOrderingData(ReadSeries(), "dvd", ReadEpisodes("series-81797-episodes-dvd.json"), new HashSet<int>()));

    [Fact]
    public void AnOrderingsGroups_NeverTakeTheIDOfAStoredSeason()
    {
        var series = MapOnePiece();
        var ordering = TvdbEntityMapper.ToOrderingData(ReadSeries(), "absolute", ReadEpisodes("series-81797-episodes-absolute.json"), new HashSet<int> { 361887, 361888 })!;

        Assert.Empty(ordering.Groups.Select(group => group.ID).Intersect(series.Seasons.Select(season => season.ID)));
        Assert.Equal("Absolute Order", Assert.Single(ordering.Titles).Value);
    }

    #endregion

    #region Tags, Studios, Networks & People

    [Fact]
    public void Genres_AreGenreTagsApartFromTheTagOptions()
    {
        var (tags, entries) = TvdbEntityMapper.Tags(ReadSeries());

        Assert.Contains(tags, tag => tag.ID.ToString() == "tvdb://tag/genre/19" && tag is { Name: "Action", Kind: TagKind.Genre });
        Assert.Contains(tags, tag => tag.ID.ToString() == "tvdb://tag/63" && tag is { Name: "Story-based Drama", Kind: TagKind.Tag, Category: "TV Type or Format" });
        Assert.StartsWith("Drama series where you must watch all episodes in order.", tags.Single(tag => tag.Kind is TagKind.Tag).Overview);
        Assert.Equal(tags.Select(tag => tag.ID), entries.Select(entry => entry.TagID));
    }

    [Fact]
    public void Studios_AreTheStudiosAndProductionCompaniesEachOnce()
    {
        var (studios, entries) = TvdbEntityMapper.Studios(ReadSeries());

        Assert.Equal(["Toei Animation", "Shueisha"], studios.Select(studio => studio.Name));
        Assert.Equal([StudioType.Animation, StudioType.Production], entries.Select(entry => entry.Type));
    }

    [Fact]
    public void ACompanysKind_IsReadFromWhicheverOfItsFieldsItHas()
    {
        // TvDB's records carry both, but the plugin does not count on it.
        var series = new TvdbSeriesExtended
        {
            Companies =
            [
                new() { ID = 1, Name = "By Name", CompanyType = new() { CompanyTypeName = "Studio" } },
                new() { ID = 2, Name = "By Number", PrimaryCompanyType = 3 },
                new() { ID = 3, Name = "A Distributor", CompanyType = new() { CompanyTypeID = 4, CompanyTypeName = "Distributor" } },
            ],
        };

        var (_, entries) = TvdbEntityMapper.Studios(series);

        Assert.Equal([StudioType.Animation, StudioType.Production], entries.Select(entry => entry.Type));
    }

    [Fact]
    public void Networks_AreTheOriginalThenTheLatestThenTheRestEachOnce()
    {
        var networks = TvdbEntityMapper.Networks(ReadSeries());

        Assert.Equal(["tvdb://network/111", "tvdb://network/698"], networks.Select(network => network.ID.ToString()));
        Assert.Equal(["Fuji TV", "Adult Swim"], networks.Select(network => network.Name));
    }

    [Fact]
    public void People_AreCastForActorsAndCrewForTheRest()
    {
        var people = TvdbEntityMapper.People(ReadSeries());

        Assert.Equal(["Monkey D. Luffy", "Roronoa Zoro"], people.Cast.Select(cast => cast.Name));
        Assert.Equal(TvdbUtility.CharacterGuid(65111900), people.Cast[0].CharacterID);
        Assert.Equal(TvdbUtility.CreatorGuid(412417), people.Cast[0].CreatorID);
        Assert.Equal(CastRoleType.MainCharacter, people.Cast[0].RoleType);
        Assert.Equal(CastRoleType.None, people.Cast[1].RoleType);
        Assert.Equal([("Creator", CrewRoleType.SourceWork), ("Director", CrewRoleType.Director)], people.Crew.Select(crew => (crew.Name, crew.RoleType)));
    }

    [Fact]
    public void People_LeaveOutTheCreditsForOneEpisode()
    {
        var people = TvdbEntityMapper.People(ReadSeries());

        Assert.DoesNotContain(TvdbUtility.CreatorGuid(603), people.Creators.Keys);
        Assert.DoesNotContain(people.Cast, cast => cast.Name == "Guest Pirate");
        Assert.Equal([412417, 602, 604, 605], people.Creators.Keys.Select(id => id.GetNumericID<int>()));
    }

    [Fact]
    public void People_KeepThePhotosAndCharacterImagesAsResourceIDs()
    {
        var people = TvdbEntityMapper.People(ReadSeries());

        Assert.Equal("person/412417/primary.jpg", people.Portraits[TvdbUtility.CreatorGuid(412417)]);
        Assert.Equal("person/412417/65afe1871bc9d.jpg", people.Portraits[TvdbUtility.CharacterGuid(65111900)]);
        Assert.Equal(2, people.Portraits.Count);
    }

    [Fact]
    public void People_PinTheirPortraitsAsTheirDefaults()
    {
        var people = TvdbEntityMapper.People(ReadSeries());

        Assert.Equal("person/412417/primary.jpg", people.Creators[TvdbUtility.CreatorGuid(412417)].DefaultImageResourceIDs![ImageEntityType.Primary]);
        Assert.Equal("person/412417/65afe1871bc9d.jpg", people.Characters[TvdbUtility.CharacterGuid(65111900)].DefaultImageResourceIDs![ImageEntityType.Primary]);
        Assert.Null(people.Creators[TvdbUtility.CreatorGuid(602)].DefaultImageResourceIDs);
    }

    [Fact]
    public void People_LinkEachCharacterToItsPageUnderTheShow()
    {
        var people = TvdbEntityMapper.People(ReadSeries());

        var luffy = Assert.Single(people.Characters[TvdbUtility.CharacterGuid(65111900)].Resources);
        Assert.Equal(ResourceType.Metadata, luffy.Type);
        Assert.Equal("TvDB", luffy.Name);
        Assert.Equal("65111900", luffy.ID);
        Assert.Equal("https://thetvdb.com/series/one-piece/people/65111900", luffy.Url);
        Assert.Equal("https://thetvdb.com/series/one-piece/people/7002", Assert.Single(people.Characters[TvdbUtility.CharacterGuid(7002)].Resources).Url);
    }

    [Fact]
    public void People_GiveACharacterItsAliasesEachOncePerLanguage()
    {
        // No credit of One Piece has aliases on TvDB, so Luffy is given
        // some here.
        var series = ReadSeries();
        series.Characters![0].Aliases =
        [
            new() { Language = "eng", Name = "Straw Hat Luffy" },
            new() { Language = "jpn", Name = "モンキー・D・ルフィ" },
            new() { Language = "eng", Name = "Straw Hat Luffy" },
            new() { Language = "eng", Name = "Monkey D. Luffy" },
            new() { Language = "eng", Name = " " },
        ];
        var people = TvdbEntityMapper.People(series);

        var luffy = people.Characters[TvdbUtility.CharacterGuid(65111900)];
        Assert.Equal([("en", "Straw Hat Luffy"), ("ja", "モンキー・D・ルフィ")], luffy.AlternativeNames.Select(alias => (alias.LanguageCode, alias.Name)));
        Assert.Empty(people.Characters[TvdbUtility.CharacterGuid(7002)].AlternativeNames);
    }

    [Fact]
    public void People_LeaveACharacterUnlinkedWithoutTheShowsSlug()
    {
        var series = ReadSeries();
        series.Slug = " ";

        var people = TvdbEntityMapper.People(series);

        Assert.Empty(people.Characters[TvdbUtility.CharacterGuid(65111900)].Resources);
        Assert.Single(people.Creators[TvdbUtility.CreatorGuid(412417)].Resources);
    }

    [Theory]
    [InlineData(null, "one-piece", "https://thetvdb.com/series/one-piece/people/65111900")]
    [InlineData(null, "a b/c", "https://thetvdb.com/series/a%20b%2Fc/people/65111900")]
    [InlineData(null, null, null)]
    [InlineData("https://thetvdb.com/people/412417-mayumi-tanaka", "one-piece", "https://thetvdb.com/series/one-piece/people/65111900")]
    [InlineData("https://thetvdb.com/people/412417-mayumi-tanaka", null, null)]
    [InlineData("https://thetvdb.com/series/one-piece/people/651119000", "one-piece", "https://thetvdb.com/series/one-piece/people/65111900")]
    [InlineData("https://example.com/series/one-piece/people/65111900", "one-piece", "https://thetvdb.com/series/one-piece/people/65111900")]
    [InlineData("https://thetvdb.com/series/one-piece/people/65111900", null, "https://thetvdb.com/series/one-piece/people/65111900")]
    [InlineData(" https://thetvdb.com/series/one-piece/people/65111900-monkey-d-luffy ", "other", "https://thetvdb.com/series/one-piece/people/65111900-monkey-d-luffy")]
    [InlineData("https://www.thetvdb.com/movies/some-movie/people/65111900", null, "https://www.thetvdb.com/movies/some-movie/people/65111900")]
    public void CharacterPage_PrefersTheCreditsOwnPageThenTheShowsSlug(string? url, string? slug, string? expected)
        => Assert.Equal(expected, TvdbEntityMapper.CharacterPage(new() { ID = 65111900, Name = "Monkey D. Luffy", Url = url }, slug));

    [Theory]
    [InlineData("Director", CrewRoleType.Director)]
    [InlineData("Executive Producer", CrewRoleType.Producer)]
    [InlineData("Showrunner", CrewRoleType.Producer)]
    [InlineData("Creator", CrewRoleType.SourceWork)]
    [InlineData("Writer", CrewRoleType.None)]
    [InlineData(null, CrewRoleType.None)]
    public void ACrewJob_MapsOntoAKindOfJobWhereOneFits(string? peopleType, CrewRoleType expected)
        => Assert.Equal(expected, TvdbEntityMapper.ToCrewRoleType(peopleType));

    #endregion

    #region Stored Show

    [Fact]
    public void TheStoredShow_KeepsWhatOnlyTvDBHas()
    {
        var show = TvdbEntityMapper.ToStoredSeries(ReadSeries(), ReadDefaultEpisodes(), ["dvd"], _fetchedAt);

        Assert.Equal(81797, show.ID);
        Assert.Equal("one-piece", show.Slug);
        Assert.Equal("Continuing", show.Status);
        Assert.Equal("official", show.SeasonType);
        Assert.Equal(["dvd"], show.AlternateSeasonTypes);
        Assert.Equal(_fetchedAt, show.FetchedAt);
    }

    [Fact]
    public void TheStoredShow_KeepsItsImagesAsResourceIDs()
    {
        var show = TvdbEntityMapper.ToStoredSeries(ReadSeries(), ReadDefaultEpisodes(), [], _fetchedAt);

        Assert.Equal("series/81797/posters/5eec847d52a04.jpg", show.Poster);
        Assert.Equal([1, 2, 3, 4, 5, 7], show.Artworks.Select(artwork => artwork.ID));
        Assert.Equal("v4/series/81797/backgrounds/616009a8bd688.jpg", show.Artworks.Single(artwork => artwork.ID is 3).Path);
        Assert.Equal(31893, show.Artworks.Single(artwork => artwork.ID is 7).SeasonID);
        Assert.Equal(new Dictionary<int, string> { [31892] = "seasons/81797-0-3.jpg", [31893] = "seasons/81797-1-3.jpg" }, show.SeasonPosters);
        Assert.Equal(
            new Dictionary<int, string> { [361887] = "v4/episode/361887/screencap/604df7d3ecf3a.jpg", [619671] = "episodes/81797/619671.jpg" },
            show.EpisodeThumbnails
        );
    }

    #endregion

    #region Dates & Search

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("1999/10/20")]
    public void ABadDate_IsNullRatherThanAThrow(string? value)
        => Assert.Null(TvdbEntityMapper.ParseDate(value));

    [Fact]
    public void ASearchHit_BecomesASearchResultOnTheTvdbSource()
    {
        var hit = JsonSerializer.Deserialize<TvdbResponse<List<TvdbSearchResult>>>(Fixture.Read("search-one-piece.json"), TvdbJson.Options)!.Data![0];

        var result = TvdbEntityMapper.ToSearchResult(hit, 81797);

        Assert.Equal("tvdb://series/81797", result.ID.ToString());
        Assert.Equal(MetadataSource.Tvdb, result.Source);
        Assert.Equal("One Piece", result.Title);
        Assert.Equal("ja", result.OriginalLanguageCode);
        Assert.Equal(new PartialDateOnly(new DateOnly(1999, 10, 20)), result.FirstAiredAt);
        Assert.Equal("https://artworks.thetvdb.com/banners/series/81797/posters/5eec847d52a04.jpg", result.PosterUrl);
        Assert.StartsWith("The adventures of Monkey D. Luffy", result.Overview);
        // TvDB sends no genres on a show's hit.
        Assert.Empty(result.Genres);
    }

    [Fact]
    public void ASearchHit_TakesItsFullAirDateAndEnglishOverviewWhereItHasThem()
    {
        var hit = new TvdbSearchResult
        {
            Name = "ワンピース",
            Year = "1999",
            FirstAirTime = "1999-10-20",
            Overview = "海賊王を目指す少年の物語。",
            Overviews = new Dictionary<string, string> { ["eng"] = "Monkey D. Luffy sets out to become King of the Pirates." },
        };

        var result = TvdbEntityMapper.ToSearchResult(hit, 81797);

        Assert.Equal(new PartialDateOnly(new DateOnly(1999, 10, 20)), result.FirstAiredAt);
        Assert.Equal("Monkey D. Luffy sets out to become King of the Pirates.", result.Overview);
        Assert.Equal(new PartialDateOnly(1999), TvdbEntityMapper.ToSearchResult(new TvdbSearchResult { Year = "1999", FirstAirTime = "" }, 81797).FirstAiredAt);
    }

    [Fact]
    public void AShowsOwnRecord_BecomesASearchResult_ForAHint()
    {
        var result = TvdbEntityMapper.ToSearchResult(ReadSeries());

        Assert.Equal("tvdb://series/81797", result.ID.ToString());
        Assert.Equal(("One Piece", "ワンピース"), (result.Title, result.OriginalTitle));
        Assert.Contains("Wan Pisu", result.AlternateTitles);
        Assert.Contains("One Piece (1998)", result.AlternateTitles);
        Assert.DoesNotContain("One Piece", result.AlternateTitles);
        Assert.DoesNotContain("ワンピース", result.AlternateTitles);
        Assert.Equal(result.AlternateTitles.Count, result.AlternateTitles.Distinct().Count());
        Assert.Equal("ja", result.OriginalLanguageCode);
        Assert.StartsWith("The adventures of Monkey D. Luffy", result.Overview);
        Assert.Contains("Anime", result.Genres);
        Assert.Equal(new PartialDateOnly(new DateOnly(1999, 10, 20)), result.FirstAiredAt);
        Assert.Null(result.Seasons);
    }

    [Fact]
    public void AShowsEpisodes_BecomeSeasonsToLineUpByAirDate_WithoutTheSpecials()
    {
        var season = Assert.Single(TvdbEntityMapper.ToSearchSeasons(ReadDefaultEpisodes()));

        Assert.Equal(1, season.SeasonNumber);
        Assert.Equal(2, season.EpisodeCount);
        Assert.Equal(new PartialDateOnly(new DateOnly(1999, 10, 20)), season.FirstAiredAt);
        Assert.Equal(new DateOnly(1999, 10, 20), season.FirstEpisodeAiredAt);
        Assert.NotNull(season.Episodes);
        Assert.Equal([(1, new DateOnly(1999, 10, 20)), (2, new DateOnly(1999, 11, 17))], season.Episodes.Select(episode => (episode.EpisodeNumber, episode.AiredAt!.Value)));
        Assert.All(season.Episodes, episode => Assert.False(string.IsNullOrWhiteSpace(episode.Title)));
    }

    [Fact]
    public void AnEpisodeListedTwiceOrUndated_IsLinedUpOnceAndLeftUndated()
    {
        var seasons = TvdbEntityMapper.ToSearchSeasons(
        [
            new TvdbEpisode { ID = 3, SeasonNumber = 2, Number = 1, Aired = "2001-04-01" },
            new TvdbEpisode { ID = 3, SeasonNumber = 2, Number = 1, Aired = "2001-04-01" },
            new TvdbEpisode { ID = 4, SeasonNumber = 2, Number = 2 },
            new TvdbEpisode { ID = 1, SeasonNumber = 1, Number = 1, Aired = "2000-01-01" },
            new TvdbEpisode { ID = 0, SeasonNumber = 1, Number = 2, Aired = "2000-01-08" },
        ]);

        Assert.Equal([1, 2], seasons.Select(season => season.SeasonNumber));
        Assert.Equal([(1, (DateOnly?)new DateOnly(2001, 4, 1)), (2, null)], seasons[1].Episodes!.Select(episode => (episode.EpisodeNumber, episode.AiredAt)));
        Assert.Single(seasons[0].Episodes!);
    }

    [Theory]
    [InlineData("Continuing", ReleaseStatus.Releasing)]
    [InlineData("Ended", ReleaseStatus.Finished)]
    [InlineData("Upcoming", ReleaseStatus.NotYetReleased)]
    [InlineData(null, ReleaseStatus.Unknown)]
    public void AStatus_MapsOntoAReleaseStatus(string? status, ReleaseStatus expected)
        => Assert.Equal(expected, TvdbEntityMapper.ToReleaseStatus(status));

    [Fact]
    public void ASearchHit_YieldsItsBareIDFromEitherField()
    {
        Assert.Equal(81797, TvdbEntityMapper.GetSeriesID(new TvdbSearchResult { TvdbID = "81797" }));
        Assert.Equal(999999, TvdbEntityMapper.GetSeriesID(new TvdbSearchResult { ID = "series-999999" }));
        Assert.Null(TvdbEntityMapper.GetSeriesID(new TvdbSearchResult { ID = "movie-abc" }));
        Assert.Null(TvdbEntityMapper.GetSeriesID(new TvdbSearchResult()));
    }

    [Fact]
    public void ASearchHit_OffersItsAliasesAndTranslationsAsItsOtherNames()
    {
        var hit = JsonSerializer.Deserialize<TvdbResponse<List<TvdbSearchResult>>>(Fixture.Read("search-one-piece.json"), TvdbJson.Options)!.Data![0];

        var result = TvdbEntityMapper.ToSearchResult(hit, 81797);

        // The English translation is the title and the show's own name the
        // original title, so neither is offered again.
        Assert.Equal("One Piece", result.OriginalTitle);
        Assert.Equal(["ワンピース", "Wan Pisu"], result.AlternateTitles);
    }

    #endregion
}
