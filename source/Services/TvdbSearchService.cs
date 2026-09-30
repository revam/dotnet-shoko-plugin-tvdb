using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Storage;

namespace Shoko.Plugin.Tvdb.Services;

/// <summary>
/// Searches TheTVDB, for a person looking for a show to link and for the
/// auto-linker looking for the best one.
/// </summary>
/// <param name="apiClient">The TheTVDB client.</param>
/// <param name="store">The plugin's store, for the shows already stored.</param>
/// <param name="matchingEngine">The core's matching engine, which judges the shows found.</param>
/// <param name="linkingService">The core's linking service, for the shows the anime's other links name.</param>
/// <param name="configurationProvider">The plugin's configuration.</param>
/// <param name="logger">The logger.</param>
public sealed class TvdbSearchService(
    TvdbApiClient apiClient,
    TvdbStore store,
    IMetadataMatchingEngine matchingEngine,
    IMetadataLinkingService linkingService,
    ConfigurationProvider<TvdbConfiguration> configurationProvider,
    ILogger<TvdbSearchService> logger
)
{
    /// <summary>
    /// How many of the shows the searches for one anime rate anything, over
    /// every title searched, have their episodes fetched to line them up with
    /// the anime's by air date, a call each (more for a show of over 500
    /// episodes). A stored show is sent whole and does not count.
    /// </summary>
    internal const int AlignedCandidateCount = 3;

    #region Search

    /// <summary>
    /// Searches TheTVDB for shows a user might link to.
    /// </summary>
    /// <remarks>
    /// TheTVDB's search is across every entity type and answers one flat list
    /// with no total, so the paging asked for is applied here over what came
    /// back rather than sent along.
    /// </remarks>
    /// <param name="options">What to search for.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The page asked for, and how many hits there were in total.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="TvdbApiException">TheTVDB answered with something unexpected.</exception>
    public async Task<(IReadOnlyList<MetadataSeriesSearchResult> Page, int TotalCount)> Search(MetadataSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!apiClient.HasApiKey || string.IsNullOrWhiteSpace(options.Query))
            return ([], 0);

        var page = Math.Max(options.Page, 1);
        var pageSize = Math.Max(options.PageSize, 1);
        var results = await apiClient.SearchSeries(options.Query, page * pageSize, cancellationToken).ConfigureAwait(false);
        var hits = results
            .Select(result => (Result: result, ID: TvdbEntityMapper.GetSeriesID(result)))
            .Where(hit => hit.ID is not null)
            .Where(hit => options.Year is not { } year || (int.TryParse(hit.Result.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hitYear) && hitYear == year))
            .ToList();

        return
        (
            [.. hits.Skip((page - 1) * pageSize).Take(pageSize).Select(hit => TvdbEntityMapper.ToSearchResult(hit.Result, hit.ID!.Value))],
            hits.Count
        );
    }

    #endregion

    #region Auto-Linking

    /// <summary>
    /// Works out which TheTVDB show an anime is: searches for it, judges every
    /// show found through the core's matching engine, and judges the shows
    /// the anime's links on other sources name as hints.
    /// </summary>
    /// <remarks>
    /// <para>
    ///   Up to three of the anime's titles are searched, one after another,
    ///   stopping once a show matches on both title and date, which nothing
    ///   can outrank. Every show found is judged against all of the anime's
    ///   titles rather than the one that found it, TheTVDB keeping every
    ///   season of a show in one entry.
    /// </para>
    /// <para>
    ///   A stored show is judged with every season's episodes, and the first
    ///   <see cref="AlignedCandidateCount"/> others rated anything with the
    ///   episodes TheTVDB lists for them, each fetched once per anime, so the
    ///   engine can line their air dates up with the anime's.
    /// </para>
    /// <para>
    ///   The show taken is turned down unless the evidence is strong enough
    ///   for a search across every kind of show; see <see cref="Refuse"/>.
    /// </para>
    /// </remarks>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// Every show the search found, best first, the first taken unless it
    /// says why not, then the hints, the one to take first leading. Empty
    /// without an API key, or when nothing was found.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="anime"/> is <see langword="null"/>.</exception>
    /// <exception cref="TvdbApiException">TheTVDB answered with something unexpected.</exception>
    public async Task<IReadOnlyList<MetadataAutoLinkCandidate>> FindAutoLinks(IAnidbAnime anime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anime);

        if (!apiClient.HasApiKey)
            return [];

        var episodes = new EpisodeLists(apiClient);
        var hints = await FindHints(anime, cancellationToken).ConfigureAwait(false);
        var searched = await SearchFor(anime, hints, episodes, cancellationToken).ConfigureAwait(false);
        var hinted = await JudgeHints(anime, hints, searched, episodes, cancellationToken).ConfigureAwait(false);
        return
        [
            .. searched.Select(found => new MetadataAutoLinkCandidate
            {
                Result = found.Match.Candidate,
                AnidbAnimeID = anime.AnidbID,
                MatchRating = found.Match.Rating,
                IsRemote = true,
                Rejection = found.Match.Rejection is MatchRejectionReason.None
                    ? null
                    : new() { Reason = found.Match.Rejection, Details = Sentences($"Searched for \"{found.Query}\".", found.Refusal, found.Match.Details) },
            }),
            .. hinted,
        ];
    }

    /// <summary>
    /// Searches TheTVDB by the anime's titles and has the engine judge every
    /// show found.
    /// </summary>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="hints">The shows the anime's other links name.</param>
    /// <param name="episodes">The episode lists fetched for the anime so far.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Every show found, best first, with the title that found it and why this plugin turned it down, if it did.</returns>
    private async Task<IReadOnlyList<Found>> SearchFor(IAnidbAnime anime, IReadOnlyList<TvdbHint> hints, EpisodeLists episodes, CancellationToken cancellationToken)
    {
        var queries = anime.Titles
            .Select(title => title.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
        var limit = configurationProvider.Load().SearchResultLimit;
        IReadOnlyCollection<MetadataGuid> hintedIDs = [.. hints.Select(hint => hint.ID)];
        var options = new SeriesMatchOptions { IncludeRestricted = anime.Restricted, HintedIDs = hintedIDs };
        var candidates = new List<MetadataSeriesSearchResult>();
        var foundBy = new Dictionary<MetadataGuid, string>();
        IReadOnlyList<(SeriesMatch Match, string? Refusal)> ranked = [];
        foreach (var query in queries)
        {
            var results = await apiClient.SearchSeries(query, limit, cancellationToken).ConfigureAwait(false);
            var found = candidates.Count;
            foreach (var result in results)
            {
                if (TvdbEntityMapper.GetSeriesID(result) is not { } id || !foundBy.TryAdd(TvdbUtility.SeriesGuid(id), query))
                    continue;

                var candidate = TvdbEntityMapper.ToSearchResult(result, id);
                candidates.Add(store.GetSeries(id) is { } stored ? candidate with { Seasons = TvdbEntityMapper.ToSearchSeasons(stored.Episodes) } : candidate);
            }

            // Nothing new found leaves the judgement as it was.
            if (candidates.Count == found)
                continue;

            var matches = matchingEngine.MatchSeries(anime, candidates, options);
            if (await LineUp(matches, candidates, episodes, cancellationToken).ConfigureAwait(false))
                matches = matchingEngine.MatchSeries(anime, candidates, options);

            ranked = Refuse(matches, hintedIDs);
            if (ranked[0].Match is { Rejection: MatchRejectionReason.None, Rating: MatchRating.DateAndTitleMatches })
                break;
        }

        return [.. ranked.Select(pair => new Found(pair.Match, foundBy[pair.Match.Candidate.ID], pair.Refusal))];
    }

    /// <summary>
    /// Gives the first few shows rated anything, in the engine's order, the
    /// episodes TheTVDB lists for them, up to <see cref="AlignedCandidateCount"/>
    /// per anime.
    /// </summary>
    /// <param name="matches">The engine's judgement, best first.</param>
    /// <param name="candidates">The shows found, updated in place.</param>
    /// <param name="episodes">The episode lists fetched for the anime so far.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Whether any show was given its episodes.</returns>
    private static async Task<bool> LineUp(IReadOnlyList<SeriesMatch> matches, List<MetadataSeriesSearchResult> candidates, EpisodeLists episodes, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var match in matches)
        {
            if (match.Rating is MatchRating.None || HasEpisodes(match.Candidate) || !TvdbUtility.TryGetID(match.Candidate.ID, MetadataEntityType.Series, out var seriesID))
                continue;

            if (!episodes.WasFetched(seriesID) && episodes.FetchedCount >= AlignedCandidateCount)
                break;

            if (await episodes.Get(seriesID, cancellationToken).ConfigureAwait(false) is not { Count: > 0 } seasons)
                continue;

            var index = candidates.FindIndex(candidate => candidate.ID == match.Candidate.ID);
            candidates[index] = candidates[index] with { Seasons = seasons };
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Turns the show the engine took down unless the evidence is strong
    /// enough, and the shows it outranked with it unless theirs is.
    /// </summary>
    /// <remarks>
    /// <para>
    ///   TheTVDB's search is across every kind of show, not only animation, so
    ///   a name that only comes close, or a show that only started the same
    ///   year, is found by chance far more often than it is right: 16 of 19
    ///   such picks on a sample were wrong. A show is taken when:
    /// </para>
    /// <list type="bullet">
    ///   <item>one of its names matches one of the anime's titles
    ///   (<see cref="MatchRating.DateAndTitleMatches"/> or
    ///   <see cref="MatchRating.TitleMatches"/>);</item>
    ///   <item>its names come close (<see cref="MatchRating.DateAndTitleKindaMatches"/>
    ///   or <see cref="MatchRating.TitleKindaMatches"/>) and its episodes' air
    ///   dates line up conclusively with the anime's
    ///   (<see cref="EpisodeAlignment.IsConclusive"/>), which a show found by
    ///   chance does not do;</item>
    ///   <item>or the anime's links on other sources name it.</item>
    /// </list>
    /// <para>
    ///   A show matched on its dates alone is turned down even when they line
    ///   up: two weekly shows aired over the same weeks line up just as well.
    ///   When the pick is turned down, the shows it outranked lose on their
    ///   names too, unless they would have been taken, not to a show nothing
    ///   links; those the engine filtered out keep why.
    /// </para>
    /// </remarks>
    /// <param name="ranked">The engine's judgement, best first.</param>
    /// <param name="hintedIDs">The shows the anime's other links name.</param>
    /// <returns>Every show, best first, with why this plugin turned it down, if it did.</returns>
    internal static IReadOnlyList<(SeriesMatch Match, string? Refusal)> Refuse(IReadOnlyList<SeriesMatch> ranked, IReadOnlyCollection<MetadataGuid> hintedIDs)
    {
        if (ranked is not [{ Rejection: MatchRejectionReason.None } taken, ..] || Takeable(taken, hintedIDs))
            return [.. ranked.Select(match => (match, (string?)null))];

        return
        [
            (taken with { Rejection = MatchRejectionReason.TitleMismatch }, Refusal(taken)),
            .. ranked.Skip(1).Select(match => !Takeable(match, hintedIDs) && match.Rejection is MatchRejectionReason.Outranked or MatchRejectionReason.EpisodeCountMismatch or MatchRejectionReason.DateMismatch
                ? (match with { Rejection = MatchRejectionReason.TitleMismatch }, Refusal(match))
                : (match, (string?)null)),
        ];

        static string Refusal(SeriesMatch match)
            => $"Rated {match.Rating}{(match.EpisodeAlignment is { IsConclusive: true } ? ", its episodes lining up with the anime's by air date" : string.Empty)}. " +
                "TheTVDB's search spans every kind of show, so a show is taken only when one of its names matches one of the anime's titles, " +
                "when its names come close and its episodes' air dates line up conclusively with the anime's, or when the anime's links on other sources name it.";
    }

    /// <summary>
    /// Whether a show the engine judged has what it takes to be linked from a
    /// search across every kind of show.
    /// </summary>
    /// <param name="match">The engine's judgement of the show.</param>
    /// <param name="hintedIDs">The shows the anime's other links name.</param>
    /// <returns><see langword="true"/> when it may be taken.</returns>
    private static bool Takeable(SeriesMatch match, IReadOnlyCollection<MetadataGuid> hintedIDs)
        => match.Rating switch
        {
            MatchRating.DateAndTitleMatches or MatchRating.TitleMatches => true,
            MatchRating.DateAndTitleKindaMatches or MatchRating.TitleKindaMatches when match.EpisodeAlignment is { IsConclusive: true } => true,
            _ => hintedIDs.Contains(match.Candidate.ID),
        };

    #endregion

    #region Hints

    /// <summary>
    /// The TheTVDB shows the anime's links on other sources name, such as a
    /// linked TMDB show's TheTVDB ID.
    /// </summary>
    /// <remarks>
    /// An episode the core could not place in a stored show is looked up on
    /// TheTVDB for its show. An entry of another kind, a film, is left out:
    /// the plugin does not read TheTVDB's films.
    /// </remarks>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Each show once, in the core's order, with every entry naming it.</returns>
    /// <exception cref="TvdbApiException">TheTVDB failed looking an episode up.</exception>
    private async Task<IReadOnlyList<TvdbHint>> FindHints(IAnidbAnime anime, CancellationToken cancellationToken)
    {
        var found = new List<TvdbHint>();
        foreach (var hint in linkingService.GetCrossSourceHints(TvdbSources.Tvdb, anime.AnidbID))
        {
            if (hint.ID.Source != TvdbSources.Tvdb)
                continue;

            int seriesID;
            if (TvdbUtility.TryGetID(hint.ID, MetadataEntityType.Series, out var namedSeriesID))
            {
                seriesID = namedSeriesID;
            }
            else if (TvdbUtility.TryGetID(hint.ID, MetadataEntityType.Episode, out var episodeID))
            {
                if (await apiClient.GetEpisode(episodeID, cancellationToken).ConfigureAwait(false) is not { SeriesID: > 0 } episode)
                {
                    logger.LogDebug("The links of AniDB anime {AnimeID} name TheTVDB episode {EpisodeID}, which TheTVDB does not know.", anime.AnidbID, episodeID);
                    continue;
                }

                seriesID = episode.SeriesID;
            }
            else
            {
                logger.LogDebug("The links of AniDB anime {AnimeID} name {EntryID}, which the plugin does not link.", anime.AnidbID, hint.ID);
                continue;
            }

            var index = found.FindIndex(existing => existing.SeriesID == seriesID);
            if (index < 0)
                found.Add(new(seriesID, hint.NamedBy));
            else
                found[index] = found[index] with { NamedBy = [.. found[index].NamedBy.Concat(hint.NamedBy).Distinct()] };
        }

        return found;
    }

    /// <summary>
    /// Judges each show the anime's links on other sources name, the one to
    /// take first leading.
    /// </summary>
    /// <remarks>
    /// A show the search found is judged as found; any other is read from the
    /// store with every season's episodes, or fetched from TheTVDB with its
    /// episodes when the engine rates it anything. One that cannot be had is
    /// left out. A hint is turned down only for being restricted or of a
    /// kind the engine refuses; whether the search took something it competes
    /// with is the core's to judge. One that may be taken keeps the engine's
    /// rating, or <see cref="MatchRating.FirstAvailable"/> when nothing
    /// agreed; those come first, the best rated leading, then in the order
    /// they were named.
    /// </remarks>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="hints">The shows the anime's other links name.</param>
    /// <param name="searched">What the search judged.</param>
    /// <param name="episodes">The episode lists fetched for the anime so far.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>One candidate per show that could be had, as <see cref="MetadataAutoLinkOrigin.CrossSourceLink"/>.</returns>
    /// <exception cref="TvdbApiException">TheTVDB answered with something unexpected.</exception>
    private async Task<IReadOnlyList<MetadataAutoLinkCandidate>> JudgeHints(
        IAnidbAnime anime,
        IReadOnlyList<TvdbHint> hints,
        IReadOnlyList<Found> searched,
        EpisodeLists episodes,
        CancellationToken cancellationToken
    )
    {
        var results = new List<(MetadataAutoLinkCandidate Candidate, int Order)>();
        foreach (var hint in hints)
        {
            var options = new SeriesMatchOptions { IncludeRestricted = anime.Restricted, HintedIDs = [hint.ID] };
            MetadataSeriesSearchResult candidate;
            bool isLocal;
            if (searched.FirstOrDefault(found => found.Match.Candidate.ID == hint.ID) is { } found)
                (candidate, isLocal) = (found.Match.Candidate, false);
            else if (store.GetSeries(hint.SeriesID) is { } stored)
                (candidate, isLocal) = (TvdbEntityMapper.ToSearchResult(stored), true);
            else if (await apiClient.GetSeries(hint.SeriesID, cancellationToken).ConfigureAwait(false) is { } remote)
                (candidate, isLocal) = (TvdbEntityMapper.ToSearchResult(remote), false);
            else
            {
                logger.LogDebug("{Hint} for AniDB anime {AnimeID}, which TheTVDB does not know.", hint.Source, anime.AnidbID);
                continue;
            }

            var judged = matchingEngine.MatchSeries(anime, [candidate], options)[0];
            if (judged.Rating is not MatchRating.None && !HasEpisodes(candidate) &&
                await episodes.Get(hint.SeriesID, cancellationToken).ConfigureAwait(false) is { Count: > 0 } seasons)
            {
                candidate = candidate with { Seasons = seasons };
                judged = matchingEngine.MatchSeries(anime, [candidate], options)[0];
            }

            MetadataAutoLinkRejection? rejection = judged.Rejection is MatchRejectionReason.Restricted or MatchRejectionReason.TypeMismatch
                ? new() { Reason = judged.Rejection, Details = Sentences($"{hint.Source}.", judged.Details) }
                : null;
            var rating = rejection is null && judged.Rating is MatchRating.None ? MatchRating.FirstAvailable : judged.Rating;
            if (rejection is null)
                logger.LogDebug("{Hint} for AniDB anime {AnimeID}, which may be taken, rated {Rating}.", hint.Source, anime.AnidbID, rating);

            results.Add((new()
            {
                Result = candidate,
                AnidbAnimeID = anime.AnidbID,
                MatchRating = rating,
                Origin = MetadataAutoLinkOrigin.CrossSourceLink,
                IsLocal = isLocal,
                IsRemote = !isLocal,
                Rejection = rejection,
            }, results.Count));
        }

        return
        [
            .. results
                .OrderBy(result => result.Candidate.Rejection is null ? 0 : 1)
                .ThenBy(result => result.Candidate.Rejection is null ? Priority(result.Candidate.MatchRating) : 0)
                .ThenBy(result => result.Order)
                .Select(result => result.Candidate),
        ];
    }

    /// <summary>
    /// Where a rating puts a hint among the others that may be taken, the
    /// most trusted first, as the core's TMDB provider orders its own.
    /// </summary>
    /// <param name="rating">The rating.</param>
    /// <returns>The place, lower first.</returns>
    internal static int Priority(MatchRating rating) => rating switch
    {
        MatchRating.UserVerified => 0,
        MatchRating.DateAndTitleMatches => 1,
        MatchRating.TitleMatches => 2,
        MatchRating.DateAndTitleKindaMatches => 3,
        MatchRating.DateMatches => 4,
        MatchRating.TitleKindaMatches => 5,
        _ => 6,
    };

    #endregion

    #region Helpers

    /// <summary>
    /// Whether a show is sent with the episodes of any of its seasons.
    /// </summary>
    /// <param name="candidate">The show.</param>
    /// <returns><see langword="true"/> when it has them.</returns>
    private static bool HasEpisodes(MetadataSeriesSearchResult candidate)
        => candidate.Seasons?.Any(season => season.Episodes is not null) is true;

    /// <summary>
    /// Joins the sentences given, leaving out the empty ones.
    /// </summary>
    /// <param name="sentences">The sentences.</param>
    /// <returns>The text.</returns>
    private static string Sentences(params string?[] sentences)
        => string.Join(' ', sentences.Where(sentence => !string.IsNullOrWhiteSpace(sentence)));

    /// <summary>
    /// A show the search found, as the engine and this plugin judged it.
    /// </summary>
    /// <param name="Match">The engine's judgement, with this plugin's rejection when it turned the show down.</param>
    /// <param name="Query">The title that found it.</param>
    /// <param name="Refusal">Why this plugin turned it down, or <see langword="null"/> when it did not.</param>
    private sealed record Found(SeriesMatch Match, string Query, string? Refusal);

    /// <summary>
    /// A TheTVDB show the anime's links on other sources name.
    /// </summary>
    /// <param name="SeriesID">TheTVDB series ID.</param>
    /// <param name="NamedBy">The linked entries of other sources naming it, or an episode of it.</param>
    private sealed record TvdbHint(int SeriesID, IReadOnlyList<MetadataGuid> NamedBy)
    {
        /// <summary>
        /// The show's identity.
        /// </summary>
        public MetadataGuid ID => TvdbUtility.SeriesGuid(SeriesID);

        /// <summary>
        /// Where the hint came from, as a clause.
        /// </summary>
        public string Source => NamedBy is { Count: > 0 }
            ? $"{NamedBy[0]}{(NamedBy.Count > 1 ? $" and {NamedBy.Count - 1} more" : string.Empty)}, linked to the anime, {(NamedBy.Count > 1 ? "name" : "names")} TheTVDB series {SeriesID}"
            : $"The anime's links name TheTVDB series {SeriesID}";
    }

    /// <summary>
    /// The episodes TheTVDB lists for the shows judged for one anime, fetched
    /// once each and grouped into seasons.
    /// </summary>
    /// <param name="apiClient">The TheTVDB client.</param>
    private sealed class EpisodeLists(TvdbApiClient apiClient)
    {
        private readonly Dictionary<int, IReadOnlyList<MetadataSearchResultSeason>> _fetched = [];

        /// <summary>
        /// How many shows have had their episodes fetched.
        /// </summary>
        public int FetchedCount => _fetched.Count;

        /// <summary>
        /// Whether a show's episodes were fetched already.
        /// </summary>
        /// <param name="seriesID">TheTVDB series ID.</param>
        /// <returns><see langword="true"/> when they were.</returns>
        public bool WasFetched(int seriesID)
            => _fetched.ContainsKey(seriesID);

        /// <summary>
        /// A show's regular seasons with their episodes, in its default season
        /// type, fetched the first time only.
        /// </summary>
        /// <param name="seriesID">TheTVDB series ID.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>The seasons; empty when TheTVDB lists none.</returns>
        /// <exception cref="TvdbApiException">TheTVDB answered with something unexpected.</exception>
        public async Task<IReadOnlyList<MetadataSearchResultSeason>> Get(int seriesID, CancellationToken cancellationToken)
        {
            if (_fetched.TryGetValue(seriesID, out var seasons))
                return seasons;

            var episodes = await apiClient.GetEpisodes(seriesID, "default", cancellationToken).ConfigureAwait(false);
            return _fetched[seriesID] = TvdbEntityMapper.ToSearchSeasons(episodes);
        }
    }

    #endregion
}
