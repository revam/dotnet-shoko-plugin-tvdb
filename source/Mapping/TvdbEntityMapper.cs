using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Abstractions.Metadata.Stub;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Storage;

namespace Shoko.Plugin.Tvdb.Mapping;

/// <summary>
/// Turns what TvDB returns into what the core's stores keep. Pure and
/// static, so the shape of the mapping can be tested without a network, a
/// store or a server.
/// </summary>
public static class TvdbEntityMapper
{
    #region Season Types

    /// <summary>
    /// The season type a show's own seasons are in: the one TvDB uses for
    /// it by default, which is what its <c>default</c> episode listing
    /// answers in.
    /// </summary>
    /// <param name="series">The show.</param>
    /// <returns>The season type, lower-case, <c>official</c> when the show names none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static string DefaultSeasonType(TvdbSeriesExtended series)
    {
        ArgumentNullException.ThrowIfNull(series);

        if (series.DefaultSeasonType is { } defaultID)
        {
            var type = series.SeasonTypes?.FirstOrDefault(seasonType => seasonType.ID == defaultID)?.Type
                ?? series.Seasons?.FirstOrDefault(season => season.Type?.ID == defaultID)?.Type?.Type;
            if (!string.IsNullOrWhiteSpace(type))
                return type.Trim().ToLowerInvariant();
        }

        return TvdbUtility.OfficialSeasonType;
    }

    /// <summary>
    /// The season types a show has seasons in besides its default one, each
    /// of which is stored as an ordering.
    /// </summary>
    /// <param name="series">The show.</param>
    /// <param name="defaultSeasonType">The show's default season type.</param>
    /// <returns>The season types, lower-case, in the order TvDB lists them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> AlternateSeasonTypes(TvdbSeriesExtended series, string defaultSeasonType)
    {
        ArgumentNullException.ThrowIfNull(series);

        return
        [
            .. (series.Seasons ?? [])
                .Select(season => season.Type?.Type?.Trim().ToLowerInvariant())
                .Where(type => !string.IsNullOrEmpty(type) && type.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                .Where(type => !string.Equals(type, defaultSeasonType, StringComparison.Ordinal) && !string.Equals(type, "default", StringComparison.Ordinal))
                .Select(type => type!)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// What an ordering made of a season type follows.
    /// </summary>
    /// <param name="seasonType">The season type, e.g. <c>dvd</c>.</param>
    /// <returns>The ordering type.</returns>
    public static OrderingType ToOrderingType(string seasonType)
        => seasonType switch
        {
            "official" => OrderingType.OriginalAirDate,
            "dvd" or "altdvd" => OrderingType.DVD,
            "absolute" => OrderingType.Absolute,
            "regional" => OrderingType.TV,
            _ => OrderingType.Unknown,
        };

    /// <summary>
    /// The name of a season type, as the show names it, else as TvDB
    /// lists it, else made up from its key.
    /// </summary>
    /// <remarks>
    /// A show names its alternate orderings where TvDB only numbers them:
    /// One Piece's <c>alternate</c> and <c>alttwo</c> are its
    /// <c>Story Order</c> and <c>Streaming Order</c>.
    /// </remarks>
    /// <param name="series">The show.</param>
    /// <param name="seasonType">The season type, e.g. <c>dvd</c>.</param>
    /// <returns>The name, e.g. <c>DVD Order</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static string SeasonTypeName(TvdbSeriesExtended series, string seasonType)
    {
        ArgumentNullException.ThrowIfNull(series);

        var type = series.SeasonTypes?.FirstOrDefault(type => string.Equals(type.Type, seasonType, StringComparison.OrdinalIgnoreCase))
            ?? series.Seasons?.FirstOrDefault(season => string.Equals(season.Type?.Type, seasonType, StringComparison.OrdinalIgnoreCase))?.Type;
        var name = string.IsNullOrWhiteSpace(type?.AlternateName) ? type?.Name : type.AlternateName;
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();

        return seasonType switch
        {
            "official" => "Aired Order",
            "dvd" => "DVD Order",
            "absolute" => "Absolute Order",
            "alternate" => "Alternate Order",
            "regional" => "Regional Order",
            "altdvd" => "Alternate DVD Order",
            "alttwo" => "Alternate Order 2",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(seasonType) + " Order",
        };
    }

    #endregion

    #region Series

    /// <summary>
    /// Maps a show with its seasons and episodes into what the series store
    /// keeps.
    /// </summary>
    /// <param name="series">The show as TvDB returned it, with its translations.</param>
    /// <param name="languages">The three-letter codes of the languages to take the show's translations in.</param>
    /// <param name="episodes">The show's episodes in its default season type.</param>
    /// <param name="episodeTranslations">The same episodes in other languages, by the three-letter code they were asked for in.</param>
    /// <returns>The series to store.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> or <paramref name="episodes"/> is <see langword="null"/>.</exception>
    public static MetadataSeriesData ToSeriesData(
        TvdbSeriesExtended series,
        IReadOnlyList<string>? languages,
        IReadOnlyList<TvdbEpisode> episodes,
        IReadOnlyDictionary<string, IReadOnlyList<TvdbEpisode>>? episodeTranslations = null
    )
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(episodes);

        var seasonType = DefaultSeasonType(series);
        var seasons = Seasons(series, seasonType, episodes);
        var seasonIDs = seasons.ToDictionary(season => season.SeasonNumber, season => season.ID);
        var translated = (episodeTranslations ?? new Dictionary<string, IReadOnlyList<TvdbEpisode>>())
            .ToDictionary(pair => pair.Key, pair => pair.Value.GroupBy(episode => episode.ID).ToDictionary(group => group.Key, group => group.First()));
        var status = ToReleaseStatus(series.Status?.Name);
        return new()
        {
            ID = TvdbUtility.SeriesGuid(series.ID),
            Titles = SeriesTitles(series, languages),
            Overviews = SeriesOverviews(series, languages),
            Type = AnimeType.TV,
            AirDate = ParseDate(series.FirstAired) is { } firstAired ? new PartialDateOnly(firstAired) : null,
            EndDate = status is ReleaseStatus.Finished && ParseDate(series.LastAired) is { } lastAired ? new PartialDateOnly(lastAired) : null,
            ReleaseStatus = status,
            OriginalLanguageCode = TvdbUtility.ToLanguageCode(series.OriginalLanguage),
            Popularity = series.Score is > 0 ? series.Score : null,
            Resources = SeriesResources(series),
            CrossSourceIDs = SeriesCrossSourceIDs(series),
            ContentRatings = ContentRatings(series),
            Seasons = seasons,
            Episodes =
            [
                .. episodes
                    .Where(episode => episode.ID > 0)
                    .DistinctBy(episode => episode.ID)
                    .OrderBy(episode => episode.SeasonNumber is 0 ? int.MaxValue : episode.SeasonNumber)
                    .ThenBy(episode => episode.Number)
                    .Select(episode => ToEpisodeData(series, episode, seasonIDs, translated)),
            ],
            DefaultImageResourceIDs = TvdbImages.ToDefaultImages(ImageEntityType.Primary, TvdbImages.ToResourceID(series.Image)),
        };
    }

    /// <summary>
    /// Maps a show's name, aliases and translations into titles.
    /// </summary>
    /// <remarks>
    /// The show's own name is its main title, in its original language. Its
    /// names in the languages asked for are official titles, the aliases among
    /// them and the show's own aliases synonyms, each in their own language. A
    /// title already given in a language is not given again.
    /// </remarks>
    /// <param name="series">The show, with its translations.</param>
    /// <param name="languages">The three-letter codes of the languages to take translations in, in order.</param>
    /// <returns>The titles, the main one first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<ITitle> SeriesTitles(TvdbSeriesExtended series, IReadOnlyList<string>? languages)
    {
        ArgumentNullException.ThrowIfNull(series);

        List<ITitle> titles = [];
        if (!string.IsNullOrWhiteSpace(series.Name))
            titles.Add(Title(series.Name, series.OriginalLanguage, TitleType.Main));

        foreach (var (language, translations) in TranslationsIn(series.Translations?.NameTranslations, languages))
        {
            foreach (var translation in translations.Where(translation => translation.IsAlias is not true && !string.IsNullOrWhiteSpace(translation.Name)))
                titles.Add(Title(translation.Name!, language, TitleType.Official));
            foreach (var translation in translations.Where(translation => translation.IsAlias is true && !string.IsNullOrWhiteSpace(translation.Name)))
                titles.Add(Title(translation.Name!, language, TitleType.Synonym));
        }

        foreach (var alias in series.Aliases ?? [])
            if (!string.IsNullOrWhiteSpace(alias.Name))
                titles.Add(Title(alias.Name, alias.Language, TitleType.Synonym));

        return Deduplicate(titles);
    }

    /// <summary>
    /// Maps a show's overview and its translations into overviews.
    /// </summary>
    /// <param name="series">The show, with its translations.</param>
    /// <param name="languages">The three-letter codes of the languages to take translations in, in order.</param>
    /// <returns>The overviews, the original one first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<IText> SeriesOverviews(TvdbSeriesExtended series, IReadOnlyList<string>? languages)
    {
        ArgumentNullException.ThrowIfNull(series);

        List<IText> overviews = [];
        if (!string.IsNullOrWhiteSpace(series.Overview))
            overviews.Add(Text(series.Overview, series.OriginalLanguage));
        foreach (var (language, translations) in TranslationsIn(series.Translations?.OverviewTranslations, languages))
            foreach (var translation in translations.Where(translation => !string.IsNullOrWhiteSpace(translation.Overview)))
                overviews.Add(Text(translation.Overview!, language));

        return Deduplicate(overviews);
    }

    // A record's translations in each of the languages asked for, in the
    // order they were asked for, matched on TvDB's own codes.
    private static IEnumerable<(string Language, List<TvdbTranslation> Translations)> TranslationsIn(IReadOnlyList<TvdbTranslation>? translations, IReadOnlyList<string>? languages)
    {
        if (translations is not { Count: > 0 } || languages is not { Count: > 0 })
            yield break;

        foreach (var language in languages.Where(language => !string.IsNullOrWhiteSpace(language)).Select(language => language.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var inLanguage = translations.Where(translation => string.Equals(translation.Language?.Trim(), language, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inLanguage.Count > 0)
                yield return (language, inLanguage);
        }
    }

    /// <summary>
    /// A show's page on TvDB and its IDs on other sites, each with its bare
    /// ID where the site has IDs of its own.
    /// </summary>
    /// <param name="series">The show.</param>
    /// <returns>The resources, TvDB's page first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<Resource> SeriesResources(TvdbSeriesExtended series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var id = TvdbUtility.FormatID(series.ID);
        List<Resource> resources =
        [
            new()
            {
                Type = ResourceType.Metadata,
                Name = "TvDB",
                Url = TvdbUtility.SeriesUrl(series.ID, series.Slug),
                ID = id,
            },
        ];
        foreach (var remote in series.RemoteIDs ?? [])
            if (ToResource(remote) is { } resource && !resources.Any(existing => string.Equals(existing.Url, resource.Url, StringComparison.Ordinal)))
                resources.Add(resource);

        return resources;
    }

    /// <summary>
    /// The IDs other metadata sources gave the show, as TvDB lists them:
    /// its IMDb title, its TMDB show (or film) and its TVmaze show.
    /// </summary>
    /// <remarks>
    /// Only the sources that describe the show itself; the other sites
    /// TvDB lists, such as EIDR, Wikidata or the show's socials, stay
    /// among its resources. IMDb and TVmaze are parsed on every call rather
    /// than kept, so a plugin registering either later is still found.
    /// </remarks>
    /// <param name="series">The show.</param>
    /// <returns>The IDs, each once, in the order TvDB lists them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<MetadataGuid> SeriesCrossSourceIDs(TvdbSeriesExtended series)
    {
        ArgumentNullException.ThrowIfNull(series);

        return
        [
            .. (series.RemoteIDs ?? [])
                .Where(remote => remote.ID?.Trim() is { Length: > 0 and <= MetadataGuid.MaxIDLength })
                .Select(remote => (ID: remote.ID!.Trim(), Slug: SourceSlug(remote.Type, remote.SourceName, person: false)))
                .Select(remote => remote.Slug switch
                {
                    "imdb" => new MetadataGuid(MetadataSource.Parse("imdb"), MetadataEntityType.Series, remote.ID),
                    "tmdbtv" => new MetadataGuid(MetadataSource.TMDB, MetadataEntityType.Series, remote.ID),
                    "tmdb" => new MetadataGuid(MetadataSource.TMDB, MetadataEntityType.Movie, remote.ID),
                    "tvmaze" => new MetadataGuid(MetadataSource.Parse("tvmaze"), MetadataEntityType.Series, remote.ID),
                    _ => null,
                })
                .OfType<MetadataGuid>()
                .Distinct(),
        ];
    }

    /// <summary>
    /// A show's ID on another site as a resource.
    /// </summary>
    /// <remarks>
    /// The site is read from TvDB's number for it, which tells a show's
    /// IMDb or TMDB ID from a person's or a movie's, and from its name when
    /// the number is missing or unknown. IDs of people, companies, seasons
    /// and episodes are left out.
    /// </remarks>
    /// <param name="remote">The ID.</param>
    /// <returns>The resource, or <see langword="null"/> for a site the plugin cannot link to.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="remote"/> is <see langword="null"/>.</exception>
    public static Resource? ToResource(TvdbRemoteID remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        if (string.IsNullOrWhiteSpace(remote.ID))
            return null;

        var id = remote.ID.Trim();
        var escaped = Uri.EscapeDataString(id);
        return SourceSlug(remote.Type, remote.SourceName, person: false) switch
        {
            "imdb" => new() { Type = ResourceType.CrossReference, Name = "IMDb", Url = $"https://www.imdb.com/title/{escaped}/", ID = id },
            "tmdbtv" => new() { Type = ResourceType.CrossReference, Name = "TMDB", Url = $"https://www.themoviedb.org/tv/{escaped}", ID = id },
            "tmdb" => new() { Type = ResourceType.CrossReference, Name = "TMDB", Url = $"https://www.themoviedb.org/movie/{escaped}", ID = id },
            "tvmaze" => new() { Type = ResourceType.CrossReference, Name = "TVmaze", Url = $"https://www.tvmaze.com/shows/{escaped}", ID = id },
            "eidr" => new() { Type = ResourceType.CrossReference, Name = "EIDR", Url = $"https://ui.eidr.org/view/content?id={escaped}", ID = id },
            { } slug => SharedResource(slug, id, remote.SourceName),
            null => IsUrl(id) && !string.IsNullOrWhiteSpace(remote.SourceName) ? new() { Type = ResourceType.Website, Name = remote.SourceName.Trim(), Url = id } : null,
        };
    }

    /// <summary>
    /// The slug TvDB gives a site it keeps IDs for, from its number for
    /// the site as <c>/sources/types</c> lists them, else from the site's
    /// name.
    /// </summary>
    /// <remarks>
    /// A name alone does not say whose ID it is, IMDb and TMDB keeping one
    /// kind for shows and another for people, so it is read as the kind the
    /// record is.
    /// </remarks>
    /// <param name="type">TvDB's number for the site.</param>
    /// <param name="sourceName">The site's name, e.g. <c>IMDB</c>.</param>
    /// <param name="person">Whether the ID is on a person's record rather than a show's.</param>
    /// <returns>The slug, e.g. <c>imdbperson</c>, or <see langword="null"/> for a site TvDB does not list.</returns>
    public static string? SourceSlug(int? type, string? sourceName, bool person)
        => type switch
        {
            2 => "imdb",
            3 => "zap2it",
            4 => "official",
            5 => "facebook",
            6 => "twitter",
            7 => "reddit",
            8 => "fansite",
            9 => "instagram",
            10 => "tmdb",
            11 => "youtube",
            12 => "tmdbtv",
            13 => "eidr",
            14 => "eidrparty",
            15 => "tmdbperson",
            16 => "imdbperson",
            17 => "imdbcompany",
            18 => "wikidata",
            19 => "tvmaze",
            20 => "linkedin",
            21 => "tvmazeperson",
            22 => "tvmazeseason",
            23 => "tvmazeepisode",
            24 => "wikipedia",
            25 => "tiktok",
            26 => "linkedincompany",
            27 => "tvmazecompany",
            28 => "tmdbcollection",
            _ => sourceName?.Trim().ToLowerInvariant() switch
            {
                "imdb" => person ? "imdbperson" : "imdb",
                "themoviedb.com" or "tmdb" => person ? "tmdbperson" : "tmdbtv",
                "tv maze" or "tvmaze" => person ? "tvmazeperson" : "tvmaze",
                "eidr" => person ? "eidrparty" : "eidr",
                "x (twitter)" or "twitter" or "x" => "twitter",
                "official website" => "official",
                "fan site" => "fansite",
                "tms (zap2it)" => "zap2it",
                "facebook" or "instagram" or "reddit" or "youtube" or "tiktok" or "wikidata" or "wikipedia" or "linkedin" => sourceName.Trim().ToLowerInvariant(),
                _ => null,
            },
        };

    // The sites a show and a person link to alike: their socials, Wikidata,
    // Wikipedia and their own websites.
    private static Resource? SharedResource(string slug, string id, string? sourceName)
        => slug switch
        {
            "wikidata" => new() { Type = ResourceType.CrossReference, Name = "Wikidata", Url = $"https://www.wikidata.org/wiki/{Uri.EscapeDataString(id)}", ID = id },
            "wikipedia" => IsUrl(id) ? new() { Type = ResourceType.Website, Name = "Wikipedia", Url = id } : new() { Type = ResourceType.Website, Name = "Wikipedia", Url = $"https://en.wikipedia.org/wiki/{Uri.EscapeDataString(id)}", ID = id },
            "facebook" => Social("Facebook", "https://www.facebook.com/", id),
            "twitter" => Social("X", "https://x.com/", id),
            "instagram" => Social("Instagram", "https://www.instagram.com/", id),
            "reddit" => Social("Reddit", "https://www.reddit.com/r/", id),
            "youtube" => Social("YouTube", "https://www.youtube.com/", id),
            "tiktok" => Social("TikTok", "https://www.tiktok.com/@", id),
            "official" or "fansite" => IsUrl(id) ? new() { Type = ResourceType.Website, Name = string.IsNullOrWhiteSpace(sourceName) ? "Website" : sourceName.Trim(), Url = id } : null,
            _ => null,
        };

    /// <summary>
    /// A show's content ratings, one per country.
    /// </summary>
    /// <remarks>
    /// TvDB names a rating's country in three letters, which is turned into
    /// the two-letter code the core keeps; a rating whose country cannot be
    /// read is dropped. The language is left to the core, which takes the
    /// country's main one.
    /// </remarks>
    /// <param name="series">The show.</param>
    /// <returns>The ratings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<MetadataContentRatingData> ContentRatings(TvdbSeriesExtended series)
    {
        ArgumentNullException.ThrowIfNull(series);

        return
        [
            .. (series.ContentRatings ?? [])
                .Where(rating => !string.IsNullOrWhiteSpace(rating.Name))
                .Select(rating => (Rating: rating.Name!.Trim(), Country: TvdbUtility.ToCountryCode(rating.Country)))
                .Where(rating => rating.Country is not null && rating.Rating.Length <= 128)
                .DistinctBy(rating => rating.Country)
                .Select(rating => new MetadataContentRatingData { CountryCode = rating.Country!, Rating = rating.Rating }),
        ];
    }

    /// <summary>
    /// Where a show is in its release, from TvDB's status name.
    /// </summary>
    /// <param name="status">The status name, e.g. <c>Continuing</c>.</param>
    /// <returns>The release status.</returns>
    public static ReleaseStatus ToReleaseStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "continuing" => ReleaseStatus.Releasing,
            "ended" => ReleaseStatus.Finished,
            "upcoming" => ReleaseStatus.NotYetReleased,
            _ => ReleaseStatus.Unknown,
        };

    #endregion

    #region Seasons

    /// <summary>
    /// The seasons of a show's default season type, with one made up for any
    /// season number an episode has and TvDB lists no record for.
    /// </summary>
    /// <param name="series">The show.</param>
    /// <param name="seasonType">The show's default season type.</param>
    /// <param name="episodes">The show's episodes in that season type.</param>
    /// <returns>The seasons, the specials last.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> or <paramref name="episodes"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<MetadataSeasonData> Seasons(TvdbSeriesExtended series, string seasonType, IReadOnlyList<TvdbEpisode> episodes)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(episodes);

        var records = (series.Seasons ?? [])
            .Where(season => season.ID > 0 && string.Equals(season.Type?.Type, seasonType, StringComparison.OrdinalIgnoreCase))
            .GroupBy(season => season.Number)
            .ToDictionary(group => group.Key, group => group.OrderBy(season => season.ID).First());
        var numbers = records.Keys.Concat(episodes.Where(episode => episode.ID > 0).Select(episode => episode.SeasonNumber)).Distinct();
        return
        [
            .. numbers
                .OrderBy(number => number is 0 ? int.MaxValue : number)
                .Select(number => new MetadataSeasonData
                {
                    ID = records.TryGetValue(number, out var record) ? TvdbUtility.SeasonGuid(record.ID) : TvdbUtility.SeasonTypeSeasonGuid(series.ID, seasonType, number),
                    SeasonNumber = number,
                    Titles = SeasonTitles(number, records.GetValueOrDefault(number)?.Name, series.OriginalLanguage),
                    DefaultImageResourceIDs = TvdbImages.ToDefaultImages(ImageEntityType.Primary, TvdbImages.ToResourceID(records.GetValueOrDefault(number)?.Image)),
                }),
        ];
    }

    /// <summary>
    /// The English name of a season by its number, as TvDB shows it.
    /// </summary>
    /// <param name="number">The season number.</param>
    /// <returns><c>Specials</c> for season zero, <c>Season N</c> otherwise.</returns>
    public static string SeasonName(int number)
        => number is 0 ? "Specials" : $"Season {number.ToString(CultureInfo.InvariantCulture)}";

    // A season gets its English name by number as its main title, TvDB's
    // base record rarely naming one, and its own name besides when it has one.
    private static IReadOnlyList<ITitle> SeasonTitles(int number, string? name, string? originalLanguage)
    {
        List<ITitle> titles = [Title(SeasonName(number), "eng", TitleType.Main)];
        if (!string.IsNullOrWhiteSpace(name))
            titles.Add(Title(name, originalLanguage, TitleType.Official));

        return Deduplicate(titles);
    }

    #endregion

    #region Episodes

    /// <summary>
    /// Maps an episode into what the series store keeps.
    /// </summary>
    /// <param name="series">The show the episode belongs to.</param>
    /// <param name="episode">The episode.</param>
    /// <param name="seasonIDs">The show's seasons by number.</param>
    /// <param name="translations">The episodes in other languages, by three-letter code and then by episode.</param>
    /// <returns>The episode to store.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/>, <paramref name="episode"/> or <paramref name="seasonIDs"/> is <see langword="null"/>.</exception>
    public static MetadataEpisodeData ToEpisodeData(
        TvdbSeriesExtended series,
        TvdbEpisode episode,
        IReadOnlyDictionary<int, MetadataGuid> seasonIDs,
        IReadOnlyDictionary<string, Dictionary<int, TvdbEpisode>>? translations = null
    )
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(seasonIDs);

        List<ITitle> titles = [];
        List<IText> overviews = [];
        if (!string.IsNullOrWhiteSpace(episode.Name))
            titles.Add(Title(episode.Name, series.OriginalLanguage, TitleType.Main));
        if (!string.IsNullOrWhiteSpace(episode.Overview))
            overviews.Add(Text(episode.Overview, series.OriginalLanguage));
        foreach (var (language, byEpisode) in translations ?? new Dictionary<string, Dictionary<int, TvdbEpisode>>())
        {
            if (!byEpisode.TryGetValue(episode.ID, out var translated))
                continue;
            if (!string.IsNullOrWhiteSpace(translated.Name))
                titles.Add(Title(translated.Name, language, TitleType.Official));
            if (!string.IsNullOrWhiteSpace(translated.Overview))
                overviews.Add(Text(translated.Overview, language));
        }

        var id = TvdbUtility.FormatID(episode.ID);
        var placement = SpecialPlacement(DefaultSeasonType(series), episode);
        return new()
        {
            ID = TvdbUtility.EpisodeGuid(episode.ID),
            SeasonID = seasonIDs.GetValueOrDefault(episode.SeasonNumber),
            SeasonNumber = episode.SeasonNumber,
            EpisodeNumber = episode.Number,
            Type = ToEpisodeType(episode.SeasonNumber),
            Runtime = TimeSpan.FromMinutes(episode.Runtime is > 0 ? episode.Runtime.Value : 0),
            AirDate = ParseDate(episode.Aired),
            AirsBeforeSeasonNumber = placement?.AirsBeforeSeason,
            AirsBeforeEpisodeNumber = placement?.AirsBeforeEpisode,
            AirsAfterSeasonNumber = placement?.AirsAfterSeason,
            Titles = Deduplicate(titles),
            Overviews = Deduplicate(overviews),
            Resources =
            [
                new()
                {
                    Type = ResourceType.Metadata,
                    Name = "TvDB",
                    Url = TvdbUtility.EpisodeUrl(episode.ID, series.Slug),
                    ID = id,
                },
            ],
            DefaultImageResourceIDs = TvdbImages.ToDefaultImages(ImageEntityType.Backdrop, TvdbImages.ToResourceID(episode.Image)),
        };
    }

    /// <summary>
    /// What Shoko calls an episode in a given TvDB season.
    /// </summary>
    /// <remarks>
    /// TvDB has no episode-type field. Season zero is its universal
    /// convention for everything that is not a numbered episode, which covers
    /// specials, OVAs, recaps, opening and ending credits and web shorts all at
    /// once. They all land on <see cref="EpisodeType.Special"/>, because
    /// picking between <see cref="EpisodeType.Credits"/>,
    /// <see cref="EpisodeType.Trailer"/> and <see cref="EpisodeType.Parody"/>
    /// off a free-text season name would be guessing.
    /// </remarks>
    /// <param name="seasonNumber">The season number.</param>
    /// <returns>The episode type.</returns>
    public static EpisodeType ToEpisodeType(int seasonNumber)
        => seasonNumber is 0 ? EpisodeType.Special : EpisodeType.Episode;

    /// <summary>
    /// Where TvDB places a special among the show's numbered episodes.
    /// </summary>
    /// <remarks>
    /// TvDB places specials in its aired order, so the placement only holds
    /// when the show's seasons are in that order.
    /// </remarks>
    /// <param name="seasonType">The show's default season type.</param>
    /// <param name="episode">The episode.</param>
    /// <returns>
    /// The season and episode it airs before and the season it airs after, or
    /// <see langword="null"/> in another season type or when it has none.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="seasonType"/> or <paramref name="episode"/> is <see langword="null"/>.</exception>
    public static (int? AirsBeforeSeason, int? AirsBeforeEpisode, int? AirsAfterSeason)? SpecialPlacement(string seasonType, TvdbEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(seasonType);
        ArgumentNullException.ThrowIfNull(episode);

        if (!string.Equals(seasonType, TvdbUtility.OfficialSeasonType, StringComparison.OrdinalIgnoreCase))
            return null;
        if (episode is { AirsBeforeSeason: null, AirsBeforeEpisode: null, AirsAfterSeason: null })
            return null;

        return (episode.AirsBeforeSeason, episode.AirsBeforeEpisode, episode.AirsAfterSeason);
    }

    #endregion

    #region Orderings

    /// <summary>
    /// Maps one of a show's other season types into a global ordering of the
    /// show, one group per season. TvDB's season 0 is the special group,
    /// last in viewing order; the core numbers the groups.
    /// </summary>
    /// <param name="series">The show.</param>
    /// <param name="seasonType">The season type, e.g. <c>dvd</c>.</param>
    /// <param name="episodes">The show's episodes in that season type.</param>
    /// <param name="storedEpisodeIDs">The episodes stored under the show, the only ones an ordering may hold.</param>
    /// <returns>The ordering, or <see langword="null"/> when none of its episodes is stored.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static MetadataOrderingData? ToOrderingData(TvdbSeriesExtended series, string seasonType, IReadOnlyList<TvdbEpisode> episodes, IReadOnlySet<int> storedEpisodeIDs)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(seasonType);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(storedEpisodeIDs);

        var names = (series.Seasons ?? [])
            .Where(season => string.Equals(season.Type?.Type, seasonType, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(season.Name))
            .GroupBy(season => season.Number)
            .ToDictionary(group => group.Key, group => group.First().Name!.Trim());
        var groups = episodes
            .Where(episode => storedEpisodeIDs.Contains(episode.ID))
            .DistinctBy(episode => episode.ID)
            .GroupBy(episode => episode.SeasonNumber)
            .OrderBy(group => group.Key is 0 ? int.MaxValue : group.Key)
            .Select(group => new MetadataOrderingGroupData
            {
                ID = TvdbUtility.SeasonTypeSeasonGuid(series.ID, seasonType, group.Key),
                Name = names.GetValueOrDefault(group.Key) ?? SeasonName(group.Key),
                IsSpecial = group.Key is 0,
                Episodes = [.. group.OrderBy(episode => episode.Number).Select(episode => TvdbUtility.EpisodeGuid(episode.ID))],
            })
            .ToList();
        if (groups.Count is 0)
            return null;

        return new()
        {
            ID = TvdbUtility.OrderingGuid(series.ID, seasonType),
            SeriesID = TvdbUtility.SeriesGuid(series.ID),
            Name = SeasonTypeName(series, seasonType),
            Type = ToOrderingType(seasonType),
            Groups = groups,
        };
    }

    #endregion

    #region Tags, Studios & Networks

    /// <summary>
    /// A show's genres and tag options as tags, and how they apply to it.
    /// </summary>
    /// <param name="series">The show.</param>
    /// <returns>The tags to store, and the show's own list of them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static (IReadOnlyList<MetadataTagData> Tags, IReadOnlyList<MetadataEntryTagData> Entries) Tags(TvdbSeriesExtended series)
    {
        ArgumentNullException.ThrowIfNull(series);

        List<MetadataTagData> tags = [];
        foreach (var genre in (series.Genres ?? []).Where(genre => genre.ID > 0 && !string.IsNullOrWhiteSpace(genre.Name)).DistinctBy(genre => genre.ID))
            tags.Add(new() { ID = TvdbUtility.GenreGuid(genre.ID), Name = genre.Name!.Trim(), Kind = TagKind.Genre });
        foreach (var option in (series.Tags ?? []).Where(option => option.ID > 0 && !string.IsNullOrWhiteSpace(option.Name)).DistinctBy(option => option.ID))
        {
            tags.Add(new()
            {
                ID = TvdbUtility.TagGuid(option.ID),
                Name = option.Name!.Trim(),
                Overview = option.HelpText?.Trim() ?? string.Empty,
                Category = string.IsNullOrWhiteSpace(option.TagName) ? null : option.TagName.Trim(),
                Kind = TagKind.Tag,
            });
        }

        return (tags, [.. tags.Select(tag => new MetadataEntryTagData { TagID = tag.ID })]);
    }

    /// <summary>
    /// The studios credited on a show: its studios as animation studios and
    /// its production companies as production studios.
    /// </summary>
    /// <param name="series">The show.</param>
    /// <returns>The studios to store, and the show's own list of them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static (IReadOnlyList<MetadataStudioData> Studios, IReadOnlyList<MetadataEntryStudioData> Entries) Studios(TvdbSeriesExtended series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var companies = (series.Companies ?? [])
            .Where(company => company.ID > 0 && !string.IsNullOrWhiteSpace(company.Name))
            .Select(company => (Company: company, Type: ToStudioType(company)))
            .Where(pair => pair.Type is not StudioType.None)
            .DistinctBy(pair => pair.Company.ID)
            .ToList();
        return
        (
            [.. companies.Select(pair => new MetadataStudioData { ID = TvdbUtility.StudioGuid(pair.Company.ID), Name = pair.Company.Name!.Trim() })],
            [.. companies.Select(pair => new MetadataEntryStudioData { StudioID = TvdbUtility.StudioGuid(pair.Company.ID), Type = pair.Type })]
        );
    }

    /// <summary>
    /// The networks a show aired on: the one it first aired on, the one it
    /// airs on now, and every other network credited on it.
    /// </summary>
    /// <param name="series">The show.</param>
    /// <returns>The networks to store, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<MetadataNetworkData> Networks(TvdbSeriesExtended series)
    {
        ArgumentNullException.ThrowIfNull(series);

        IEnumerable<TvdbCompany?> candidates = [series.OriginalNetwork, series.LatestNetwork, .. (series.Companies ?? []).Where(company => CompanyType(company) is 1)];
        return
        [
            .. candidates
                .Where(company => company is { ID: > 0 } && !string.IsNullOrWhiteSpace(company.Name))
                .Select(company => company!)
                .DistinctBy(company => company.ID)
                .Select(company => new MetadataNetworkData { ID = TvdbUtility.NetworkGuid(company.ID), Name = company.Name!.Trim() }),
        ];
    }

    private static StudioType ToStudioType(TvdbCompany company)
        => CompanyType(company) switch
        {
            2 => StudioType.Animation,
            3 => StudioType.Production,
            _ => StudioType.None,
        };

    // TvDB's company types: 1 a network, 2 a studio, 3 a production
    // company, 4 a distributor, 5 a special effects company. A record carries
    // the number, the name or both.
    private static int? CompanyType(TvdbCompany company)
        => company.CompanyType?.CompanyTypeID
            ?? company.PrimaryCompanyType
            ?? company.CompanyType?.CompanyTypeName?.Trim().ToLowerInvariant() switch
            {
                "network" => 1,
                "studio" => 2,
                "production company" => 3,
                "distributor" => 4,
                "special effects" or "special effects company" => 5,
                _ => null,
            };

    #endregion

    #region People

    /// <summary>
    /// A show's cast and crew, from the credits TvDB files under
    /// <c>characters</c>.
    /// </summary>
    /// <remarks>
    /// Only the credits for the whole show are read; one for a single episode
    /// is left out. An actor's credit is a cast credit on the character; every
    /// other credit is a crew credit under TvDB's name for the job. A
    /// character links to its page under the show, which needs the show's
    /// slug, since TvDB has no dereferrer for characters.
    /// </remarks>
    /// <param name="series">The show.</param>
    /// <returns>The people to store and the show's credits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static TvdbPeople People(TvdbSeriesExtended series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var people = new TvdbPeople();
        var slug = string.IsNullOrWhiteSpace(series.Slug) ? null : series.Slug.Trim();
        var credits = (series.Characters ?? [])
            .Where(credit => credit.EpisodeID is null or 0)
            .OrderBy(credit => credit.Sort ?? int.MaxValue)
            .ThenBy(credit => credit.ID);
        foreach (var credit in credits)
        {
            var creatorID = ReadCreator(credit, people);
            if (IsCastCredit(credit))
            {
                AddCast(credit, creatorID, slug, people);
                continue;
            }

            if (creatorID is null || string.IsNullOrWhiteSpace(credit.PeopleType))
                continue;

            var job = credit.PeopleType.Trim();
            if (!people.Crew.Any(existing => existing.CreatorID == creatorID && string.Equals(existing.Name, job, StringComparison.Ordinal)))
                people.Crew.Add(new() { CreatorID = creatorID, Name = job, RoleType = ToCrewRoleType(job) });
        }

        return people;
    }

    /// <summary>
    /// The kind of job a TvDB crew credit is, where its name maps onto one.
    /// </summary>
    /// <param name="peopleType">TvDB's name for the job, e.g. <c>Director</c>.</param>
    /// <returns>The kind of job.</returns>
    public static CrewRoleType ToCrewRoleType(string? peopleType)
        => peopleType?.Trim().ToLowerInvariant() switch
        {
            "director" => CrewRoleType.Director,
            "producer" or "executive producer" or "showrunner" => CrewRoleType.Producer,
            "creator" => CrewRoleType.SourceWork,
            _ => CrewRoleType.None,
        };

    private static MetadataGuid? ReadCreator(TvdbCharacter credit, TvdbPeople people)
    {
        if (credit.PeopleID is not > 0 || string.IsNullOrWhiteSpace(credit.PersonName))
            return null;

        var peopleID = credit.PeopleID.Value;
        var creatorID = TvdbUtility.CreatorGuid(peopleID);
        var id = TvdbUtility.FormatID(peopleID);
        people.Creators.TryAdd(creatorID, new()
        {
            ID = creatorID,
            Name = credit.PersonName.Trim(),
            Resources = [new() { Type = ResourceType.Metadata, Name = "TvDB", Url = TvdbUtility.PersonUrl(peopleID), ID = id }],
            DefaultImageResourceIDs = TvdbImages.ToPortraitDefault(TvdbImages.ToResourceID(credit.PersonImage)),
        });
        if (TvdbImages.ToResourceID(credit.PersonImage) is { } photo)
            people.Portraits.TryAdd(creatorID, photo);

        return creatorID;
    }

    private static void AddCast(TvdbCharacter credit, MetadataGuid? creatorID, string? slug, TvdbPeople people)
    {
        MetadataGuid? characterID = null;
        if (credit.ID > 0 && !string.IsNullOrWhiteSpace(credit.Name))
        {
            characterID = TvdbUtility.CharacterGuid(credit.ID);
            var name = credit.Name.Trim();
            people.Characters.TryAdd(characterID, new()
            {
                ID = characterID,
                Name = name,
                AlternativeNames = CharacterAliases(credit, name),
                Resources = CharacterPage(credit, slug) is { } page
                    ? [new() { Type = ResourceType.Metadata, Name = "TvDB", Url = page, ID = TvdbUtility.FormatID(credit.ID) }]
                    : [],
                DefaultImageResourceIDs = TvdbImages.ToPortraitDefault(TvdbImages.ToResourceID(credit.Image)),
            });
            if (TvdbImages.ToResourceID(credit.Image) is { } image)
                people.Portraits.TryAdd(characterID, image);
        }

        if (characterID is null && creatorID is null)
            return;

        if (people.Cast.Any(existing => existing.CharacterID == characterID && existing.CreatorID == creatorID))
            return;

        people.Cast.Add(new()
        {
            CharacterID = characterID,
            CreatorID = creatorID,
            Name = string.IsNullOrWhiteSpace(credit.Name) ? credit.PersonName!.Trim() : credit.Name.Trim(),
            RoleType = credit.IsFeatured ? CastRoleType.MainCharacter : CastRoleType.None,
        });
    }

    /// <summary>
    /// The page of a character on TvDB, which is filed among the people
    /// of its show: <c>https://thetvdb.com/series/{slug}/people/{id}</c>.
    /// </summary>
    /// <param name="credit">The character's credit.</param>
    /// <param name="slug">The show's slug, when known.</param>
    /// <returns>The credit's own <c>url</c> when it is that page, else the page made from the slug, else <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="credit"/> is <see langword="null"/>.</exception>
    public static string? CharacterPage(TvdbCharacter credit, string? slug)
    {
        ArgumentNullException.ThrowIfNull(credit);

        if (credit.ID is not > 0)
            return null;

        var id = TvdbUtility.FormatID(credit.ID);
        if (IsCharacterPage(credit.Url, id))
            return credit.Url!.Trim();

        return string.IsNullOrWhiteSpace(slug) ? null : $"https://thetvdb.com/series/{Uri.EscapeDataString(slug.Trim())}/people/{id}";
    }

    private static IReadOnlyList<MetadataNameData> CharacterAliases(TvdbCharacter credit, string name)
        => OtherNames((credit.Aliases ?? []).Select(alias => (alias.Language, alias.Name)), name);

    // Other names, each once per language, leaving out the name the person or
    // character is stored under. TvDB repeats a person's aliases without
    // their language under `translations.aliases`, so a name given with a
    // language is not given again without one.
    private static IReadOnlyList<MetadataNameData> OtherNames(IEnumerable<(string? Language, string? Name)> names, string name)
    {
        var others = names
            .Where(alias => !string.IsNullOrWhiteSpace(alias.Name))
            .Select(alias => new MetadataNameData { Name = alias.Name!.Trim(), LanguageCode = TvdbUtility.ToLanguageCode(alias.Language) })
            .Where(alias => !string.Equals(alias.Name, name, StringComparison.Ordinal))
            .DistinctBy(alias => (alias.LanguageCode, alias.Name))
            .ToList();
        var withLanguage = others.Where(alias => alias.LanguageCode is not null).Select(alias => alias.Name).ToHashSet(StringComparer.Ordinal);
        return [.. others.Where(alias => alias.LanguageCode is not null || !withLanguage.Contains(alias.Name))];
    }

    // The url a credit carries counts only when it is the character's own
    // page under a show or a movie, not the page of the person playing it.
    private static bool IsCharacterPage(string? url, string id)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (!string.Equals(uri.Host, "thetvdb.com", StringComparison.OrdinalIgnoreCase) && !string.Equals(uri.Host, "www.thetvdb.com", StringComparison.OrdinalIgnoreCase))
            return false;

        return uri.AbsolutePath.Trim('/').Split('/') is ["series" or "movies", { Length: > 0 }, "people", var last]
            && (string.Equals(last, id, StringComparison.Ordinal) || last.StartsWith(id + "-", StringComparison.Ordinal));
    }

    // TvDB's people types 3 and 4 are an actor and a guest star.
    private static bool IsCastCredit(TvdbCharacter credit)
        => credit.Type is 3 or 4
            || string.Equals(credit.PeopleType?.Trim(), "Actor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(credit.PeopleType?.Trim(), "Guest Star", StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Person Details

    /// <summary>
    /// What the plugin keeps of a person's own record.
    /// </summary>
    /// <remarks>
    /// The names are the person's aliases, their name in each language and
    /// the aliases given with it; the biographies are the person's own and
    /// their overview in each language. Blank ones are left out.
    /// </remarks>
    /// <param name="person">The record as TvDB returned it.</param>
    /// <param name="fetchedAt">When it was fetched, in UTC.</param>
    /// <returns>The record to keep.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="person"/> is <see langword="null"/>.</exception>
    public static TvdbStoredPerson ToStoredPerson(TvdbPersonExtended person, DateTime fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(person);

        var nameTranslations = person.Translations?.NameTranslations ?? [];
        var name = !string.IsNullOrWhiteSpace(person.Name)
            ? person.Name
            : nameTranslations.FirstOrDefault(translation => translation.IsPrimary is true && !string.IsNullOrWhiteSpace(translation.Name))?.Name;
        IEnumerable<(string? Language, string? Value)> names =
        [
            .. (person.Aliases ?? []).Select(alias => (alias.Language, alias.Name)),
            .. nameTranslations.Select(translation => (translation.Language, translation.Name)),
            .. nameTranslations.SelectMany(translation => (translation.Aliases ?? []).Select(alias => (translation.Language, (string?)alias))),
            .. (person.Translations?.Aliases ?? []).Select(alias => ((string?)null, (string?)alias)),
        ];
        IEnumerable<(string? Language, string? Value)> biographies =
        [
            .. (person.Biographies ?? []).Select(biography => (biography.Language, biography.Biography)),
            .. (person.Translations?.OverviewTranslations ?? []).Select(translation => (translation.Language, translation.Overview)),
        ];
        return new()
        {
            ID = person.ID,
            Found = true,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Slug = string.IsNullOrWhiteSpace(person.Slug) ? null : person.Slug.Trim(),
            Image = TvdbImages.ToResourceID(person.Image),
            Birth = string.IsNullOrWhiteSpace(person.Birth) ? null : person.Birth.Trim(),
            Death = string.IsNullOrWhiteSpace(person.Death) ? null : person.Death.Trim(),
            Gender = person.Gender,
            Names = StoredTexts(names),
            Biographies = StoredTexts(biographies),
            RemoteIDs =
            [
                .. (person.RemoteIDs ?? [])
                    .Where(remote => !string.IsNullOrWhiteSpace(remote.ID))
                    .Select(remote => new TvdbStoredRemoteID { ID = remote.ID!.Trim(), Type = remote.Type, SourceName = remote.SourceName?.Trim() }),
            ],
            LastUpdated = string.IsNullOrWhiteSpace(person.LastUpdated) ? null : person.LastUpdated.Trim(),
            FetchedAt = fetchedAt,
        };
    }

    /// <summary>
    /// What the plugin keeps of a person TvDB did not have, so they are
    /// not asked for again until the record is stale.
    /// </summary>
    /// <param name="peopleID">TvDB person ID.</param>
    /// <param name="fetchedAt">When they were asked for, in UTC.</param>
    /// <returns>The record to keep.</returns>
    public static TvdbStoredPerson MissingPerson(int peopleID, DateTime fetchedAt)
        => new() { ID = peopleID, Found = false, FetchedAt = fetchedAt };

    /// <summary>
    /// Adds what a person's own record says to what their credits said.
    /// </summary>
    /// <remarks>
    /// The record's name wins, and the credited name, the aliases and the
    /// translated names become other names. The overview is the
    /// biography in the first of <paramref name="languages"/> that has one,
    /// then English, then any. The links are the credits' own, then the
    /// person's page and their IDs elsewhere. A record TvDB did not have
    /// adds nothing.
    /// </remarks>
    /// <param name="creator">The person as their credits have them.</param>
    /// <param name="person">What the plugin keeps of the person's record, if anything.</param>
    /// <param name="languages">The three-letter codes to choose the biography by, in order.</param>
    /// <returns>The person to store.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="creator"/> is <see langword="null"/>.</exception>
    public static MetadataCreatorData WithPersonDetails(MetadataCreatorData creator, TvdbStoredPerson? person, IReadOnlyList<string>? languages = null)
    {
        ArgumentNullException.ThrowIfNull(creator);

        if (person is not { Found: true })
            return creator;

        var name = string.IsNullOrWhiteSpace(person.Name) ? creator.Name : person.Name.Trim();
        IEnumerable<(string? Language, string? Name)> names =
        [
            (null, creator.Name),
            .. person.Names.Select(text => (text.Language, (string?)text.Value)),
        ];
        List<Resource> resources = [.. creator.Resources];
        if (PersonPage(person.ID, person.Slug) is { } page)
            resources.Add(new() { Type = ResourceType.Metadata, Name = "TvDB", Url = page, ID = TvdbUtility.FormatID(person.ID) });
        foreach (var remote in person.RemoteIDs)
            if (ToPersonResource(remote) is { } resource)
                resources.Add(resource);

        return creator with
        {
            Name = name,
            Overview = ChooseBiography(person.Biographies, languages) ?? creator.Overview,
            AlternativeNames = [.. creator.AlternativeNames.Concat(OtherNames(names, name)).Where(alias => !string.Equals(alias.Name, name, StringComparison.Ordinal)).DistinctBy(alias => (alias.LanguageCode, alias.Name))],
            Gender = ToPersonGender(person.Gender) is var gender and not PersonGender.Unknown ? gender : creator.Gender,
            BirthDay = ParseFuzzyDate(person.Birth) ?? creator.BirthDay,
            DeathDay = ParseFuzzyDate(person.Death) ?? creator.DeathDay,
            Resources = [.. resources.DistinctBy(resource => resource.Url, StringComparer.Ordinal)],
            DefaultImageResourceIDs = creator.DefaultImageResourceIDs ?? TvdbImages.ToPortraitDefault(person.Image),
        };
    }

    /// <summary>
    /// A person's page on TvDB under their slug,
    /// <c>https://thetvdb.com/people/412417-mayumi-tanaka</c>. TvDB's slugs
    /// for people have the ID in front already; one without it is given it.
    /// </summary>
    /// <param name="peopleID">TvDB person ID.</param>
    /// <param name="slug">The person's slug, when known.</param>
    /// <returns>The page, or <see langword="null"/> without a slug.</returns>
    public static string? PersonPage(int peopleID, string? slug)
    {
        if (peopleID <= 0 || string.IsNullOrWhiteSpace(slug))
            return null;

        var id = TvdbUtility.FormatID(peopleID);
        var trimmed = slug.Trim();
        var path = trimmed.StartsWith(id + "-", StringComparison.Ordinal) ? trimmed : $"{id}-{trimmed}";
        return $"https://thetvdb.com/people/{Uri.EscapeDataString(path)}";
    }

    /// <summary>
    /// A person's ID on another site as a resource.
    /// </summary>
    /// <remarks>
    /// The site is read as <see cref="SourceSlug"/> reads it. An IMDb ID links
    /// to the person's page when it is a person's (<c>nm</c>) ID, and a TMDB
    /// or TVmaze ID when it is a number; IDs of shows, movies, companies and
    /// EIDR's parties are left out. The rest link as a show's would.
    /// </remarks>
    /// <param name="remote">The ID.</param>
    /// <returns>The resource, or <see langword="null"/> for an ID the plugin cannot link to.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="remote"/> is <see langword="null"/>.</exception>
    public static Resource? ToPersonResource(TvdbStoredRemoteID remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        if (string.IsNullOrWhiteSpace(remote.ID))
            return null;

        var id = remote.ID.Trim();
        var escaped = Uri.EscapeDataString(id);
        return SourceSlug(remote.Type, remote.SourceName, person: true) switch
        {
            "imdbperson" or "imdb" => IsImdbPersonID(id) ? new() { Type = ResourceType.CrossReference, Name = "IMDb", Url = $"https://www.imdb.com/name/{escaped}/", ID = id } : null,
            "tmdbperson" => IsNumber(id) ? new() { Type = ResourceType.CrossReference, Name = "TMDB", Url = $"https://www.themoviedb.org/person/{escaped}", ID = id } : null,
            "tvmazeperson" => IsNumber(id) ? new() { Type = ResourceType.CrossReference, Name = "TVmaze", Url = $"https://www.tvmaze.com/people/{escaped}", ID = id } : null,
            "reddit" => IsUrl(id) ? new() { Type = ResourceType.Social, Name = "Reddit", Url = id } : null,
            { } slug => SharedResource(slug, id, remote.SourceName),
            null => null,
        };
    }

    /// <summary>
    /// A person's gender, from TvDB's number for it.
    /// </summary>
    /// <param name="gender">1 for male, 2 for female, 3 for what <c>/genders</c> calls other.</param>
    /// <returns>The gender, <see cref="PersonGender.Unknown"/> for any other number.</returns>
    public static PersonGender ToPersonGender(int? gender)
        => gender switch
        {
            1 => PersonGender.Male,
            2 => PersonGender.Female,
            3 => PersonGender.NonBinary,
            _ => PersonGender.Unknown,
        };

    /// <summary>
    /// Parses a date TvDB may know only part of: <c>yyyy-MM-dd</c>,
    /// <c>yyyy-MM</c> or <c>yyyy</c>, possibly with a time after it, and with
    /// an unknown part written as zeroes.
    /// </summary>
    /// <remarks>
    /// A part that is zero or out of range is taken as unknown, and so is a
    /// day without its month. Anything else that cannot be read is no date.
    /// </remarks>
    /// <param name="value">The raw value.</param>
    /// <returns>The known parts, or <see langword="null"/> when none are.</returns>
    public static FuzzyDateOnly? ParseFuzzyDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var end = trimmed.IndexOfAny([' ', 'T']);
        var parts = (end < 0 ? trimmed : trimmed[..end]).Split('-');
        if (parts.Length is 0 or > 3 || parts[0].Length is not 4 || parts.Any(part => part.Length is 0 || !part.All(char.IsAsciiDigit)))
            return null;

        // A part too long for a number is out of range, so unknown.
        var numbers = parts.Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1).ToList();
        int? year = numbers[0] is >= 1 and <= 9999 ? numbers[0] : null;
        int? month = numbers.Count > 1 && numbers[1] is >= 1 and <= 12 ? numbers[1] : null;
        int? day = month is { } knownMonth && numbers.Count > 2 && numbers[2] >= 1 && numbers[2] <= DaysInMonth(year, knownMonth) ? numbers[2] : null;
        if (year is null && month is null)
            return null;

        return new FuzzyDateOnly(year, month, day);
    }

    // Without a year, 29 February is a valid day.
    private static int DaysInMonth(int? year, int month)
        => year is { } known ? DateTime.DaysInMonth(known, month) : month is 2 ? 29 : DateTime.DaysInMonth(2001, month);

    private static string? ChooseBiography(IReadOnlyList<TvdbStoredText> biographies, IReadOnlyList<string>? languages)
    {
        var usable = biographies.Where(biography => !string.IsNullOrWhiteSpace(biography.Value)).ToList();
        if (usable.Count is 0)
            return null;

        foreach (var language in (languages ?? []).Append("eng"))
        {
            if (TvdbUtility.ToLanguageCode(language) is not { } code)
                continue;

            if (usable.FirstOrDefault(biography => string.Equals(TvdbUtility.ToLanguageCode(biography.Language), code, StringComparison.Ordinal)) is { } match)
                return match.Value.Trim();
        }

        return usable[0].Value.Trim();
    }

    private static List<TvdbStoredText> StoredTexts(IEnumerable<(string? Language, string? Value)> texts)
        => [
            .. texts
                .Where(text => !string.IsNullOrWhiteSpace(text.Value))
                .Select(text => new TvdbStoredText { Language = string.IsNullOrWhiteSpace(text.Language) ? null : text.Language.Trim(), Value = text.Value!.Trim() })
                .DistinctBy(text => (text.Language, text.Value)),
        ];

    private static bool IsImdbPersonID(string id)
        => id.Length > 2 && id.StartsWith("nm", StringComparison.OrdinalIgnoreCase) && id[2..].All(char.IsAsciiDigit);

    private static bool IsNumber(string id)
        => id.Length > 0 && id.All(char.IsAsciiDigit);

    #endregion

    #region Stored Show

    /// <summary>
    /// What the plugin keeps of a show besides the core's stores: its slug,
    /// status and season types, and where its images are.
    /// </summary>
    /// <param name="series">The show.</param>
    /// <param name="episodes">The show's episodes in its default season type.</param>
    /// <param name="alternateSeasonTypes">The other season types stored as orderings.</param>
    /// <param name="fetchedAt">When the show was fetched.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static TvdbStoredSeries ToStoredSeries(TvdbSeriesExtended series, IReadOnlyList<TvdbEpisode> episodes, IReadOnlyList<string> alternateSeasonTypes, DateTime fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(alternateSeasonTypes);

        var seasonType = DefaultSeasonType(series);
        return new()
        {
            ID = series.ID,
            Slug = string.IsNullOrWhiteSpace(series.Slug) ? null : series.Slug.Trim(),
            Status = series.Status?.Name,
            SeasonType = seasonType,
            AlternateSeasonTypes = [.. alternateSeasonTypes],
            Poster = TvdbImages.ToResourceID(series.Image),
            Artworks = [.. StoredArtworks(series)],
            SeasonPosters = (series.Seasons ?? [])
                .Where(season => season.ID > 0 && string.Equals(season.Type?.Type, seasonType, StringComparison.OrdinalIgnoreCase))
                .Select(season => (season.ID, Path: TvdbImages.ToResourceID(season.Image)))
                .Where(pair => pair.Path is not null)
                .DistinctBy(pair => pair.ID)
                .ToDictionary(pair => pair.ID, pair => pair.Path!),
            EpisodeThumbnails = episodes
                .Where(episode => episode.ID > 0)
                .Select(episode => (episode.ID, Path: TvdbImages.ToResourceID(episode.Image)))
                .Where(pair => pair.Path is not null)
                .DistinctBy(pair => pair.ID)
                .ToDictionary(pair => pair.ID, pair => pair.Path!),
            FetchedAt = fetchedAt,
        };
    }

    // The show's and its seasons' posters, backgrounds, banners and logos,
    // each once; artwork for an episode or of a kind the plugin does not offer
    // is left out.
    private static IEnumerable<TvdbStoredArtwork> StoredArtworks(TvdbSeriesExtended series)
        => (series.Artworks ?? [])
            .Where(artwork => artwork.EpisodeID is null or 0 && TvdbImages.ToImageType(artwork.Type) is not ImageEntityType.None)
            .Where(artwork => TvdbImages.IsSeasonArtwork(artwork.Type) == artwork.SeasonID is > 0)
            .Select(artwork => (Artwork: artwork, Path: TvdbImages.ToResourceID(artwork.Image)))
            .Where(pair => pair.Path is not null)
            .DistinctBy(pair => (pair.Path, pair.Artwork.Type, pair.Artwork.SeasonID))
            .Select(pair => new TvdbStoredArtwork
            {
                ID = pair.Artwork.ID,
                Type = pair.Artwork.Type,
                Path = pair.Path!,
                Language = pair.Artwork.Language,
                Width = pair.Artwork.Width is > 0 ? pair.Artwork.Width : null,
                Height = pair.Artwork.Height is > 0 ? pair.Artwork.Height : null,
                SeasonID = pair.Artwork.SeasonID is > 0 ? pair.Artwork.SeasonID : null,
            });

    #endregion

    #region Search

    /// <summary>
    /// Maps a search hit into what the core's search hands back.
    /// </summary>
    /// <remarks>
    /// The title and overview are the English ones where the hit has them,
    /// the other names are its aliases and translations, and the first air
    /// date is the full date where known, else the year.
    /// </remarks>
    /// <param name="result">The hit.</param>
    /// <param name="seriesID">The hit's TvDB series ID.</param>
    /// <returns>The search result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static MetadataSeriesSearchResult ToSearchResult(TvdbSearchResult result, int seriesID)
    {
        ArgumentNullException.ThrowIfNull(result);

        var englishTitle = result.Translations?.GetValueOrDefault("eng");
        var englishOverview = result.Overviews?.GetValueOrDefault("eng");
        var overview = !string.IsNullOrWhiteSpace(englishOverview) ? englishOverview : result.Overview;
        var title = !string.IsNullOrWhiteSpace(englishTitle) ? englishTitle : result.Name ?? string.Empty;
        var originalTitle = string.IsNullOrWhiteSpace(result.Name) ? null : result.Name;
        return new()
        {
            ID = TvdbUtility.SeriesGuid(seriesID),
            Title = title,
            OriginalTitle = originalTitle,
            AlternateTitles =
            [
                .. (result.Aliases ?? [])
                    .Concat(result.Translations?.Values ?? Enumerable.Empty<string>())
                    .Where(name => !string.IsNullOrWhiteSpace(name) && name != title && name != originalTitle)
                    .Distinct(StringComparer.Ordinal),
            ],
            OriginalLanguageCode = TvdbUtility.ToLanguageCode(result.PrimaryLanguage),
            Overview = string.IsNullOrWhiteSpace(overview) ? null : overview,
            PosterUrl = string.IsNullOrWhiteSpace(result.ImageUrl) ? null : result.ImageUrl,
            Genres = [.. (result.Genres ?? []).Where(genre => !string.IsNullOrWhiteSpace(genre))],
            FirstAiredAt = ParseDate(result.FirstAirTime) is { } firstAired
                ? new PartialDateOnly(firstAired)
                : int.TryParse(result.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is > 0 and < 10000 ? new PartialDateOnly(year) : null,
            // TvDB's search answers series and movies alike and says nothing
            // about the kind of release beyond that, so anything reaching here
            // is television as far as this plugin can tell.
            Type = AnimeType.TV,
        };
    }

    /// <summary>
    /// The bare TvDB id of a search hit, which comes back either bare in
    /// <c>tvdb_id</c> or prefixed in <c>id</c> (<c>series-71663</c>).
    /// </summary>
    /// <param name="result">The search hit.</param>
    /// <returns>The id, or <see langword="null"/> when neither field parses.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static int? GetSeriesID(TvdbSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (int.TryParse(result.TvdbID, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bare) && bare > 0)
            return bare;

        if (result.ID is { Length: > 0 } prefixed && prefixed.LastIndexOf('-') is var dash and >= 0
            && int.TryParse(prefixed[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix) && suffix > 0)
            return suffix;

        return null;
    }

    /// <summary>
    /// Maps a show's own record into what the core's search hands back, for a
    /// show a hint names rather than a search found.
    /// </summary>
    /// <remarks>
    /// The title and overview are the English ones where the show has them,
    /// the original title its own name, and the other names its translations
    /// and aliases, as for a search hit.
    /// </remarks>
    /// <param name="series">The show, with its translations.</param>
    /// <returns>The search result, without its seasons.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static MetadataSeriesSearchResult ToSearchResult(TvdbSeriesExtended series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var names = series.Translations?.NameTranslations ?? [];
        var englishTitle = names.FirstOrDefault(name => name.IsAlias is not true && IsEnglish(name.Language) && !string.IsNullOrWhiteSpace(name.Name))?.Name?.Trim();
        var englishOverview = series.Translations?.OverviewTranslations?.FirstOrDefault(text => IsEnglish(text.Language) && !string.IsNullOrWhiteSpace(text.Overview))?.Overview;
        var originalTitle = string.IsNullOrWhiteSpace(series.Name) ? null : series.Name.Trim();
        var title = englishTitle ?? originalTitle ?? string.Empty;
        var overview = englishOverview ?? series.Overview;
        return new()
        {
            ID = TvdbUtility.SeriesGuid(series.ID),
            Title = title,
            OriginalTitle = originalTitle,
            AlternateTitles =
            [
                .. names.Select(name => name.Name)
                    .Concat((series.Aliases ?? []).Select(alias => alias.Name))
                    .Concat(series.Translations?.Aliases ?? [])
                    .OfType<string>()
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Where(name => name != title && name != originalTitle)
                    .Distinct(StringComparer.Ordinal),
            ],
            OriginalLanguageCode = TvdbUtility.ToLanguageCode(series.OriginalLanguage),
            Overview = string.IsNullOrWhiteSpace(overview) ? null : overview,
            PosterUrl = string.IsNullOrWhiteSpace(series.Image) ? null : series.Image,
            Genres = [.. (series.Genres ?? []).Select(genre => genre.Name).OfType<string>().Where(genre => !string.IsNullOrWhiteSpace(genre))],
            FirstAiredAt = ParseDate(series.FirstAired) is { } firstAired ? new PartialDateOnly(firstAired) : null,
            Type = AnimeType.TV,
        };

        static bool IsEnglish(string? language)
            => string.Equals(language?.Trim(), "eng", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a stored show into what the core's search hands back, with every
    /// season's episodes.
    /// </summary>
    /// <remarks>
    /// The title is its English title where it has one, the original title its
    /// default one, and the other names the rest of its titles.
    /// </remarks>
    /// <param name="series">The show, as the core reads it back.</param>
    /// <returns>The search result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static MetadataSeriesSearchResult ToSearchResult(ISeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var originalTitle = series.DefaultTitle.Value;
        var title = series.Titles.FirstOrDefault(name => name is { Language: TitleLanguage.English, Type: TitleType.Main or TitleType.Official })?.Value ?? originalTitle;
        return new()
        {
            ID = series.ID,
            Title = title,
            OriginalTitle = originalTitle,
            AlternateTitles = [.. series.Titles.Select(name => name.Value).Where(name => name != title && name != originalTitle).Distinct(StringComparer.Ordinal)],
            OriginalLanguageCode = series.OriginalLanguageCode,
            Overview = series.DefaultOverview?.Value,
            IsRestricted = series.Restricted,
            FirstAiredAt = series.AirDate,
            Type = series.Type,
            Seasons = ToSearchSeasons(series.Episodes),
        };
    }

    /// <summary>
    /// Groups a show's episodes, as its default episode listing answers them,
    /// into seasons for the matching engine to line up with an anime's by air
    /// date.
    /// </summary>
    /// <param name="episodes">The show's episodes in its default season type.</param>
    /// <returns>The regular seasons, in order, each with its episodes; the specials are left out.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="episodes"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<MetadataSearchResultSeason> ToSearchSeasons(IEnumerable<TvdbEpisode> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        return SearchSeasons(episodes
            .Where(episode => episode.ID > 0)
            .DistinctBy(episode => episode.ID)
            .Select(episode => (episode.SeasonNumber, episode.Number, ParseDate(episode.Aired), string.IsNullOrWhiteSpace(episode.Name) ? null : episode.Name.Trim())));
    }

    /// <summary>
    /// Groups a stored show's episodes into seasons for the matching engine to
    /// line up with an anime's by air date.
    /// </summary>
    /// <param name="episodes">The show's stored episodes.</param>
    /// <returns>The regular seasons, in order, each with its episodes; the specials are left out.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="episodes"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<MetadataSearchResultSeason> ToSearchSeasons(IEnumerable<IEpisode> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        return SearchSeasons(episodes
            .Where(episode => episode is { Type: EpisodeType.Episode, SeasonNumber: > 0 })
            .Select(episode => (episode.SeasonNumber!.Value, episode.EpisodeNumber, episode.AirDate, string.IsNullOrWhiteSpace(episode.Title) ? null : episode.Title)));
    }

    // Season zero holds TvDB's specials, which never line up with an
    // anime's regular episodes.
    private static IReadOnlyList<MetadataSearchResultSeason> SearchSeasons(IEnumerable<(int SeasonNumber, int EpisodeNumber, DateOnly? AiredAt, string? Title)> episodes)
        => [
            .. episodes
                .Where(episode => episode.SeasonNumber > 0)
                .GroupBy(episode => episode.SeasonNumber)
                .OrderBy(season => season.Key)
                .Select(season =>
                {
                    List<MetadataSearchResultEpisode> inSeason =
                    [
                        .. season
                            .OrderBy(episode => episode.EpisodeNumber)
                            .Select(episode => new MetadataSearchResultEpisode { EpisodeNumber = episode.EpisodeNumber, AiredAt = episode.AiredAt, Title = episode.Title }),
                    ];
                    return new MetadataSearchResultSeason
                    {
                        SeasonNumber = season.Key,
                        EpisodeCount = inSeason.Count,
                        FirstAiredAt = inSeason.Min(episode => episode.AiredAt) is { } began ? new PartialDateOnly(began) : null,
                        FirstEpisodeAiredAt = inSeason[0].AiredAt,
                        Episodes = inSeason,
                    };
                }),
        ];

    #endregion

    #region Helpers

    /// <summary>
    /// Parses a TvDB date, which is <c>yyyy-MM-dd</c> when present and
    /// either absent or an empty string when not.
    /// </summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The date, or <see langword="null"/>.</returns>
    public static DateOnly? ParseDate(string? value)
        => !string.IsNullOrWhiteSpace(value) && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static TitleStub Title(string value, string? languageCode, TitleType type)
        => new()
        {
            Source = MetadataSource.Tvdb,
            Language = TvdbUtility.ToTitleLanguage(languageCode),
            LanguageCode = TvdbUtility.ToLanguageCode(languageCode) ?? "unk",
            Value = value.Trim(),
            Type = type,
        };

    private static TextStub Text(string value, string? languageCode)
        => new()
        {
            Source = MetadataSource.Tvdb,
            Language = TvdbUtility.ToTitleLanguage(languageCode),
            LanguageCode = TvdbUtility.ToLanguageCode(languageCode) ?? "unk",
            Value = value.Trim(),
        };

    private static Resource Social(string name, string prefix, string id)
        => IsUrl(id)
            ? new() { Type = ResourceType.Social, Name = name, Url = id }
            : new() { Type = ResourceType.Social, Name = name, Url = prefix + Uri.EscapeDataString(id.TrimStart('@')), ID = id.TrimStart('@') };

    private static bool IsUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // A title or overview is given once per language: the first one wins,
    // which is the show's own before a translation and a translation before
    // an alias.
    private static IReadOnlyList<T> Deduplicate<T>(List<T> texts) where T : IText
        => [.. texts.DistinctBy(text => (text.LanguageCode, text.Value))];

    #endregion
}

/// <summary>
/// The people read off one show: who to store, and the show's credits.
/// </summary>
public sealed class TvdbPeople
{
    /// <summary>The people credited, by identifier, in credit order.</summary>
    public OrderedDictionary<MetadataGuid, MetadataCreatorData> Creators { get; } = [];

    /// <summary>The characters played, by identifier.</summary>
    public Dictionary<MetadataGuid, MetadataCharacterData> Characters { get; } = [];

    /// <summary>The photos and character images, by the person or character.</summary>
    public Dictionary<MetadataGuid, string> Portraits { get; } = [];

    /// <summary>The show's cast, in credit order.</summary>
    public List<MetadataCastData> Cast { get; } = [];

    /// <summary>The show's crew, in credit order.</summary>
    public List<MetadataCrewData> Crew { get; } = [];
}
