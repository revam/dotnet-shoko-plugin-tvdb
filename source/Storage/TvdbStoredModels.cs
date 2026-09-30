using System;
using System.Collections.Generic;

namespace Shoko.Plugin.Tvdb.Storage;

/// <summary>
/// What the plugin keeps of a show besides the core's stores: what only
/// TheTVDB has, and where its images are.
/// </summary>
/// <remarks>
/// Kept as one row per show in the <c>Shows</c> table of the plugin's own
/// <see cref="TvdbDbContext"/>, the lists as JSON columns. The show itself,
/// its seasons, episodes, titles and the rest are in the core's typed stores.
/// </remarks>
public sealed class TvdbStoredSeries
{
    /// <summary>TheTVDB series ID.</summary>
    public int ID { get; set; }

    /// <summary>The show's URL slug.</summary>
    public string? Slug { get; set; }

    /// <summary>TheTVDB's name for the show's status, e.g. <c>Continuing</c>.</summary>
    public string? Status { get; set; }

    /// <summary>The season type the show's own seasons are in, e.g. <c>official</c>.</summary>
    public string SeasonType { get; set; } = "official";

    /// <summary>The other season types the show has, stored as orderings.</summary>
    public List<string> AlternateSeasonTypes { get; set; } = [];

    /// <summary>The show's default poster, as a resource ID of the image template.</summary>
    public string? Poster { get; set; }

    /// <summary>The show's and its seasons' artwork.</summary>
    public List<TvdbStoredArtwork> Artworks { get; set; } = [];

    /// <summary>The default poster of each season, by TheTVDB season ID.</summary>
    public Dictionary<int, string> SeasonPosters { get; set; } = [];

    /// <summary>The thumbnail of each episode, by TheTVDB episode ID.</summary>
    public Dictionary<int, string> EpisodeThumbnails { get; set; } = [];

    /// <summary>When the show was last fetched.</summary>
    public DateTime FetchedAt { get; set; }
}

/// <summary>One piece of a show's or a season's artwork.</summary>
public sealed class TvdbStoredArtwork
{
    /// <summary>TheTVDB artwork ID.</summary>
    public int ID { get; set; }

    /// <summary>TheTVDB's number for the kind of artwork.</summary>
    public int Type { get; set; }

    /// <summary>The image, as a resource ID of the image template.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>TheTVDB's three-letter code of the language of any text in it.</summary>
    public string? Language { get; set; }

    /// <summary>The width in pixels.</summary>
    public int? Width { get; set; }

    /// <summary>The height in pixels.</summary>
    public int? Height { get; set; }

    /// <summary>The season it belongs to, or <see langword="null"/> for the show's own.</summary>
    public int? SeasonID { get; set; }
}

/// <summary>
/// The photo of a person or the image of a character, which the core's
/// people store has no room for.
/// </summary>
public sealed class TvdbStoredPortrait
{
    /// <summary>
    /// What the portrait is of: the person's or character's identifier
    /// without its source, e.g. <c>creator/123</c>.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The image, as a resource ID of the image template.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// What the plugin keeps of a person's own TheTVDB record: the details the
/// credits on a show do not carry, and when they were fetched.
/// </summary>
/// <remarks>
/// The core's people store is rewritten from the credits on every refresh
/// and stamps each person as updated when it is, so its stamp cannot say
/// when the details were last fetched. This record can, and it is also what
/// the details are written from again on a refresh that does not fetch them.
/// The dates are kept as TheTVDB wrote them, so a later build can read them
/// better without fetching again.
/// </remarks>
public sealed class TvdbStoredPerson
{
    /// <summary>TheTVDB person ID.</summary>
    public int ID { get; set; }

    /// <summary>
    /// Whether TheTVDB had the record. A person it did not have is kept too,
    /// so they are not asked for again until the record is stale.
    /// </summary>
    public bool Found { get; set; }

    /// <summary>The person's name.</summary>
    public string? Name { get; set; }

    /// <summary>The person's URL slug.</summary>
    public string? Slug { get; set; }

    /// <summary>The person's photo, as a resource ID of the image template.</summary>
    public string? Image { get; set; }

    /// <summary>When the person was born, as TheTVDB wrote it.</summary>
    public string? Birth { get; set; }

    /// <summary>When the person died, as TheTVDB wrote it.</summary>
    public string? Death { get; set; }

    /// <summary>TheTVDB's number for the person's gender.</summary>
    public int? Gender { get; set; }

    /// <summary>The person's other names and translated names.</summary>
    public List<TvdbStoredText> Names { get; set; } = [];

    /// <summary>The person's biographies and translated overviews.</summary>
    public List<TvdbStoredText> Biographies { get; set; } = [];

    /// <summary>The person's IDs on other sites.</summary>
    public List<TvdbStoredRemoteID> RemoteIDs { get; set; } = [];

    /// <summary>When TheTVDB last changed the record, as it wrote it.</summary>
    public string? LastUpdated { get; set; }

    /// <summary>When the record was last fetched, in UTC.</summary>
    public DateTime FetchedAt { get; set; }
}

/// <summary>A piece of text in one language.</summary>
public sealed class TvdbStoredText
{
    /// <summary>TheTVDB's three-letter code of the language, when known.</summary>
    public string? Language { get; set; }

    /// <summary>The text.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>An ID on another site.</summary>
public sealed class TvdbStoredRemoteID
{
    /// <summary>The ID, or for a website its URL.</summary>
    public string ID { get; set; } = string.Empty;

    /// <summary>TheTVDB's number for the kind of site.</summary>
    public int? Type { get; set; }

    /// <summary>The site's name, e.g. <c>IMDB</c>.</summary>
    public string? SourceName { get; set; }
}
