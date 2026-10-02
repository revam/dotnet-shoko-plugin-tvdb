using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// One stored link, whichever level it was made at, as the fake store keeps
/// it.
/// </summary>
internal sealed class FakeCrossReference : IMetadataSeriesCrossReference, IMetadataMovieCrossReference, IMetadataEpisodeCrossReference
{
    public required MetadataEntityType EntityType { get; init; }

    public required MetadataSource Source { get; init; }

    public required int AnidbAnimeID { get; init; }

    public int AnidbEpisodeID { get; init; }

    public required MetadataGuid? ProviderID { get; init; }

    public MetadataGuid? ProviderParentID { get; init; }

    public MetadataGuid? SeasonID { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public MatchRating MatchRating { get; init; }

    public int Ordering { get; set; }

    public Guid? WrittenBy { get; init; }

    public IShokoSeries? ShokoSeries => null;

    public IShokoEpisode? ShokoEpisode => null;

    public IMetadata? Provider => null;
}

/// <summary>
/// An in-memory stand-in for the core's cross-reference store, implemented
/// for real rather than stubbed: the reads filter and order the way the
/// core's do, and the writes merge per AniDB entry.
/// </summary>
internal sealed class FakeCrossReferenceStore : IMetadataCrossReferenceStore
{
    private readonly List<FakeCrossReference> _links = [];

    /// <summary>Every link held, in the order it was written.</summary>
    public IReadOnlyList<FakeCrossReference> Links => _links;

    /// <summary>Puts a link in without going through a merge, for arranging a test.</summary>
    public FakeCrossReferenceStore Add(FakeCrossReference link)
    {
        link.Ordering = _links.Count(existing => existing.EntityType == link.EntityType && Slot(existing) == Slot(link));
        _links.Add(link);
        return this;
    }

    /// <summary>Links an AniDB anime to a TvDB show, for arranging a test.</summary>
    public FakeCrossReferenceStore AddSeries(int anidbAnimeID, int tvdbSeriesID, MatchRating rating = MatchRating.UserVerified)
        => Add(new() { EntityType = MetadataEntityType.Series, Source = MetadataSource.Tvdb, AnidbAnimeID = anidbAnimeID, ProviderID = TvdbUtility.SeriesGuid(tvdbSeriesID), MatchRating = rating });

    /// <summary>Links an AniDB episode to a TvDB episode, or to nothing, for arranging a test.</summary>
    public FakeCrossReferenceStore AddEpisode(int anidbAnimeID, int anidbEpisodeID, int tvdbSeriesID, int tvdbEpisodeID, MatchRating rating = MatchRating.UserVerified)
        => Add(new()
        {
            EntityType = MetadataEntityType.Episode,
            Source = MetadataSource.Tvdb,
            AnidbAnimeID = anidbAnimeID,
            AnidbEpisodeID = anidbEpisodeID,
            ProviderID = tvdbEpisodeID is 0 ? null : TvdbUtility.EpisodeGuid(tvdbEpisodeID),
            ProviderParentID = tvdbEpisodeID is 0 ? null : TvdbUtility.SeriesGuid(tvdbSeriesID),
            MatchRating = rating,
        });

    #region Reading

    public IReadOnlyList<IMetadataSeriesCrossReference> GetSeriesLinks(int anidbAnimeID, MetadataSource? source = null)
        => [.. Of(MetadataEntityType.Series, source).Where(link => link.AnidbAnimeID == anidbAnimeID).OrderBy(link => link.Ordering)];

    public IReadOnlyList<IMetadataMovieCrossReference> GetMovieLinks(int anidbEpisodeID, MetadataSource? source = null)
        => [.. Of(MetadataEntityType.Movie, source).Where(link => link.AnidbEpisodeID == anidbEpisodeID).OrderBy(link => link.Ordering)];

    public IReadOnlyList<IMetadataMovieCrossReference> GetMovieLinksForSeries(int anidbAnimeID, MetadataSource? source = null)
        => [.. Of(MetadataEntityType.Movie, source).Where(link => link.AnidbAnimeID == anidbAnimeID).OrderBy(link => link.AnidbEpisodeID).ThenBy(link => link.Ordering)];

    public IReadOnlyList<IMetadataEpisodeCrossReference> GetEpisodeLinks(int anidbEpisodeID, MetadataSource? source = null)
        => [.. Of(MetadataEntityType.Episode, source).Where(link => link.AnidbEpisodeID == anidbEpisodeID).OrderBy(link => link.Ordering)];

    public IReadOnlyList<IMetadataEpisodeCrossReference> GetEpisodeLinksForSeries(int anidbAnimeID, MetadataSource? source = null)
        => [.. Of(MetadataEntityType.Episode, source).Where(link => link.AnidbAnimeID == anidbAnimeID).OrderBy(link => link.AnidbEpisodeID).ThenBy(link => link.Ordering)];

    public IReadOnlyList<IMetadataCrossReference> GetLinksTo(MetadataGuid entry)
        => [.. _links.Where(link => link.ProviderID == entry).OrderBy(link => link.Ordering)];

    public IReadOnlyList<IMetadataEpisodeCrossReference> GetEpisodeLinksInto(MetadataGuid series)
        => [.. Of(MetadataEntityType.Episode, series.Source).Where(link => link.ProviderParentID == series).OrderBy(link => link.AnidbEpisodeID).ThenBy(link => link.Ordering)];

    public IReadOnlyList<IMetadataSeriesCrossReference> GetAllSeriesLinks(MetadataSource? source = null)
        => [.. Of(MetadataEntityType.Series, source)];

    public IReadOnlyList<IMetadataMovieCrossReference> GetAllMovieLinks(MetadataSource? source = null)
        => [.. Of(MetadataEntityType.Movie, source)];

    public IReadOnlyList<IMetadataEpisodeCrossReference> GetAllEpisodeLinks(MetadataSource? source = null)
        => [.. Of(MetadataEntityType.Episode, source)];

    public IReadOnlyList<IMetadataSeasonCrossReference> GetAllSeasonLinks(MetadataSource? source = null)
        => [];

    #endregion

    #region Writing

    public Task<IReadOnlyList<IMetadataSeriesCrossReference>> MergeSeriesLinks(IEnumerable<MetadataSeriesLinkData> links, IEnumerable<IMetadataSeriesCrossReference>? removals = null, MetadataLinkUpdateOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<IMetadataSeriesCrossReference>>([.. Merge(MetadataEntityType.Series, links, removals, options, _ => 0, _ => null)]);

    public Task<IReadOnlyList<IMetadataMovieCrossReference>> MergeMovieLinks(IEnumerable<MetadataMovieLinkData> links, IEnumerable<IMetadataMovieCrossReference>? removals = null, MetadataLinkUpdateOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<IMetadataMovieCrossReference>>([.. Merge(MetadataEntityType.Movie, links, removals, options, link => link.AnidbEpisodeID, _ => null)]);

    public Task<IReadOnlyList<IMetadataEpisodeCrossReference>> MergeEpisodeLinks(IEnumerable<MetadataEpisodeLinkData> links, IEnumerable<IMetadataEpisodeCrossReference>? removals = null, MetadataLinkUpdateOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<IMetadataEpisodeCrossReference>>([.. Merge(MetadataEntityType.Episode, links, removals, options, link => link.AnidbEpisodeID, link => link.ProviderParentID)]);

    public Task<IReadOnlyList<IMetadataCrossReference>> OrderLinks(IEnumerable<IMetadataCrossReference> links, CancellationToken cancellationToken = default)
    {
        var moved = new List<IMetadataCrossReference>();
        foreach (var group in links.OfType<FakeCrossReference>().GroupBy(link => (link.EntityType, Slot(link))))
        {
            var ordering = 0;
            foreach (var link in group)
            {
                if (link.Ordering != ordering)
                    moved.Add(link);
                link.Ordering = ordering++;
            }
        }

        return Task.FromResult<IReadOnlyList<IMetadataCrossReference>>(moved);
    }

    public Task<IReadOnlyList<IMetadataCrossReference>> RemoveLinksForSeries(MetadataSource source, int anidbAnimeID, MetadataEntityType? entityType = null, CancellationToken cancellationToken = default)
    {
        var gone = _links
            .Where(link => link.Source == source && link.AnidbAnimeID == anidbAnimeID)
            .Where(link => entityType is null || link.EntityType == entityType)
            .ToList();
        foreach (var link in gone)
            _links.Remove(link);

        return Task.FromResult<IReadOnlyList<IMetadataCrossReference>>(gone);
    }

    private IEnumerable<FakeCrossReference> Of(MetadataEntityType entityType, MetadataSource? source)
        => _links.Where(link => link.EntityType == entityType && (source is null || link.Source == source));

    private static (MetadataSource, int, int) Slot(FakeCrossReference link)
        => (link.Source, link.AnidbAnimeID, link.AnidbEpisodeID);

    private List<FakeCrossReference> Merge<TData>(
        MetadataEntityType entityType,
        IEnumerable<TData> links,
        IEnumerable<IMetadataCrossReference>? removals,
        MetadataLinkUpdateOptions? options,
        Func<TData, int> episodeOf,
        Func<TData, MetadataGuid?> parentOf
    )
        where TData : MetadataLinkData
    {
        var written = new List<FakeCrossReference>();
        foreach (var removal in removals ?? [])
        {
            var episodeID = removal is IMetadataEpisodeCrossReference episode ? episode.AnidbEpisodeID : removal is IMetadataMovieCrossReference movie ? movie.AnidbEpisodeID : 0;
            if (_links.FirstOrDefault(link => link.EntityType == removal.EntityType && link.Source == removal.Source && link.AnidbAnimeID == removal.AnidbAnimeID && link.AnidbEpisodeID == episodeID && link.ProviderID == removal.ProviderID) is { } gone)
            {
                _links.Remove(gone);
                written.Add(gone);
            }
        }

        foreach (var group in links.GroupBy(link => (link.Source, link.AnidbAnimeID, episodeOf(link))))
        {
            if (options?.ReplaceExisting ?? false)
                foreach (var stale in _links.Where(link => link.EntityType == entityType && Slot(link) == group.Key && !group.Any(item => item.ProviderID == link.ProviderID)).ToList())
                    _links.Remove(stale);

            foreach (var link in group)
            {
                var stored = new FakeCrossReference
                {
                    EntityType = entityType,
                    Source = link.Source,
                    AnidbAnimeID = link.AnidbAnimeID,
                    AnidbEpisodeID = episodeOf(link),
                    ProviderID = link.ProviderID,
                    ProviderParentID = parentOf(link),
                    MatchRating = link.MatchRating,
                    WrittenBy = options?.WrittenBy,
                };
                if (_links.FirstOrDefault(existing => existing.EntityType == entityType && Slot(existing) == Slot(stored) && existing.ProviderID == stored.ProviderID) is { } replaced)
                {
                    stored.Ordering = replaced.Ordering;
                    _links[_links.IndexOf(replaced)] = stored;
                }
                else
                {
                    stored.Ordering = _links.Count(existing => existing.EntityType == entityType && Slot(existing) == Slot(stored));
                    _links.Add(stored);
                }

                written.Add(stored);
            }
        }

        return written;
    }

    #endregion
}
