using System;
using System.Collections.Generic;
using System.Linq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Text.Options;

namespace Shoko.Plugin.Tvdb.Mapping;

/// <summary>
/// The languages a refresh keeps TvDB's translations in, as TvDB's codes,
/// best first. A <see langword="null"/> list keeps every translation the
/// record carries.
/// </summary>
/// <param name="SeriesTitles">The languages of the show's names.</param>
/// <param name="SeriesOverviews">The languages of the show's overviews.</param>
/// <param name="EpisodeTitles">The languages of the episodes' names.</param>
/// <param name="Overviews">The languages of the episodes' overviews.</param>
/// <param name="PersonTitles">The languages of a person's names besides their own: the ones the core names people by.</param>
/// <param name="PersonOverviews">The languages of a person's biographies, besides the first one.</param>
/// <param name="PersonOverviewOrder">The order a person's first biography is chosen in.</param>
public sealed record TvdbTextLanguages(
    IReadOnlyList<string>? SeriesTitles,
    IReadOnlyList<string>? SeriesOverviews,
    IReadOnlyList<string> EpisodeTitles,
    IReadOnlyList<string> Overviews,
    IReadOnlyList<string>? PersonTitles,
    IReadOnlyList<string>? PersonOverviews,
    IReadOnlyList<string> PersonOverviewOrder
)
{
    /// <summary>
    /// The languages to fetch the episodes in, each once: one request per
    /// page of episodes per language.
    /// </summary>
    public IReadOnlyList<string> EpisodeLanguages
        => [.. EpisodeTitles.Concat(Overviews).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The languages the core's title and overview selections and the
    /// configuration keep for one show.
    /// </summary>
    /// <remarks>
    /// The show's and its people's names and overviews come with their
    /// records, so the download-all switches keep every one of them, and
    /// English is always kept for the show besides the selection, as the
    /// auto-matching searches it. The episodes cost a request per page per
    /// language, so they only ever follow the selection. A person's names
    /// follow the selection the core names creators by, and their biographies
    /// the overview selection, each with its main step left out, as a person
    /// is written the same with or without a show and their own language is
    /// not the show's. A season keeps only the name its record gives, so it
    /// needs none.
    /// </remarks>
    /// <param name="configuration">The plugin's configuration.</param>
    /// <param name="textManager">The core's text manager, which makes the selections.</param>
    /// <param name="originalLanguage">TvDB's code for the show's original language, which a selection's main step stands for, except in a person's languages.</param>
    /// <returns>The languages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="textManager"/> is <see langword="null"/>.</exception>
    public static TvdbTextLanguages From(TvdbConfiguration configuration, IMetadataTextManager textManager, string? originalLanguage)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(textManager);

        var seriesTitles = WithEnglish(Codes(textManager.GetTitleSelection(MetadataEntityType.Series), originalLanguage));
        var overviewSelection = textManager.GetOverviewSelection();
        var overviews = Codes(overviewSelection, originalLanguage);
        var personOverviews = Codes(overviewSelection, null);
        return new(
            configuration.DownloadAllTitles ? null : seriesTitles,
            configuration.DownloadAllOverviews ? null : WithEnglish(overviews),
            Codes(textManager.GetTitleSelection(MetadataEntityType.Episode), originalLanguage),
            overviews,
            configuration.DownloadAllTitles ? null : Codes(textManager.GetTitleSelection(MetadataEntityType.Creator), null),
            configuration.DownloadAllOverviews ? null : personOverviews,
            personOverviews
        );
    }

    // The languages a selection's steps name, in order, each once.
    private static IReadOnlyList<string> Codes(TitleSelection selection, string? originalLanguage)
        => TvdbUtility.ToTvdbLanguageCodes(selection.Steps.Select(step => step.Language), originalLanguage);

    private static IReadOnlyList<string> Codes(OverviewSelection selection, string? originalLanguage)
        => TvdbUtility.ToTvdbLanguageCodes(selection.Steps.Select(step => step.Language), originalLanguage);

    // The show's English texts cost nothing and are what the auto-matching
    // searches, so they are kept whatever the selection says.
    private static IReadOnlyList<string> WithEnglish(IReadOnlyList<string> languages)
        => languages.Contains(English, StringComparer.Ordinal) ? languages : [.. languages, English];

    private const string English = "eng";
}
