using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Config;

namespace Shoko.Plugin.Tvdb;

/// <summary>
/// Configuration for the TvDB metadata plugin.
/// </summary>
/// <remarks>
/// <para>
///   Nothing here is marked <c>[Required]</c> on purpose. A required-but-unset
///   property fails configuration validation, which stops the whole plugin from
///   loading, so the missing credential is checked for at runtime instead: the
///   plugin stays loaded, the core keeps reading what is already stored, and
///   the provider reports itself paused, so the core holds its jobs back.
/// </para>
/// <para>
///   There is no switch here for whether the provider answers either. That
///   belongs to the core now:
///   <see cref="Shoko.Abstractions.Metadata.Services.IMetadataProviderManager"/>
///   turns a provider on and off per source and per entity type, and a second
///   switch of the plugin's own would only be a way for the two to disagree.
/// </para>
/// </remarks>
[Display(Name = "TvDB")]
public class TvdbConfiguration : IConfiguration
{
    #region Credentials

    /// <summary>
    /// A TvDB v4 project key, sent as <c>apikey</c> to <c>POST /v4/login</c>,
    /// for a build from source or a fork. An official build ships a licensed
    /// key and needs nothing here; one set here wins over it.
    /// </summary>
    [DataType(DataType.Password)]
    [Display(Name = "API Key", Description = "Leave empty on an official build, which ships its own key. For a build from source or a fork: the TvDB project key registered for it at thetvdb.com/api-information.")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// A TvDB subscriber PIN, sent as <c>pin</c> alongside the API key.
    /// Only a user-supported key needs one, since it authenticates as the
    /// subscriber whose PIN comes with it; a licensed key, as official builds
    /// ship, needs none.
    /// </summary>
    [DataType(DataType.Password)]
    [Display(Name = "Subscriber PIN", Description = "Optional. Only needed with a user-supported API key; leave empty on an official build. The PIN is on your TvDB account's dashboard.")]
    public string? SubscriberPin { get; set; }

    #endregion

    #region Behaviour

    /// <summary>
    /// How many search results to consider per title when auto-linking a
    /// series. Only the best one is linked; the rest are handed back turned
    /// down, with why.
    /// </summary>
    [Display(Name = "Search Result Limit")]
    [Range(1, 50)]
    [DefaultValue(10)]
    public int SearchResultLimit { get; set; } = 10;

    /// <summary>
    /// Whether TvDB episodes already linked to another AniDB anime are left
    /// out when episodes are matched automatically.
    /// </summary>
    [Display(Name = "Consider Existing Other Links", Description = "Leave out TvDB episodes another anime is already linked to when matching episodes automatically.")]
    public bool ConsiderExistingOtherLinks { get; set; }

    #endregion

    #region Downloads

    /// <summary>
    /// Whether to store a show's names in every language its record carries,
    /// rather than only in the core's series title language order.
    /// </summary>
    /// <remarks>
    /// The record carries them all, so this costs no request. An episode's
    /// names always follow the core's episode title language order, since
    /// each language costs one request per page of the show's episodes.
    /// </remarks>
    [Display(Name = "Download All Titles", Description = "Store a show's names in every language TvDB has, not only in the server's series title language order. Episode names always follow the episode title language order.")]
    public bool DownloadAllTitles { get; set; }

    /// <summary>
    /// Whether to store a show's overviews in every language its record
    /// carries, rather than only in the core's description language order.
    /// </summary>
    /// <remarks>
    /// The record carries them all, so this costs no request. An episode's
    /// overviews always follow the core's description language order, since
    /// each language costs one request per page of the show's episodes.
    /// </remarks>
    [Display(Name = "Download All Overviews", Description = "Store a show's overviews in every language TvDB has, not only in the server's description language order. Episode overviews always follow that order.")]
    public bool DownloadAllOverviews { get; set; }

    /// <summary>
    /// Whether to store a show's other season types, such as its DVD or
    /// absolute order, as orderings a user can pick for the series. A refresh
    /// asked for with its own choice follows that instead.
    /// </summary>
    /// <remarks>
    /// The show's own seasons are always the season type TvDB uses for it
    /// by default. Each other season type costs one request per page of its
    /// episodes.
    /// </remarks>
    [Display(Name = "Download Alternate Orderings", Description = "Store the other TvDB season types of a show, such as its DVD or absolute order, as orderings.")]
    [DefaultValue(true)]
    public bool AutoDownloadAlternateOrderings { get; set; } = true;

    /// <summary>
    /// Whether to store a show's cast and crew. A refresh asked for with its
    /// own choice follows that instead.
    /// </summary>
    [Display(Name = "Download Cast and Crew")]
    [DefaultValue(true)]
    public bool AutoDownloadCastAndCrew { get; set; } = true;

    /// <summary>
    /// Whether to store the networks a show aired on. A refresh asked for with
    /// its own choice follows that instead.
    /// </summary>
    [Display(Name = "Download Networks")]
    [DefaultValue(true)]
    public bool AutoDownloadNetworks { get; set; } = true;

    /// <summary>
    /// Whether to fetch the own record of each person the cast and crew
    /// credit, for what the credits leave out: their birth and death, gender,
    /// biography, other names and IDs elsewhere.
    /// </summary>
    /// <remarks>
    /// One request per person, only for one never fetched or fetched more
    /// than 30 days ago, and shared across every show they are credited on.
    /// Turned off, what was fetched before is still written.
    /// </remarks>
    [Display(Name = "Download Person Details", Description = "Fetch each credited person's own record for their birth and death dates, gender, biography, other names and IDs elsewhere. Costs one request per person not fetched in the last 30 days, shared across every show they are in.")]
    [DefaultValue(true)]
    public bool AutoDownloadPersonDetails { get; set; } = true;

    /// <summary>
    /// The most people whose own records one refresh fetches, so a show with
    /// hundreds of credits does not send hundreds of requests at once. The
    /// rest are fetched on later refreshes.
    /// </summary>
    [Display(Name = "Person Details Per Refresh", Description = "The most people whose details one refresh fetches. The rest are fetched on later refreshes.")]
    [Range(1, 1000)]
    [DefaultValue(50)]
    public int PersonDetailsLimit { get; set; } = 50;

    #endregion
}
