using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Events;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Services;
using Shoko.Plugin.Tvdb.Storage;

namespace Shoko.Plugin.Tvdb.Metadata;

/// <summary>
/// Supplies TvDB shows with their seasons and episodes to Shoko, under the
/// <c>MetadataSource.Tvdb</c> source.
/// </summary>
/// <remarks>
/// <para>
///   Series-shaped only: TvDB's films are a database of their own, which
///   the plugin does not read.
/// </para>
/// <para>
///   The core runs the refresh, search, image and purge jobs and calls in
///   here; the provider fetches from TvDB and writes into the core's
///   stores, and the core reads the shows back from them. Without an API key
///   the provider says it is not configured. While TvDB refused the key,
///   rate limits the plugin or failed, it says it is paused, and the core
///   holds its jobs back.
/// </para>
/// </remarks>
public sealed class TvdbMetadataProvider : IMetadataSeriesLinkingProvider, IMetadataAutoLinkingProvider, IMetadataImageProvider, IPausableMetadataProvider, IMetadataProvider<TvdbConfiguration>, IDisposable
{
    private readonly TvdbRefreshService _refreshService;

    private readonly TvdbSearchService _searchService;

    private readonly TvdbLinkingService _linkingService;

    private readonly TvdbImageService _imageService;

    private readonly TvdbStore _store;

    private readonly TvdbApiClient _apiClient;

    private readonly ConfigurationProvider<TvdbConfiguration> _configurationProvider;

    private readonly IMetadataService _metadataService;

    private readonly ILogger<TvdbMetadataProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvdbMetadataProvider"/> class.
    /// </summary>
    /// <param name="refreshService">Fetches a show and writes it into the stores.</param>
    /// <param name="searchService">Searches TvDB.</param>
    /// <param name="linkingService">Matches episodes and records links.</param>
    /// <param name="imageService">Hands out the images.</param>
    /// <param name="store">The plugin's store, cleaned up after a purge.</param>
    /// <param name="apiClient">The TvDB client, whose key makes up the configuration and whose rate limiter makes up the pause.</param>
    /// <param name="configurationProvider">The plugin's configuration, watched for a new key.</param>
    /// <param name="metadataService">The core's metadata service, for the AniDB anime to link.</param>
    /// <param name="logger">The logger.</param>
    public TvdbMetadataProvider(
        TvdbRefreshService refreshService,
        TvdbSearchService searchService,
        TvdbLinkingService linkingService,
        TvdbImageService imageService,
        TvdbStore store,
        TvdbApiClient apiClient,
        ConfigurationProvider<TvdbConfiguration> configurationProvider,
        IMetadataService metadataService,
        ILogger<TvdbMetadataProvider> logger
    )
    {
        _refreshService = refreshService;
        _searchService = searchService;
        _linkingService = linkingService;
        _imageService = imageService;
        _store = store;
        _apiClient = apiClient;
        _configurationProvider = configurationProvider;
        _metadataService = metadataService;
        _logger = logger;
        _apiClient.RateLimiter.PauseStateChanged += OnPauseStateChanged;
        _configurationProvider.Saved += OnConfigurationSaved;
    }

    #region Provider

    /// <inheritdoc/>
    public string Name => "TvDB";

    /// <inheritdoc/>
    public string? Description => "TvDB, a community database of television, film and animation.";

    /// <inheritdoc/>
    public MetadataSource Source => MetadataSource.Tvdb;

    /// <summary>
    /// Four of each job at once. Every request is paced by the rate limiter
    /// whatever runs it, so this only keeps a library-wide refresh from
    /// crowding out the rest of the queue.
    /// </summary>
    public int? MaxConcurrentJobs => 4;

    /// <summary>
    /// Configured while an API key is available, which only a build from
    /// source can lack; the core skips auto-linking without one.
    /// </summary>
    public bool IsConfigured => _apiClient.HasApiKey;

    /// <summary>
    /// Says no API key is configured, while <see cref="IsConfigured"/> is
    /// <see langword="false"/>.
    /// </summary>
    public string? NotConfiguredReason => _apiClient.HasApiKey ? null : TvdbApiClient.NoApiKeyReason;

    /// <summary>
    /// A show and its episodes. A season is not one of them: pointing an
    /// anime at a season means pointing its episodes there, which
    /// <see cref="MatchEpisodes"/> does.
    /// </summary>
    public IReadOnlySet<MetadataEntityType> LinkableEntityTypes { get; } = FrozenSet.ToFrozenSet([MetadataEntityType.Series, MetadataEntityType.Episode]);

    /// <summary>
    /// Forgets what the plugin keeps of a purged show besides the core's
    /// stores: its record and the portraits of people no stored show
    /// credits any more. The core removes the show's orderings itself.
    /// </summary>
    /// <param name="entryID">The purged show.</param>
    /// <param name="cancellationToken">Unused; the work is local.</param>
    /// <returns>A task that completes once it is forgotten.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entryID"/> is <see langword="null"/>.</exception>
    public Task CleanUp(MetadataGuid entryID, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryID);

        if (!TvdbUtility.TryGetID(entryID, MetadataEntityType.Series, out var seriesID))
            return Task.CompletedTask;

        _logger.LogInformation("Cleaning up after TvDB series {SeriesID}.", seriesID);
        _store.RemoveShow(seriesID);
        _store.RemoveUncreditedPortraits();
        _store.RemoveUncreditedPeople();
        return Task.CompletedTask;
    }

    #endregion

    #region Pausing

    /// <summary>
    /// Paused while the rate limiter holds a pause: after TvDB rate limited
    /// the plugin, answered with a server error or refused the key. A missing
    /// key is not a pause but <see cref="IsConfigured"/>.
    /// </summary>
    public MetadataProviderPauseStatus PauseStatus
    {
        get
        {
            var rateLimiter = _apiClient.RateLimiter;
            if (rateLimiter.ResumesAt is { } resumesAt && rateLimiter.PauseReason is { } reason)
                return new() { IsPaused = true, Reason = reason, ResumesAt = resumesAt.UtcDateTime };

            return MetadataProviderPauseStatus.NotPaused;
        }
    }

    /// <inheritdoc/>
    public event EventHandler? PauseStatusChanged;

    private void OnPauseStateChanged(object? sender, EventArgs eventArgs)
        => PauseStatusChanged?.Invoke(this, EventArgs.Empty);

    // A saved configuration may replace a key TvDB refused, which lifts
    // the pause the refusal set; lifting it raises the change.
    private void OnConfigurationSaved(object? sender, ConfigurationSavedEventArgs<TvdbConfiguration> eventArgs)
    {
        if (string.Equals(_apiClient.RateLimiter.PauseReason, TvdbApiClient.RefusedCredentialsReason, StringComparison.Ordinal))
            _apiClient.RateLimiter.Resume();
    }

    #endregion

    #region Refresh

    /// <summary>
    /// Fetches a show from TvDB and writes it, its seasons and episodes,
    /// its orderings and what the other stores hold for it.
    /// </summary>
    /// <param name="seriesID">The show.</param>
    /// <param name="options">What kind of refresh it is.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the show is written.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="MetadataProviderNotConfiguredException">No API key is configured.</exception>
    /// <exception cref="TvdbApiException">
    /// TvDB refused the key, failed, answered with something unexpected,
    /// or listed no episodes for a show with episodes stored.
    /// </exception>
    public async Task RefreshSeries(MetadataGuid seriesID, MetadataRefreshOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seriesID);
        ArgumentNullException.ThrowIfNull(options);

        if (!TvdbUtility.TryGetID(seriesID, MetadataEntityType.Series, out var tvdbSeriesID))
            return;

        await _refreshService.RefreshSeries(tvdbSeriesID, options, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Images

    /// <inheritdoc/>
    public Task<IReadOnlyList<ImageCandidate>?> GetImages(MetadataGuid entityID, CancellationToken cancellationToken = default)
        => Task.FromResult(_imageService.GetImages(entityID));

    #endregion

    #region Search & Matching

    /// <inheritdoc/>
    /// <exception cref="MetadataProviderUnavailableException">TvDB failed or is rate limiting the plugin.</exception>
    public Task<(IReadOnlyList<MetadataSeriesSearchResult> Page, int TotalCount)> SearchSeries(MetadataSearchOptions options, CancellationToken cancellationToken = default)
        => Upstream(() => _searchService.Search(options, cancellationToken));

    /// <summary>
    /// Works out which TvDB episodes an anime's episodes line up with,
    /// through the core's matcher, without writing anything.
    /// </summary>
    /// <remarks>
    /// The show has to be stored already; one that is not has no episodes to
    /// match against. A season may be one of the show's own or a group of one
    /// of its orderings; one of another show is refused with nothing. No link
    /// can name an episode TvDB no longer lists, since the core removes
    /// those links when a refresh saves the show without it.
    /// </remarks>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="anidbEpisodes">The episodes in scope.</param>
    /// <param name="providerSeriesID">The show.</param>
    /// <param name="providerSeasonID">One season or ordering group of it, or <see langword="null"/> for all of it.</param>
    /// <param name="existing">The links to honour.</param>
    /// <param name="considerOtherLinks">Whether to leave out the episodes other anime are linked to; <see langword="null"/> follows the settings.</param>
    /// <param name="cancellationToken">Unused; the matching is local.</param>
    /// <returns>
    /// One match per AniDB episode in scope, including the ones nothing
    /// matched, but for an episode an existing link settles outside the
    /// candidates (a person's link to nothing, or a link to an episode not
    /// among them), which is left out so that saving leaves its link alone.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="anime"/>, <paramref name="anidbEpisodes"/> or <paramref name="providerSeriesID"/> is <see langword="null"/>.</exception>
    public Task<IReadOnlyList<EpisodeMatch>> MatchEpisodes(
        IAnidbAnime anime,
        IReadOnlyList<IAnidbEpisode> anidbEpisodes,
        MetadataGuid providerSeriesID,
        MetadataGuid? providerSeasonID = null,
        IReadOnlyList<IMetadataEpisodeCrossReference>? existing = null,
        bool? considerOtherLinks = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(anime);
        ArgumentNullException.ThrowIfNull(anidbEpisodes);
        ArgumentNullException.ThrowIfNull(providerSeriesID);

        if (!TvdbUtility.TryGetID(providerSeriesID, MetadataEntityType.Series, out var seriesID))
            return Task.FromResult<IReadOnlyList<EpisodeMatch>>([]);

        return Task.FromResult(_linkingService.Match(anime, anidbEpisodes, seriesID, providerSeasonID, existing, considerOtherLinks));
    }

    /// <summary>
    /// Searches TvDB for the anime and hands back every show found, judged
    /// by the core's matching engine, and the shows the anime's links on
    /// other sources name, without linking anything.
    /// </summary>
    /// <remarks>
    /// The core links the one taken, which matches its episodes, and turns
    /// the rest down with the reason given and the title that found them. It
    /// takes a hint, as <see cref="MetadataAutoLinkOrigin.CrossSourceLink"/>,
    /// only when the search took nothing it competes with, or only picks
    /// rated below it. What decides a show is in
    /// <see cref="TvdbSearchService.FindAutoLinks"/>.
    /// </remarks>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>
    /// The shows, best first, then the hints, or nothing without an API key,
    /// for an anime not stored, or when TvDB found nothing.
    /// </returns>
    /// <exception cref="MetadataProviderUnavailableException">TvDB failed or is rate limiting the plugin.</exception>
    /// <exception cref="TvdbApiException">TvDB refused the key or answered with something unexpected.</exception>
    public async Task<IReadOnlyList<MetadataAutoLinkCandidate>> FindAutoLinks(int anidbAnimeID, CancellationToken cancellationToken = default)
    {
        if (anidbAnimeID <= 0 || !_apiClient.HasApiKey)
            return [];

        var anime = _metadataService.GetSeries(new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, TvdbUtility.FormatID(anidbAnimeID))) as IAnidbAnime
            ?? _metadataService.GetShokoSeriesByAnidbID(anidbAnimeID)?.AnidbAnime;
        if (anime is null)
        {
            _logger.LogWarning("AniDB anime {AnimeID} is not available locally.", anidbAnimeID);
            return [];
        }

        return await Upstream(() => _searchService.FindAutoLinks(anime, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a request to TvDB, telling the core it cannot be reached for
    /// now when it failed or rate limits the plugin, so the core tries again
    /// later.
    /// </summary>
    /// <typeparam name="T">What the request answers.</typeparam>
    /// <param name="request">The request.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="MetadataProviderUnavailableException">TvDB failed, timed out or is rate limiting the plugin.</exception>
    private async Task<T> Upstream<T>(Func<Task<T>> request)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (TvdbApiException ex) when ((int)ex.StatusCode >= 500 || ex.StatusCode is HttpStatusCode.TooManyRequests)
        {
            throw Unavailable(ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable(ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw Unavailable(ex);
        }
    }

    private MetadataProviderUnavailableException Unavailable(Exception ex)
        => new(
            MetadataSource.Tvdb,
            ex.Message,
            _apiClient.RateLimiter.ResumesAt is { } resumesAt && resumesAt > DateTimeOffset.UtcNow ? resumesAt - DateTimeOffset.UtcNow : null,
            ex
        );

    #endregion

    /// <summary>
    /// Stops listening for pauses and configuration changes.
    /// </summary>
    public void Dispose()
    {
        _apiClient.RateLimiter.PauseStateChanged -= OnPauseStateChanged;
        _configurationProvider.Saved -= OnConfigurationSaved;
    }
}
