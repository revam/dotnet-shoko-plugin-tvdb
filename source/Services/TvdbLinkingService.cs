using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Storage;

namespace Shoko.Plugin.Tvdb.Services;

/// <summary>
/// Manages the links between AniDB and TvDB. The links are the core's,
/// kept in its cross-reference store and written through its
/// <see cref="IMetadataLinkingService"/>; this decides what to write.
/// </summary>
/// <remarks>
/// Episode matching is the core's <see cref="IMetadataMatchingEngine"/>, run
/// with the date-and-title-within-seasons strategy a source with seasons and
/// air dates calls for, over the episodes the core's series store holds for
/// the show.
/// </remarks>
/// <param name="store">The plugin's store.</param>
/// <param name="crossReferences">The core's store of links.</param>
/// <param name="linkingService">The core's linking service.</param>
/// <param name="matchingEngine">The core's episode matcher.</param>
/// <param name="metadataService">The core's metadata service, for a season or group to match within.</param>
/// <param name="configurationProvider">The plugin's configuration.</param>
/// <param name="logger">The logger.</param>
public sealed class TvdbLinkingService(
    TvdbStore store,
    IMetadataCrossReferenceStore crossReferences,
    IMetadataLinkingService linkingService,
    IMetadataMatchingEngine matchingEngine,
    IMetadataService metadataService,
    ConfigurationProvider<TvdbConfiguration> configurationProvider,
    ILogger<TvdbLinkingService> logger
)
{
    #region Reading

    /// <summary>
    /// The TvDB shows an AniDB anime is linked to.
    /// </summary>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <returns>TvDB series IDs, each once, in link order.</returns>
    public IReadOnlyList<int> GetLinkedSeriesIDs(int anidbAnimeID)
        => anidbAnimeID <= 0
            ? []
            : [.. crossReferences.GetSeriesLinks(anidbAnimeID, MetadataSource.Tvdb)
                .Select(link => TvdbUtility.TryGetID(link.ProviderID, MetadataEntityType.Series, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()];

    /// <summary>
    /// The AniDB anime linked to a TvDB show as a whole.
    /// </summary>
    /// <param name="seriesID">TvDB series ID.</param>
    /// <returns>The AniDB anime IDs, each once.</returns>
    public IReadOnlyList<int> GetLinkedAnidbAnimeIDs(int seriesID)
        => seriesID <= 0
            ? []
            : [.. crossReferences.GetLinksTo(TvdbUtility.SeriesGuid(seriesID))
                .OfType<IMetadataSeriesCrossReference>()
                .Select(link => link.AnidbAnimeID)
                .Where(id => id > 0)
                .Distinct()];

    #endregion

    #region Series Links

    /// <summary>
    /// Removes every TvDB link an AniDB anime has, at every level, and
    /// stops the anime being linked automatically again.
    /// </summary>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <param name="purge">Whether to purge each show once nothing links to it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many links were removed.</returns>
    public Task<int> RemoveAllLinks(int anidbAnimeID, bool purge = false, CancellationToken cancellationToken = default)
        => linkingService.RemoveLinksForAnime(MetadataSource.Tvdb, anidbAnimeID, purge: purge, disableAutoLinking: true, cancellationToken: cancellationToken);

    #endregion

    #region Matching

    /// <summary>
    /// Works out which TvDB episodes an AniDB anime's episodes line up
    /// with, without writing anything.
    /// </summary>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="anidbEpisodes">The episodes to match, already narrowed to the ones in scope.</param>
    /// <param name="seriesID">TvDB series ID to match into.</param>
    /// <param name="seasonID">
    /// One season of the show, or one group of an ordering of it, to match
    /// within; <see langword="null"/> for the whole show.
    /// </param>
    /// <param name="existing">The links to honour, or <see langword="null"/> to match everything afresh.</param>
    /// <param name="considerOtherLinks">
    /// Whether to leave out TvDB episodes another AniDB anime already
    /// claims; <see langword="null"/> follows the settings.
    /// </param>
    /// <returns>
    /// One match per AniDB episode, including the ones nothing matched, but
    /// for an episode an existing link settles outside the candidates (a
    /// person's link to nothing, or a link to an episode not among them),
    /// which is left out so that saving leaves its link alone. Nothing when
    /// the show is not stored or the season is not the show's.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="anime"/> or <paramref name="anidbEpisodes"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<EpisodeMatch> Match(
        IAnidbAnime anime,
        IReadOnlyList<IAnidbEpisode> anidbEpisodes,
        int seriesID,
        MetadataGuid? seasonID = null,
        IReadOnlyList<IMetadataEpisodeCrossReference>? existing = null,
        bool? considerOtherLinks = null
    )
    {
        ArgumentNullException.ThrowIfNull(anime);
        ArgumentNullException.ThrowIfNull(anidbEpisodes);

        if (store.GetSeries(seriesID) is not { } series)
            return [];

        IReadOnlyList<IEpisode> candidates = series.Episodes;
        if (seasonID is not null)
        {
            // A season of the show or a group of one of its orderings; the
            // core reads both back by the same kind of identifier.
            if (seasonID.Source != MetadataSource.Tvdb || metadataService.GetSeason(seasonID) is not { } season || season.SeriesID != series.ID)
                return [];

            candidates = season.Episodes;
        }

        if (considerOtherLinks ?? configurationProvider.Load().ConsiderExistingOtherLinks)
        {
            var claimed = crossReferences.GetEpisodeLinksInto(series.ID)
                .Where(link => link.AnidbAnimeID != anime.AnidbID && link.ProviderID is not null)
                .Select(link => link.ProviderID!)
                .ToHashSet();
            candidates = [.. candidates.Where(episode => !claimed.Contains(episode.ID))];
        }

        return matchingEngine.MatchEpisodes(anidbEpisodes, candidates, existing, new() { Strategy = EpisodeMatchStrategy.DateAndTitleWithinSeasons });
    }

    /// <summary>
    /// Matches the episodes of every AniDB anime linked to a show again,
    /// keeping the links already there, as a refresh does.
    /// </summary>
    /// <remarks>
    /// A failure is logged and swallowed: the show is stored by then, and a
    /// matching that could not run is no reason to fail its refresh.
    /// </remarks>
    /// <param name="seriesID">TvDB series ID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many anime were matched.</returns>
    public async Task<int> MatchLinkedEpisodes(int seriesID, CancellationToken cancellationToken = default)
    {
        var matched = 0;
        foreach (var anidbAnimeID in GetLinkedAnidbAnimeIDs(seriesID))
        {
            if (await TryMatchAndSave(anidbAnimeID, seriesID, cancellationToken).ConfigureAwait(false))
                matched++;
        }

        return matched;
    }

    /// <summary>
    /// Matches and writes one AniDB anime's episodes against a show, keeping
    /// the links already there, and logs and swallows a matching that could
    /// not run.
    /// </summary>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <param name="seriesID">TvDB series ID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the matching ran.</returns>
    private async Task<bool> TryMatchAndSave(int anidbAnimeID, int seriesID, CancellationToken cancellationToken)
    {
        try
        {
            var links = await linkingService.MatchEpisodes(
                anidbAnimeID,
                TvdbUtility.SeriesGuid(seriesID),
                useExisting: true,
                save: true,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
            logger.LogDebug("Matched {Count} TvDB episode links for AniDB anime {AnidbID} against TvDB series {SeriesID}.", links.Count, anidbAnimeID, seriesID);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            logger.LogDebug(ex, "Unable to match the episodes of AniDB anime {AnidbID} against TvDB series {SeriesID}.", anidbAnimeID, seriesID);
            return false;
        }
    }

    #endregion
}
