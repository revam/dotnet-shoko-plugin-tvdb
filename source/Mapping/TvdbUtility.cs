using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Shoko.Abstractions.Extensions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Tvdb.Metadata;

namespace Shoko.Plugin.Tvdb.Mapping;

/// <summary>
/// The identifiers the plugin names TheTVDB's entries by, and the language
/// and country codes it hands the core.
/// </summary>
public static class TvdbUtility
{
    /// <summary>
    /// The season type TheTVDB answers for <c>default</c> when a show names
    /// none of its own.
    /// </summary>
    public const string OfficialSeasonType = "official";

    #region Identifiers

    /// <summary>
    /// Writes a TheTVDB ID the way every identifier holds it.
    /// </summary>
    /// <param name="id">The ID.</param>
    /// <returns>The ID as invariant text.</returns>
    public static string FormatID(long id)
        => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A show, as <c>tvdb://series/&lt;id&gt;</c>.</summary>
    /// <param name="seriesID">TheTVDB series ID.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid SeriesGuid(int seriesID)
        => new(TvdbSources.Tvdb, MetadataEntityType.Series, FormatID(seriesID));

    /// <summary>A season, as <c>tvdb://season/&lt;id&gt;</c>.</summary>
    /// <param name="seasonID">TheTVDB season ID.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid SeasonGuid(int seasonID)
        => new(TvdbSources.Tvdb, MetadataEntityType.Season, FormatID(seasonID));

    /// <summary>An episode, as <c>tvdb://episode/&lt;id&gt;</c>.</summary>
    /// <param name="episodeID">TheTVDB episode ID.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid EpisodeGuid(int episodeID)
        => new(TvdbSources.Tvdb, MetadataEntityType.Episode, FormatID(episodeID));

    /// <summary>A person, as <c>tvdb://creator/&lt;id&gt;</c>.</summary>
    /// <param name="peopleID">TheTVDB people ID.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid CreatorGuid(int peopleID)
        => new(TvdbSources.Tvdb, MetadataEntityType.Creator, FormatID(peopleID));

    /// <summary>A character, as <c>tvdb://character/&lt;id&gt;</c>.</summary>
    /// <param name="characterID">TheTVDB character ID.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid CharacterGuid(long characterID)
        => new(TvdbSources.Tvdb, MetadataEntityType.Character, FormatID(characterID));

    /// <summary>A tag option, as <c>tvdb://tag/&lt;id&gt;</c>.</summary>
    /// <param name="tagOptionID">TheTVDB tag option ID.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid TagGuid(int tagOptionID)
        => new(TvdbSources.Tvdb, MetadataEntityType.Tag, FormatID(tagOptionID));

    /// <summary>
    /// A genre, as <c>tvdb://tag/genre/&lt;id&gt;</c>, kept apart from the tag
    /// options, whose IDs are numbered separately, the way the core names
    /// TMDB's genres.
    /// </summary>
    /// <param name="genreID">TheTVDB genre ID.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid GenreGuid(int genreID)
        => new(TvdbSources.Tvdb, MetadataEntityType.Tag, $"genre/{FormatID(genreID)}");

    /// <summary>A studio, as <c>tvdb://studio/&lt;id&gt;</c>.</summary>
    /// <param name="companyID">TheTVDB company ID.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid StudioGuid(int companyID)
        => new(TvdbSources.Tvdb, MetadataEntityType.Studio, FormatID(companyID));

    /// <summary>A network, as <c>tvdb://network/&lt;id&gt;</c>.</summary>
    /// <param name="companyID">TheTVDB company ID.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid NetworkGuid(int companyID)
        => new(TvdbSources.Tvdb, MetadataEntityType.Network, FormatID(companyID));

    /// <summary>
    /// One of a show's season types as a global ordering, as
    /// <c>tvdb://ordering/&lt;series id&gt;-&lt;season type&gt;</c>.
    /// </summary>
    /// <param name="seriesID">TheTVDB series ID.</param>
    /// <param name="seasonType">The season type, e.g. <c>dvd</c>.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid OrderingGuid(int seriesID, string seasonType)
        => new(TvdbSources.Tvdb, MetadataEntityType.Ordering, $"{FormatID(seriesID)}-{seasonType}");

    /// <summary>
    /// One season of a season type, as
    /// <c>tvdb://season/&lt;series id&gt;-&lt;season type&gt;-&lt;number&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The groups of an ordering and a season TheTVDB lists no record for are
    /// named this way. It never looks like TheTVDB's own season IDs, which are
    /// plain numbers, so a group can never take a stored season's ID.
    /// </remarks>
    /// <param name="seriesID">TheTVDB series ID.</param>
    /// <param name="seasonType">The season type, e.g. <c>dvd</c>.</param>
    /// <param name="seasonNumber">The season's number in that season type.</param>
    /// <returns>The identifier.</returns>
    public static MetadataGuid SeasonTypeSeasonGuid(int seriesID, string seasonType, int seasonNumber)
        => new(TvdbSources.Tvdb, MetadataEntityType.Season, $"{FormatID(seriesID)}-{seasonType}-{FormatID(seasonNumber)}");

    /// <summary>
    /// Reads TheTVDB's own numeric ID out of one of the plugin's identifiers.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <param name="entityType">The kind it has to be.</param>
    /// <param name="tvdbID">The ID, when it is one.</param>
    /// <returns>
    /// <see langword="true"/> for a positive numeric ID on the TheTVDB source
    /// and of that kind.
    /// </returns>
    public static bool TryGetID([NotNullWhen(true)] MetadataGuid? id, MetadataEntityType entityType, out int tvdbID)
    {
        tvdbID = 0;
        return id is not null
            && id.Source == TvdbSources.Tvdb
            && id.EntityType == entityType
            && id.TryGetNumericID(out tvdbID)
            && tvdbID > 0;
    }

    #endregion

    #region Languages & Countries

    /// <summary>
    /// The code the core keeps a TheTVDB language under: the two-letter code
    /// where there is one, since TheTVDB writes three.
    /// </summary>
    /// <remarks>
    /// Two of TheTVDB's codes are its own: <c>pt</c> is Brazilian Portuguese,
    /// the Portuguese of Portugal being <c>por</c>, and <c>zhtw</c> is the
    /// Chinese of Taiwan, kept as traditional Chinese.
    /// </remarks>
    /// <param name="code">TheTVDB's language code.</param>
    /// <returns>
    /// The two-letter code, or a regional one such as <c>pt-BR</c>, TheTVDB's
    /// own code lower-cased when the language has neither, or
    /// <see langword="null"/> for none.
    /// </returns>
    public static string? ToLanguageCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var trimmed = ToIetfTag(code);
        return trimmed.TryGetTitleLanguage(out var language) && language.GetString() is { } known and not "unk"
            ? known
            : trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// The title language a TheTVDB language code stands for.
    /// </summary>
    /// <param name="code">TheTVDB's language code.</param>
    /// <returns>The language, or <see cref="TitleLanguage.Unknown"/>.</returns>
    public static TitleLanguage ToTitleLanguage(string? code)
        => !string.IsNullOrWhiteSpace(code) && ToIetfTag(code).TryGetTitleLanguage(out var language) ? language : TitleLanguage.Unknown;

    /// <summary>
    /// The ISO 639-1 code of a TheTVDB language, for an image.
    /// </summary>
    /// <param name="code">TheTVDB's language code.</param>
    /// <returns>The two-letter code, or <see langword="null"/> for none or one without a two-letter code.</returns>
    public static string? ToImageLanguageCode(string? code)
        => ToLanguageCode(code)?.Split('-')[0] is { Length: 2 } twoLetter ? twoLetter : null;

    // TheTVDB's own codes as the tags the core reads, and the rest trimmed.
    private static string ToIetfTag(string code)
        => code.Trim().ToLowerInvariant() switch
        {
            "pt" => "pt-BR",
            "zhtw" => "zh-Hant",
            _ => code.Trim(),
        };

    /// <summary>
    /// The ISO 3166-1 alpha-2 code of a TheTVDB country, which TheTVDB writes
    /// as three letters.
    /// </summary>
    /// <param name="code">TheTVDB's country code, e.g. <c>usa</c>.</param>
    /// <returns>The upper-case two-letter code, e.g. <c>US</c>, or <see langword="null"/> when it is not a country.</returns>
    public static string? ToCountryCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var trimmed = code.Trim();
        if (trimmed.Length is 2 && trimmed.All(char.IsAsciiLetter))
            return trimmed.ToUpperInvariant();

        if (trimmed.Length is not 3)
            return null;

        return Countries.Value.TryGetValue(trimmed, out var twoLetter) ? twoLetter : null;
    }

    /// <summary>
    /// Every country .NET knows, by its three-letter code, read from the
    /// runtime rather than written down here.
    /// </summary>
    private static readonly Lazy<System.Collections.Generic.Dictionary<string, string>> Countries = new(() =>
    {
        var countries = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (region.TwoLetterISORegionName.Length is 2 && region.ThreeLetterISORegionName.Length is 3)
                    countries.TryAdd(region.ThreeLetterISORegionName, region.TwoLetterISORegionName.ToUpperInvariant());
            }
            catch (ArgumentException)
            {
            }
        }

        return countries;
    });

    #endregion
}
