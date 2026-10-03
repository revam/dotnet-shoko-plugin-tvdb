using System;
using System.Collections.Generic;
using System.Linq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;

namespace Shoko.Plugin.Tvdb.Mapping;

/// <summary>
/// The languages a refresh keeps TvDB's translations in, as TvDB's codes,
/// best first. A <see langword="null"/> list keeps every translation the
/// show's record carries.
/// </summary>
/// <param name="SeriesTitles">The languages of the show's names.</param>
/// <param name="SeriesOverviews">The languages of the show's overviews.</param>
/// <param name="EpisodeTitles">The languages of the episodes' names.</param>
/// <param name="Overviews">The languages of the episodes' overviews and the order a person's biography is chosen in.</param>
public sealed record TvdbTextLanguages(
    IReadOnlyList<string>? SeriesTitles,
    IReadOnlyList<string>? SeriesOverviews,
    IReadOnlyList<string> EpisodeTitles,
    IReadOnlyList<string> Overviews
)
{
    /// <summary>
    /// The languages to fetch the episodes in, each once: one request per
    /// page of episodes per language.
    /// </summary>
    public IReadOnlyList<string> EpisodeLanguages
        => [.. EpisodeTitles.Concat(Overviews).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The languages the core's language order and the configuration keep for
    /// one show.
    /// </summary>
    /// <remarks>
    /// The show's own names and overviews come with its record, so the
    /// download-all switches keep every one of them, and English is always
    /// kept besides the order, as the auto-matching searches it. The episodes
    /// cost a request per page per language, so they only ever follow the order.
    /// </remarks>
    /// <param name="configuration">The plugin's configuration.</param>
    /// <param name="textManager">The core's text manager, which knows the language order.</param>
    /// <param name="originalLanguage">TvDB's code for the show's original language, which the order's main entry stands for.</param>
    /// <returns>The languages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="textManager"/> is <see langword="null"/>.</exception>
    public static TvdbTextLanguages From(TvdbConfiguration configuration, IMetadataTextManager textManager, string? originalLanguage)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(textManager);

        var overviews = TvdbUtility.ToTvdbLanguageCodes(textManager.GetLanguageOrder(TextKind.Overview), originalLanguage);
        return new(
            configuration.DownloadAllTitles ? null : WithEnglish(TvdbUtility.ToTvdbLanguageCodes(textManager.GetLanguageOrder(TextKind.Title, MetadataEntityType.Series), originalLanguage)),
            configuration.DownloadAllOverviews ? null : WithEnglish(overviews),
            TvdbUtility.ToTvdbLanguageCodes(textManager.GetLanguageOrder(TextKind.Title, MetadataEntityType.Episode), originalLanguage),
            overviews
        );
    }

    // The show's English texts cost nothing and are what the auto-matching
    // searches, so they are kept whatever the order says.
    private static IReadOnlyList<string> WithEnglish(IReadOnlyList<string> languages)
        => languages.Contains(English, StringComparer.Ordinal) ? languages : [.. languages, English];

    private const string English = "eng";
}
