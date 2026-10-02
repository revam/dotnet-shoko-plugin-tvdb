using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shoko.Plugin.Tvdb.Api;

/// <summary>
/// The JSON settings every TheTVDB response is read with.
/// </summary>
/// <remarks>
/// TheTVDB v4 is snake_case in places and camelCase in others, sometimes for
/// two fields of the same object, so the names are spelled out per property
/// with <see cref="JsonPropertyNameAttribute"/> rather than inferred from a
/// naming policy. <see cref="JsonNumberHandling.AllowReadingFromString"/> is
/// on because several numeric fields come back quoted depending on the
/// endpoint.
/// </remarks>
internal static class TvdbJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}

/// <summary>
/// The envelope every v4 endpoint wraps its payload in.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class TvdbResponse<T>
{
    /// <summary>
    /// <c>success</c> on a good answer, <c>failure</c> otherwise.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// The payload, absent on failure.
    /// </summary>
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    /// <summary>
    /// The paging links, present on list endpoints.
    /// </summary>
    [JsonPropertyName("links")]
    public TvdbLinks? Links { get; set; }
}

/// <summary>
/// Paging links on a list endpoint.
/// </summary>
public sealed class TvdbLinks
{
    /// <summary>The URL of the next page, if there is one.</summary>
    [JsonPropertyName("next")]
    public string? Next { get; set; }

    /// <summary>The total number of records across every page.</summary>
    [JsonPropertyName("total_items")]
    public int? TotalItems { get; set; }
}

/// <summary>
/// The body of <c>POST /v4/login</c>.
/// </summary>
public sealed class TvdbLoginRequest
{
    /// <summary>The API key identifying the program.</summary>
    [JsonPropertyName("apikey")]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The subscriber PIN identifying the user, omitted entirely when unset:
    /// TheTVDB rejects an empty string here rather than treating it as absent.
    /// </summary>
    [JsonPropertyName("pin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pin { get; set; }
}

/// <summary>
/// The payload of a successful login.
/// </summary>
public sealed class TvdbLoginData
{
    /// <summary>The bearer token, good for roughly a month.</summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

/// <summary>
/// A show as <c>/series/{id}/extended</c> returns it.
/// </summary>
public sealed class TvdbSeriesExtended
{
    /// <summary>TheTVDB series id.</summary>
    [JsonPropertyName("id")]
    public int ID { get; set; }

    /// <summary>The show's own name, in whatever its original language is.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The show's URL slug.</summary>
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    /// <summary>The overview, in the show's original language.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>The three-letter code of the show's original language.</summary>
    [JsonPropertyName("originalLanguage")]
    public string? OriginalLanguage { get; set; }

    /// <summary>The first air date, <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("firstAired")]
    public string? FirstAired { get; set; }

    /// <summary>The last air date, <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("lastAired")]
    public string? LastAired { get; set; }

    /// <summary>The average runtime of an episode, in minutes.</summary>
    [JsonPropertyName("averageRuntime")]
    public int? AverageRuntime { get; set; }

    /// <summary>
    /// TheTVDB's score, a popularity count on its own scale rather than a
    /// rating: One Piece's is in the millions.
    /// </summary>
    [JsonPropertyName("score")]
    public double? Score { get; set; }

    /// <summary>Whether TheTVDB shuffles the show's episode order.</summary>
    [JsonPropertyName("isOrderRandomized")]
    public bool IsOrderRandomized { get; set; }

    /// <summary>The release status.</summary>
    [JsonPropertyName("status")]
    public TvdbStatus? Status { get; set; }

    /// <summary>Every name the show is known by.</summary>
    [JsonPropertyName("aliases")]
    public List<TvdbAlias>? Aliases { get; set; }

    /// <summary>The seasons, across every ordering TheTVDB holds.</summary>
    [JsonPropertyName("seasons")]
    public List<TvdbSeason>? Seasons { get; set; }

    /// <summary>The networks and studios that made or aired the show.</summary>
    [JsonPropertyName("companies")]
    public List<TvdbCompany>? Companies { get; set; }

    /// <summary>The content ratings, one per issuing body.</summary>
    [JsonPropertyName("contentRatings")]
    public List<TvdbContentRating>? ContentRatings { get; set; }

    /// <summary>The path or URL of the show's default poster.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>The ID of the season type the show uses by default.</summary>
    [JsonPropertyName("defaultSeasonType")]
    public int? DefaultSeasonType { get; set; }

    /// <summary>The season types the show has episodes in.</summary>
    [JsonPropertyName("seasonTypes")]
    public List<TvdbSeasonType>? SeasonTypes { get; set; }

    /// <summary>The three-letter code of the country the show comes from.</summary>
    [JsonPropertyName("originalCountry")]
    public string? OriginalCountry { get; set; }

    /// <summary>The network the show first aired on.</summary>
    [JsonPropertyName("originalNetwork")]
    public TvdbCompany? OriginalNetwork { get; set; }

    /// <summary>The network the show airs on now.</summary>
    [JsonPropertyName("latestNetwork")]
    public TvdbCompany? LatestNetwork { get; set; }

    /// <summary>The show's genres.</summary>
    [JsonPropertyName("genres")]
    public List<TvdbGenre>? Genres { get; set; }

    /// <summary>The tag options set on the show.</summary>
    [JsonPropertyName("tags")]
    public List<TvdbTagOption>? Tags { get; set; }

    /// <summary>The show's IDs on other sites.</summary>
    [JsonPropertyName("remoteIds")]
    public List<TvdbRemoteID>? RemoteIDs { get; set; }

    /// <summary>The show's artwork of every kind.</summary>
    [JsonPropertyName("artworks")]
    public List<TvdbArtwork>? Artworks { get; set; }

    /// <summary>The people credited on the show, cast and crew alike.</summary>
    [JsonPropertyName("characters")]
    public List<TvdbCharacter>? Characters { get; set; }

    /// <summary>
    /// The show's names and overviews in every language TheTVDB has, which
    /// <c>meta=translations</c> adds to the record.
    /// </summary>
    [JsonPropertyName("translations")]
    public TvdbTranslations? Translations { get; set; }
}

/// <summary>A release status.</summary>
public sealed class TvdbStatus
{
    /// <summary>The status name, e.g. <c>Continuing</c> or <c>Ended</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>An alternate name for a show.</summary>
public sealed class TvdbAlias
{
    /// <summary>The three-letter language code the alias is in.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>The alias itself.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>A season of a show, in one of TheTVDB's orderings.</summary>
public sealed class TvdbSeason
{
    /// <summary>TheTVDB season id, unique across orderings.</summary>
    [JsonPropertyName("id")]
    public int ID { get; set; }

    /// <summary>The series the season belongs to.</summary>
    [JsonPropertyName("seriesId")]
    public int SeriesID { get; set; }

    /// <summary>The season number within its ordering.</summary>
    [JsonPropertyName("number")]
    public int Number { get; set; }

    /// <summary>
    /// The season's own name, when it has one. The seasons on
    /// <c>/series/{id}/extended</c> carry none, only the codes of the
    /// languages they have a name in, so a season fetched with its show is
    /// named by its number.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The ordering this season belongs to.</summary>
    [JsonPropertyName("type")]
    public TvdbSeasonType? Type { get; set; }

    /// <summary>The path or URL of the season's default poster.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }
}

/// <summary>One of TheTVDB's parallel episode orderings.</summary>
public sealed class TvdbSeasonType
{
    /// <summary>The ordering's ID.</summary>
    [JsonPropertyName("id")]
    public int? ID { get; set; }

    /// <summary>The ordering's stable name, e.g. <c>official</c> or <c>absolute</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>The ordering's display name, e.g. <c>Alternate Order 2</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// What the show calls the ordering, when it names it, e.g.
    /// <c>Streaming Order</c> for its <c>alttwo</c> ordering.
    /// </summary>
    [JsonPropertyName("alternateName")]
    public string? AlternateName { get; set; }
}

/// <summary>A company credited on a show.</summary>
public sealed class TvdbCompany
{
    /// <summary>TheTVDB company id.</summary>
    [JsonPropertyName("id")]
    public int ID { get; set; }

    /// <summary>The company name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>What kind of company it is.</summary>
    [JsonPropertyName("companyType")]
    public TvdbCompanyType? CompanyType { get; set; }

    /// <summary>
    /// The ID of what kind of company it is, on the records that carry only
    /// that: 1 a network, 2 a studio, 3 a production company.
    /// </summary>
    [JsonPropertyName("primaryCompanyType")]
    public int? PrimaryCompanyType { get; set; }
}

/// <summary>A company's kind.</summary>
public sealed class TvdbCompanyType
{
    /// <summary>The kind's ID: 1 a network, 2 a studio, 3 a production company.</summary>
    [JsonPropertyName("companyTypeId")]
    public int? CompanyTypeID { get; set; }

    /// <summary>The kind's display name, e.g. <c>Network</c> or <c>Studio</c>.</summary>
    [JsonPropertyName("companyTypeName")]
    public string? CompanyTypeName { get; set; }
}

/// <summary>A content rating issued by one body.</summary>
public sealed class TvdbContentRating
{
    /// <summary>The rating, e.g. <c>TV-14</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The country whose body issued it.</summary>
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    /// <summary>What the rating is about, when it says.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// A page of <c>/series/{id}/episodes/{season-type}</c>, or of the same with
/// a language after it.
/// </summary>
/// <remarks>
/// The plain listing puts the show under <c>series</c> beside the episodes;
/// the translated one answers with the show itself, the episodes in it. Both
/// have the episodes under <c>episodes</c>, which is all the plugin reads.
/// </remarks>
public sealed class TvdbEpisodesData
{
    /// <summary>This page of episodes.</summary>
    [JsonPropertyName("episodes")]
    public List<TvdbEpisode>? Episodes { get; set; }
}

/// <summary>An episode.</summary>
public sealed class TvdbEpisode
{
    /// <summary>TheTVDB episode id.</summary>
    [JsonPropertyName("id")]
    public int ID { get; set; }

    /// <summary>The series the episode belongs to.</summary>
    [JsonPropertyName("seriesId")]
    public int SeriesID { get; set; }

    /// <summary>The episode's own name, in the show's original language.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The synopsis, in the show's original language.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>
    /// The season number within the requested ordering. TheTVDB has no
    /// special flag: season zero is the convention, and
    /// <see cref="Mapping.TvdbEntityMapper"/> reads it that way.
    /// </summary>
    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    /// <summary>The episode number within the season.</summary>
    [JsonPropertyName("number")]
    public int Number { get; set; }

    /// <summary>
    /// The absolute episode number, when the show has one; <c>0</c> for a
    /// special.
    /// </summary>
    [JsonPropertyName("absoluteNumber")]
    public int? AbsoluteNumber { get; set; }

    /// <summary>
    /// For a special, the aired-order season it airs before.
    /// </summary>
    [JsonPropertyName("airsBeforeSeason")]
    public int? AirsBeforeSeason { get; set; }

    /// <summary>
    /// For a special, the episode it airs before, within
    /// <see cref="AirsBeforeSeason"/>.
    /// </summary>
    [JsonPropertyName("airsBeforeEpisode")]
    public int? AirsBeforeEpisode { get; set; }

    /// <summary>
    /// For a special, the aired-order season it airs after, when it comes
    /// after that season's last episode.
    /// </summary>
    [JsonPropertyName("airsAfterSeason")]
    public int? AirsAfterSeason { get; set; }

    /// <summary>The air date, <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("aired")]
    public string? Aired { get; set; }

    /// <summary>The runtime, in minutes.</summary>
    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    /// <summary>The path or URL of the episode's thumbnail.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }
}

/// <summary>A translated name and overview for one entity in one language.</summary>
public sealed class TvdbTranslation
{
    /// <summary>The translated name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The translated overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>The three-letter code of the language this is in.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>Whether TheTVDB considers this the primary translation.</summary>
    [JsonPropertyName("isPrimary")]
    public bool? IsPrimary { get; set; }

    /// <summary>Alternate names in this language.</summary>
    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }

    /// <summary>Whether the name is an alias rather than a translation.</summary>
    [JsonPropertyName("isAlias")]
    public bool? IsAlias { get; set; }
}

/// <summary>One hit from <c>/v4/search</c>.</summary>
public sealed class TvdbSearchResult
{
    /// <summary>
    /// The prefixed id, e.g. <c>series-71663</c>. TheTVDB's search is across
    /// every entity type, so the bare id is only unique together with the type.
    /// </summary>
    [JsonPropertyName("id")]
    public string? ID { get; set; }

    /// <summary>The bare TheTVDB id, as a string.</summary>
    [JsonPropertyName("tvdb_id")]
    public string? TvdbID { get; set; }

    /// <summary>The entity type, e.g. <c>series</c> or <c>movie</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>The name, in whatever language the search matched.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The year, as a string.</summary>
    [JsonPropertyName("year")]
    public string? Year { get; set; }

    /// <summary>The first air date, <c>yyyy-MM-dd</c>, when TheTVDB knows it.</summary>
    [JsonPropertyName("first_air_time")]
    public string? FirstAirTime { get; set; }

    /// <summary>Every name the hit is known by, across languages.</summary>
    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }

    /// <summary>The names keyed by three-letter language code.</summary>
    [JsonPropertyName("translations")]
    public Dictionary<string, string>? Translations { get; set; }

    /// <summary>The overview, in whatever language the search matched.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>The overviews keyed by three-letter language code.</summary>
    [JsonPropertyName("overviews")]
    public Dictionary<string, string>? Overviews { get; set; }

    /// <summary>The three-letter code of the hit's original language.</summary>
    [JsonPropertyName("primary_language")]
    public string? PrimaryLanguage { get; set; }

    /// <summary>The URL of the hit's poster.</summary>
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    /// <summary>The genre names. Not sent on a show's hit.</summary>
    [JsonPropertyName("genres")]
    public List<string>? Genres { get; set; }
}

/// <summary>A genre.</summary>
public sealed class TvdbGenre
{
    /// <summary>TheTVDB genre ID.</summary>
    [JsonPropertyName("id")]
    public int ID { get; set; }

    /// <summary>The genre's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The genre's URL slug.</summary>
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }
}

/// <summary>One option of a tag, as set on a show.</summary>
public sealed class TvdbTagOption
{
    /// <summary>TheTVDB ID of the option.</summary>
    [JsonPropertyName("id")]
    public int ID { get; set; }

    /// <summary>The ID of the tag the option belongs to.</summary>
    [JsonPropertyName("tag")]
    public int? Tag { get; set; }

    /// <summary>The name of the tag the option belongs to, e.g. <c>Setting</c>.</summary>
    [JsonPropertyName("tagName")]
    public string? TagName { get; set; }

    /// <summary>The option's own name, e.g. <c>Japan</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>What the option means, when TheTVDB says.</summary>
    [JsonPropertyName("helpText")]
    public string? HelpText { get; set; }
}

/// <summary>A show's ID on another site.</summary>
public sealed class TvdbRemoteID
{
    /// <summary>The ID, or for a website its URL.</summary>
    [JsonPropertyName("id")]
    public string? ID { get; set; }

    /// <summary>
    /// TheTVDB's number for the site, as <c>/sources/types</c> lists them:
    /// 2 an IMDb title, 16 an IMDb person, 12 a TMDB show, 15 a TMDB person
    /// and so on.
    /// </summary>
    [JsonPropertyName("type")]
    public int? Type { get; set; }

    /// <summary>
    /// The site's name, e.g. <c>IMDB</c>, <c>TheMovieDB.com</c> or
    /// <c>X (Twitter)</c>, the same for a site's show and person IDs.
    /// </summary>
    [JsonPropertyName("sourceName")]
    public string? SourceName { get; set; }
}

/// <summary>One piece of artwork.</summary>
public sealed class TvdbArtwork
{
    /// <summary>TheTVDB artwork ID.</summary>
    [JsonPropertyName("id")]
    public int ID { get; set; }

    /// <summary>The path or URL of the image.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>
    /// What kind of artwork it is: 1 a series banner, 2 a series poster, 3 a
    /// series background, 6 to 8 the same for a season, 23 a series logo.
    /// </summary>
    [JsonPropertyName("type")]
    public int Type { get; set; }

    /// <summary>The three-letter code of the language of any text in it.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>TheTVDB's score for it, higher being better.</summary>
    [JsonPropertyName("score")]
    public double? Score { get; set; }

    /// <summary>The width in pixels.</summary>
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    /// <summary>The height in pixels.</summary>
    [JsonPropertyName("height")]
    public int? Height { get; set; }

    /// <summary>The season it belongs to, for a season's artwork.</summary>
    [JsonPropertyName("seasonId")]
    public int? SeasonID { get; set; }

    /// <summary>The episode it belongs to, for an episode's artwork.</summary>
    [JsonPropertyName("episodeId")]
    public int? EpisodeID { get; set; }
}

/// <summary>
/// A credit on a show: a character and who plays them, or a crew member and
/// their job. TheTVDB files both under <c>characters</c>.
/// </summary>
/// <remarks>
/// The record also has <c>nameTranslations</c> and <c>overviewTranslations</c>,
/// which only list the codes of the languages it has text in (and are empty
/// or null on the credits seen so far), and <c>tagOptions</c>, which the
/// core's character store has no place for. The plugin reads none of them.
/// </remarks>
public sealed class TvdbCharacter
{
    /// <summary>TheTVDB ID of the credit, which is the character's for a role.</summary>
    [JsonPropertyName("id")]
    public long ID { get; set; }

    /// <summary>The character's name, for a role.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>TheTVDB ID of the person credited.</summary>
    [JsonPropertyName("peopleId")]
    public int? PeopleID { get; set; }

    /// <summary>The person's name.</summary>
    [JsonPropertyName("personName")]
    public string? PersonName { get; set; }

    /// <summary>
    /// What the person did: <c>Actor</c>, <c>Guest Star</c>, <c>Director</c>,
    /// <c>Writer</c>, <c>Creator</c>, <c>Producer</c> and so on.
    /// </summary>
    [JsonPropertyName("peopleType")]
    public string? PeopleType { get; set; }

    /// <summary>TheTVDB's number for <see cref="PeopleType"/>.</summary>
    [JsonPropertyName("type")]
    public int? Type { get; set; }

    /// <summary>The position in the credits.</summary>
    [JsonPropertyName("sort")]
    public int? Sort { get; set; }

    /// <summary>Whether the role is a featured one.</summary>
    [JsonPropertyName("isFeatured")]
    public bool IsFeatured { get; set; }

    /// <summary>The path or URL of the character's image.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>The path or URL of the person's photo.</summary>
    [JsonPropertyName("personImgURL")]
    public string? PersonImage { get; set; }

    /// <summary>The episode the credit is for, when it is not for the whole show.</summary>
    [JsonPropertyName("episodeId")]
    public int? EpisodeID { get; set; }

    /// <summary>
    /// The character's other names, each with its language. Empty or null on
    /// every credit of One Piece.
    /// </summary>
    [JsonPropertyName("aliases")]
    public List<TvdbAlias>? Aliases { get; set; }

    /// <summary>
    /// The page of the person credited, not of the character:
    /// <c>https://thetvdb.com/people/412417-mayumi-tanaka</c> on a show's
    /// credits, and the bare <c>412417-mayumi-tanaka</c> on a person's own
    /// record. The character's page is under the show instead.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// A person's own record, from <c>/people/{id}/extended?meta=translations</c>.
/// </summary>
/// <remarks>
/// The record also has <c>awards</c>, <c>races</c>, <c>characters</c>,
/// <c>tagOptions</c> and <c>score</c>, which the core's people store has no
/// place for, and <c>nameTranslations</c> and <c>overviewTranslations</c>,
/// which only list language codes; the text is under
/// <see cref="Translations"/>. The plugin reads none of them.
/// </remarks>
public sealed class TvdbPersonExtended
{
    /// <summary>TheTVDB person ID.</summary>
    [JsonPropertyName("id")]
    public int ID { get; set; }

    /// <summary>The person's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// The person's URL slug, with their ID already in front, e.g.
    /// <c>412417-mayumi-tanaka</c>.
    /// </summary>
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    /// <summary>The path or URL of the person's photo.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>When the person was born, as TheTVDB writes it: a full date, or only part of one.</summary>
    [JsonPropertyName("birth")]
    public string? Birth { get; set; }

    /// <summary>When the person died, written as <see cref="Birth"/> is.</summary>
    [JsonPropertyName("death")]
    public string? Death { get; set; }

    /// <summary>Where the person was born.</summary>
    [JsonPropertyName("birthPlace")]
    public string? BirthPlace { get; set; }

    /// <summary>
    /// TheTVDB's number for the person's gender, as <c>/genders</c> lists
    /// them: 1 male, 2 female, 3 other.
    /// </summary>
    [JsonPropertyName("gender")]
    public int? Gender { get; set; }

    /// <summary>The person's other names, each with its language.</summary>
    [JsonPropertyName("aliases")]
    public List<TvdbAlias>? Aliases { get; set; }

    /// <summary>
    /// The person's biographies, one per language, which may be blank: the
    /// text is more often under <see cref="Translations"/> as overviews.
    /// </summary>
    [JsonPropertyName("biographies")]
    public List<TvdbBiography>? Biographies { get; set; }

    /// <summary>The person's IDs on other sites.</summary>
    [JsonPropertyName("remoteIds")]
    public List<TvdbRemoteID>? RemoteIDs { get; set; }

    /// <summary>The person's name and overview in each language, when asked for.</summary>
    [JsonPropertyName("translations")]
    public TvdbTranslations? Translations { get; set; }

    /// <summary>When TheTVDB last changed the record, e.g. <c>2024-01-02 03:04:05</c>.</summary>
    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }
}

/// <summary>A person's biography in one language.</summary>
public sealed class TvdbBiography
{
    /// <summary>The biography.</summary>
    [JsonPropertyName("biography")]
    public string? Biography { get; set; }

    /// <summary>The three-letter code of the language it is in.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }
}

/// <summary>
/// An entity's names and overviews in every language TheTVDB has, as
/// <c>meta=translations</c> adds them to a record.
/// </summary>
public sealed class TvdbTranslations
{
    /// <summary>
    /// The names, one per language, and on a show also its aliases, each
    /// marked with <see cref="TvdbTranslation.IsAlias"/>.
    /// </summary>
    [JsonPropertyName("nameTranslations")]
    public List<TvdbTranslation>? NameTranslations { get; set; }

    /// <summary>The overviews, one per language.</summary>
    [JsonPropertyName("overviewTranslations")]
    public List<TvdbTranslation>? OverviewTranslations { get; set; }

    /// <summary>
    /// Other names, with no language: the record's own aliases again, without
    /// theirs.
    /// </summary>
    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }
}
