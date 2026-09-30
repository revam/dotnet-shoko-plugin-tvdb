using System;
using System.Collections.Generic;
using System.Linq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Storage;

namespace Shoko.Plugin.Tvdb.Services;

/// <summary>
/// Hands the core TheTVDB's images and keeps its default template URL
/// registered.
/// </summary>
/// <remarks>
/// A show has posters, backgrounds, banners and logos, a season the same but
/// logos, an episode a thumbnail, and a person or character one portrait.
/// The core decides which of them to download from the admin's image settings
/// for the source.
/// </remarks>
/// <param name="imageManager">The core's image manager.</param>
/// <param name="store">The plugin's store, which knows where the images are.</param>
public sealed class TvdbImageService(IImageManager imageManager, TvdbStore store)
{
    private volatile bool _registered;

    #region Template

    /// <summary>
    /// Registers TheTVDB's default template URL with the core, once.
    /// </summary>
    /// <remarks>
    /// The core keeps a registration in memory only and lets the user set their
    /// own over it, which the plugin never touches.
    /// </remarks>
    public void RegisterTemplateUrl()
    {
        if (_registered)
            return;

        imageManager.RegisterTemplateUrl(TvdbSources.Tvdb, TvdbImages.TemplateUrl);
        _registered = true;
    }

    #endregion

    #region Images

    /// <summary>
    /// The images TheTVDB has for one of its entities.
    /// </summary>
    /// <param name="entityID">The entity.</param>
    /// <returns>
    /// The images, or <see langword="null"/> for an entity the plugin keeps
    /// no images for, which leaves its linked images alone.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="entityID"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<ImageCandidate>? GetImages(MetadataGuid entityID)
    {
        ArgumentNullException.ThrowIfNull(entityID);

        if (entityID.Source != TvdbSources.Tvdb)
            return null;

        RegisterTemplateUrl();
        if (TvdbUtility.TryGetID(entityID, MetadataEntityType.Series, out var seriesID))
            return store.GetShow(seriesID) is { } show ? SeriesImages(show) : null;

        if (TvdbUtility.TryGetID(entityID, MetadataEntityType.Season, out var seasonID))
            return ShowOf(store.Series.GetSeason(entityID)?.SeriesID) is { } show ? SeasonImages(show, seasonID) : null;

        if (TvdbUtility.TryGetID(entityID, MetadataEntityType.Episode, out var episodeID))
        {
            if (ShowOf(store.Series.GetEpisode(entityID)?.SeriesID) is not { } show)
                return null;

            return show.EpisodeThumbnails.TryGetValue(episodeID, out var thumbnail)
                ? [new() { ResourceID = thumbnail, ImageType = ImageEntityType.Backdrop, IsDefault = true }]
                : [];
        }

        if (entityID.EntityType == MetadataEntityType.Creator || entityID.EntityType == MetadataEntityType.Character)
            return store.GetPortrait(entityID) is { } portrait ? [new() { ResourceID = portrait, ImageType = ImageEntityType.Primary, IsDefault = true }] : [];

        return null;
    }

    private TvdbStoredSeries? ShowOf(MetadataGuid? seriesID)
        => TvdbUtility.TryGetID(seriesID, MetadataEntityType.Series, out var id) ? store.GetShow(id) : null;

    private static List<ImageCandidate> SeriesImages(TvdbStoredSeries show)
        => Candidates(show.Artworks.Where(artwork => artwork.SeasonID is null), show.Poster);

    private static List<ImageCandidate> SeasonImages(TvdbStoredSeries show, int seasonID)
        => Candidates(show.Artworks.Where(artwork => artwork.SeasonID == seasonID), show.SeasonPosters.GetValueOrDefault(seasonID));

    // TheTVDB's artwork in its own order, with the entity's default poster
    // marked as the default, or added first when it is not among the rest.
    private static List<ImageCandidate> Candidates(IEnumerable<TvdbStoredArtwork> artworks, string? poster)
    {
        var candidates = artworks
            .Select(artwork => new ImageCandidate
            {
                ResourceID = artwork.Path,
                ImageType = TvdbImages.ToImageType(artwork.Type),
                Width = artwork.Width,
                Height = artwork.Height,
                LanguageCode = TvdbUtility.ToImageLanguageCode(artwork.Language),
                IsDefault = string.Equals(artwork.Path, poster, StringComparison.Ordinal) && TvdbImages.ToImageType(artwork.Type) is ImageEntityType.Primary,
            })
            .Where(candidate => candidate.ImageType is not ImageEntityType.None)
            .DistinctBy(candidate => (candidate.ResourceID, candidate.ImageType))
            .ToList();
        if (!string.IsNullOrEmpty(poster) && !candidates.Any(candidate => candidate.IsDefault))
            candidates.Insert(0, new() { ResourceID = poster, ImageType = ImageEntityType.Primary, IsDefault = true });

        return candidates;
    }

    #endregion
}
