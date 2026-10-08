using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;

namespace Shoko.Plugin.Tvdb.Api;

/// <summary>
/// Thin wrapper around the TvDB v4 API
/// (<see href="https://api4.thetvdb.com/v4"/>).
/// </summary>
/// <remarks>
/// Unlike Fanart.tv, TvDB does not take the key on every request. It trades
/// the key (and the subscriber PIN, when one is set) for a bearer
/// token at <c>POST /v4/login</c>, and the token is what every later request
/// carries. The token is good for roughly a month, so it is cached here rather
/// than fetched per call, and re-fetched exactly once when a request comes back
/// 401. One login is in flight at a time: a burst of parallel requests after an
/// expiry would otherwise each start their own.
/// <para>
/// Only "not found" answers with nothing. A request that could not be asked,
/// because TvDB refused the key, or a listing that lost a page partway
/// through, throws, so a refresh never writes half an answer as the whole.
/// </para>
/// </remarks>
public sealed class TvdbApiClient(
    HttpClient httpClient,
    TvdbRateLimiter rateLimiter,
    ConfigurationProvider<TvdbConfiguration> configurationProvider,
    ILogger<TvdbApiClient> logger
) : IDisposable
{
    private const int MaximumRateLimitRetries = 3;

    /// <summary>
    /// How long the plugin's work is suspended after TvDB answered with a
    /// server error.
    /// </summary>
    internal static readonly TimeSpan ServerErrorPause = TimeSpan.FromMinutes(1);

    /// <summary>Why the plugin is not configured while no API key is available.</summary>
    internal const string NoApiKeyReason = "No TvDB API key is configured.";

    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private string? _token;

    /// <summary>
    /// The credentials the cached token was obtained with. A user changing
    /// either in the settings has to invalidate the token, and comparing is
    /// cheaper and less error-prone than subscribing to the saved event.
    /// </summary>
    private (string ApiKey, string? Pin) _tokenCredentials;

    /// <summary>
    /// Whether an API key is available at all. Nothing can be fetched without
    /// one.
    /// </summary>
    public bool HasApiKey => ResolveApiKey(configurationProvider.Load()) is not null;

    /// <summary>
    /// The rate limiter every request goes through, which also reports the
    /// plugin's suspensions and keeps the key TvDB refused.
    /// </summary>
    public TvdbRateLimiter RateLimiter => rateLimiter;

    /// <summary>
    /// Resolves the API key to send, preferring the configured one over the
    /// key an official build was stamped with.
    /// </summary>
    /// <param name="configuration">The loaded configuration.</param>
    /// <returns>The key, or <see langword="null"/> when neither is available.</returns>
    private static string? ResolveApiKey(TvdbConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.ApiKey))
            return configuration.ApiKey;

        // CI rewrites `Constants.ProjectApiKey` for official builds, so in the
        // tree this comparison is between two equal literals and the compiler
        // sees the second branch as unreachable. It is not, once stamped.
#pragma warning disable CS0162 // Unreachable code detected
        return Constants.ProjectApiKey != "TVDB_PROJECT_KEY_GOES_HERE" ? Constants.ProjectApiKey : null;
#pragma warning restore CS0162 // Unreachable code detected
    }

    #region Endpoints

    /// <summary>
    /// Gets the full record for a show, with its names and overviews in every
    /// language TvDB has.
    /// </summary>
    /// <param name="seriesID">TvDB series id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The show, or <see langword="null"/> when TvDB does not know the id or
    /// no API key is configured.
    /// </returns>
    /// <exception cref="TvdbApiException">
    /// Thrown when TvDB answers with anything other than success or
    /// "not found", or refused the key.
    /// </exception>
    public Task<TvdbSeriesExtended?> GetSeries(int seriesID, CancellationToken cancellationToken = default)
        => Get<TvdbSeriesExtended>($"series/{seriesID.ToString(CultureInfo.InvariantCulture)}/extended?meta=translations", cancellationToken);

    /// <summary>
    /// Gets every episode of a show in one of TvDB's orderings, following
    /// the paging links to the end.
    /// </summary>
    /// <param name="seriesID">TvDB series id.</param>
    /// <param name="seasonType">The ordering, e.g. <c>default</c> or <c>absolute</c>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// Every episode across every page, or an empty list when TvDB does not
    /// know the id or no API key is configured.
    /// </returns>
    /// <exception cref="TvdbApiException">
    /// Thrown when TvDB answers with anything other than success or
    /// "not found", refused the key, or lost a page after the first.
    /// </exception>
    public Task<IReadOnlyList<TvdbEpisode>> GetEpisodes(int seriesID, string seasonType, CancellationToken cancellationToken = default)
        => GetEpisodePages($"series/{seriesID.ToString(CultureInfo.InvariantCulture)}/episodes/{Uri.EscapeDataString(seasonType)}", cancellationToken);

    /// <summary>
    /// Gets every episode of a show in one of TvDB's orderings with its
    /// name and overview in one language, following the paging links to the
    /// end.
    /// </summary>
    /// <param name="seriesID">TvDB series id.</param>
    /// <param name="seasonType">The ordering, e.g. <c>default</c> or <c>absolute</c>.</param>
    /// <param name="language">A three-letter language code.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// Every episode across every page, or an empty list when TvDB does not
    /// know the id or has no translations in the language.
    /// </returns>
    /// <exception cref="TvdbApiException">
    /// Thrown when TvDB answers with anything other than success or
    /// "not found", refused the key, or lost a page after the first.
    /// </exception>
    public Task<IReadOnlyList<TvdbEpisode>> GetTranslatedEpisodes(int seriesID, string seasonType, string language, CancellationToken cancellationToken = default)
        => GetEpisodePages(
            $"series/{seriesID.ToString(CultureInfo.InvariantCulture)}/episodes/{Uri.EscapeDataString(seasonType)}/{Uri.EscapeDataString(language)}",
            cancellationToken
        );

    /// <summary>
    /// Gets the full record for one episode.
    /// </summary>
    /// <param name="episodeID">TvDB episode id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The episode, or <see langword="null"/> when it is not known.</returns>
    /// <exception cref="TvdbApiException">
    /// Thrown when TvDB answers with anything other than success or
    /// "not found", or refused the key.
    /// </exception>
    public Task<TvdbEpisode?> GetEpisode(int episodeID, CancellationToken cancellationToken = default)
        => Get<TvdbEpisode>($"episodes/{episodeID.ToString(CultureInfo.InvariantCulture)}/extended", cancellationToken);

    /// <summary>
    /// Gets a person's own record, with their names and overviews in every
    /// language TvDB has.
    /// </summary>
    /// <param name="peopleID">TvDB person ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The person, or <see langword="null"/> when TvDB does not know the ID
    /// or no API key is configured.
    /// </returns>
    /// <exception cref="TvdbApiException">
    /// Thrown when TvDB answers with anything other than success or
    /// "not found", or refused the key.
    /// </exception>
    public Task<TvdbPersonExtended?> GetPerson(int peopleID, CancellationToken cancellationToken = default)
        => Get<TvdbPersonExtended>($"people/{peopleID.ToString(CultureInfo.InvariantCulture)}/extended?meta=translations", cancellationToken);

    /// <summary>
    /// Gets a character's own record: the credit of the role on its show.
    /// </summary>
    /// <param name="characterID">TvDB character ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The character, or <see langword="null"/> when TvDB does not know the
    /// ID or no API key is configured.
    /// </returns>
    /// <exception cref="TvdbApiException">
    /// Thrown when TvDB answers with anything other than success or
    /// "not found", or refused the key.
    /// </exception>
    public Task<TvdbCharacter?> GetCharacter(long characterID, CancellationToken cancellationToken = default)
        => Get<TvdbCharacter>($"characters/{characterID.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets a company's own record, a studio's or a network's alike.
    /// </summary>
    /// <param name="companyID">TvDB company ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The company, or <see langword="null"/> when TvDB does not know the ID
    /// or no API key is configured.
    /// </returns>
    /// <exception cref="TvdbApiException">
    /// Thrown when TvDB answers with anything other than success or
    /// "not found", or refused the key.
    /// </exception>
    public Task<TvdbCompany?> GetCompany(int companyID, CancellationToken cancellationToken = default)
        => Get<TvdbCompany>($"companies/{companyID.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    /// <summary>
    /// Gets an episode's translated name and overview in one language.
    /// </summary>
    /// <param name="episodeID">TvDB episode id.</param>
    /// <param name="language">A three-letter language code.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The translation, or <see langword="null"/> when there is none in that
    /// language.
    /// </returns>
    /// <exception cref="TvdbApiException">
    /// Thrown when TvDB answers with anything other than success or
    /// "not found", or refused the key.
    /// </exception>
    public Task<TvdbTranslation?> GetEpisodeTranslation(int episodeID, string language, CancellationToken cancellationToken = default)
        => Get<TvdbTranslation>($"episodes/{episodeID.ToString(CultureInfo.InvariantCulture)}/translations/{Uri.EscapeDataString(language)}", cancellationToken);

    /// <summary>
    /// Searches TvDB for shows matching a query.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="limit">The maximum number of results to ask for.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The hits, newest-relevance first, or an empty list.</returns>
    /// <exception cref="TvdbApiException">
    /// Thrown when TvDB answers with anything other than success or
    /// "not found", or refused the key.
    /// </exception>
    public async Task<IReadOnlyList<TvdbSearchResult>> SearchSeries(string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var path = $"search?query={Uri.EscapeDataString(query)}&type=series&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        return await Get<List<TvdbSearchResult>>(path, cancellationToken).ConfigureAwait(false) ?? [];
    }

    #endregion

    #region Plumbing

    private async Task<T?> Get<T>(string path, CancellationToken cancellationToken)
        => (await GetEnvelope<T>(path, cancellationToken).ConfigureAwait(false)) is { } envelope ? envelope.Data : default;

    /// <summary>
    /// Follows a listing's paging links to the end.
    /// </summary>
    /// <remarks>
    /// Only the first page may be missing, for a show TvDB does not know.
    /// One missing after it means the listing changed or broke under the
    /// request, and is thrown rather than taken as its end.
    /// </remarks>
    /// <param name="path">The listing, without the page.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Every episode across every page.</returns>
    /// <exception cref="TvdbApiException">A page after the first is missing, or TvDB failed.</exception>
    private async Task<IReadOnlyList<TvdbEpisode>> GetEpisodePages(string path, CancellationToken cancellationToken)
    {
        List<TvdbEpisode> episodes = [];
        // Bounded rather than "until `next` is null": a server that keeps
        // handing back the same link would otherwise spin forever, and no show
        // has five hundred pages of episodes.
        for (var page = 0; page < 500; page++)
        {
            var pagePath = $"{path}?page={page.ToString(CultureInfo.InvariantCulture)}";
            var response = await GetEnvelope<TvdbEpisodesData>(pagePath, cancellationToken).ConfigureAwait(false);
            if (response is null && page > 0)
                throw new TvdbApiException(HttpStatusCode.NotFound, $"TvDB lost /{pagePath} partway through the listing.");

            if (response?.Data is not { } data || data.Episodes is not { Count: > 0 } pageEpisodes)
                break;

            episodes.AddRange(pageEpisodes);
            if (string.IsNullOrEmpty(response.Links?.Next))
                break;
        }

        return episodes;
    }

    private async Task<TvdbResponse<T>?> GetEnvelope<T>(string path, CancellationToken cancellationToken)
    {
        // Without a key there is nobody to ask, which every caller checks for
        // first. A key TvDB refused is another matter: the answer is not
        // "nothing", it is "could not ask".
        if (!HasApiKey)
            return null;

        if (await ResolveToken(forceRefresh: false, cancellationToken).ConfigureAwait(false) is null)
            throw RefusedCredentials(path);

        for (var attempt = 0; ; attempt++)
        {
            using var response = await Send(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                // TvDB answers 404 for an id it does not know, which is a
                // normal answer rather than an error: plenty of anime has no
                // TvDB entry at all.
                case HttpStatusCode.NotFound:
                    return null;

                // The token expired, was revoked, or the subscription lapsed.
                // Worth exactly one re-login: if the fresh token is refused too,
                // the credentials are wrong and retrying only burns requests.
                case HttpStatusCode.Unauthorized when attempt is 0:
                    logger.LogInformation("TvDB refused the cached token for /{Path}; logging in again.", path);
                    if (await ResolveToken(forceRefresh: true, cancellationToken).ConfigureAwait(false) is null)
                        throw RefusedCredentials(path);
                    continue;

                case HttpStatusCode.TooManyRequests when attempt < MaximumRateLimitRetries:
                    var delay = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                        ?? TimeSpan.FromSeconds(1);
                    if (delay < TimeSpan.Zero)
                        delay = TimeSpan.FromSeconds(1);
                    logger.LogWarning("TvDB rate limited the request for /{Path}; retrying in {Delay}.", path, delay);
                    rateLimiter.NotifyRateLimited(delay);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;

                case var status when (int)status >= 500:
                    rateLimiter.NotifyServerError(ServerErrorPause);
                    throw new TvdbApiException(status, $"TvDB answered {((int)status).ToString(CultureInfo.InvariantCulture)} ({status}) for /{path}.");

                case var status when !response.IsSuccessStatusCode:
                    throw new TvdbApiException(status, $"TvDB answered {((int)status).ToString(CultureInfo.InvariantCulture)} ({status}) for /{path}.");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonSerializer.Deserialize<TvdbResponse<T>>(content, TvdbJson.Options);
            }
            catch (JsonException ex)
            {
                throw new TvdbApiException(response.StatusCode, $"TvDB answered /{path} with a body that could not be parsed.", ex);
            }
        }
    }

    private static TvdbApiException RefusedCredentials(string path)
        => new(HttpStatusCode.Unauthorized, $"TvDB refused the API key or subscriber PIN, so /{path} could not be fetched.");

    /// <summary>
    /// Returns a usable bearer token, logging in when there is none cached,
    /// when the credentials changed under it, or when the caller was just
    /// refused with one.
    /// </summary>
    private async Task<string?> ResolveToken(bool forceRefresh, CancellationToken cancellationToken)
    {
        var configuration = configurationProvider.Load();
        if (ResolveApiKey(configuration) is not { } apiKey)
            return null;

        var pin = string.IsNullOrWhiteSpace(configuration.SubscriberPin) ? null : configuration.SubscriberPin;
        var credentials = (apiKey, pin);

        // Credentials TvDB refused are not tried again until they change or
        // the suspension is lifted; new ones resume it.
        if (rateLimiter.RefusedCredentials is { } refused)
        {
            if (refused == credentials)
                return null;

            rateLimiter.ForgetRefusedCredentials();
        }

        var cached = _token;
        if (!forceRefresh && cached is not null && _tokenCredentials == credentials)
            return cached;

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Somebody else may have logged in while this call waited for the
            // lock, in which case their token is as good as one of our own.
            if (_token is { } current && _tokenCredentials == credentials && !(forceRefresh && ReferenceEquals(current, cached)))
                return current;

            var token = await LoginWithFallback(apiKey, pin, cancellationToken).ConfigureAwait(false);
            if (token is null)
                rateLimiter.RefuseCredentials(credentials);

            _token = token;
            _tokenCredentials = credentials;
            return token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>
    /// Logs in with the key and PIN, and when TvDB refuses that, once more
    /// with the key alone, so a wrong PIN never stops the plugin.
    /// </summary>
    /// <param name="apiKey">The API key.</param>
    /// <param name="pin">The subscriber PIN, or <c>null</c> when none is set.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The bearer token, or <c>null</c> when TvDB refused the key.</returns>
    private async Task<string?> LoginWithFallback(string apiKey, string? pin, CancellationToken cancellationToken)
    {
        if (pin is not null && rateLimiter.RefusedPin != (apiKey, pin))
        {
            var (token, refused) = await Login(apiKey, pin, cancellationToken).ConfigureAwait(false);
            if (!refused)
                return token;

            logger.LogWarning("TvDB refused the subscriber PIN. Logging in without it; check the PIN in the TvDB plugin's settings.");
            rateLimiter.RefusedPin = (apiKey, pin);
        }

        var (keyOnlyToken, keyRefused) = await Login(apiKey, null, cancellationToken).ConfigureAwait(false);
        if (keyRefused)
            logger.LogError("TvDB refused the API key. Check the key and, for a user-supported key, the subscriber PIN.");

        return keyOnlyToken;
    }

    /// <summary>
    /// Sends one login request.
    /// </summary>
    /// <param name="apiKey">The API key.</param>
    /// <param name="pin">The subscriber PIN to send, or <c>null</c> to leave it out.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The bearer token, if any, and whether TvDB refused the credentials.</returns>
    /// <exception cref="TvdbApiException">Thrown when TvDB answers with anything other than success or a refusal.</exception>
    private async Task<(string? Token, bool Refused)> Login(string apiKey, string? pin, CancellationToken cancellationToken)
    {
        await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "login")
            {
                Content = JsonContent.Create(new TvdbLoginRequest { ApiKey = apiKey, Pin = pin }, options: TvdbJson.Options),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Logged by the caller rather than thrown; the request that
                // asked throws instead, and the suspension the refusal raises
                // holds back the provider's jobs until the key is changed or
                // the suspension is lifted.
                return (null, true);
            }

            if (!response.IsSuccessStatusCode)
                throw new TvdbApiException(response.StatusCode, $"TvDB answered {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} ({response.StatusCode}) to the login request.");

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            TvdbResponse<TvdbLoginData>? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<TvdbResponse<TvdbLoginData>>(content, TvdbJson.Options);
            }
            catch (JsonException ex)
            {
                throw new TvdbApiException(response.StatusCode, "TvDB answered the login request with a body that could not be parsed.", ex);
            }

            if (envelope?.Data?.Token is not { Length: > 0 } token)
            {
                logger.LogError("TvDB accepted the login request but returned no token.");
                return (null, false);
            }

            return (token, false);
        }
        finally
        {
            rateLimiter.Release();
        }
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (_token is { } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            rateLimiter.Release();
        }
    }

    #endregion

    /// <inheritdoc/>
    public void Dispose() => _loginLock.Dispose();
}

/// <summary>
/// Thrown when TvDB answers a request with something the client cannot make
/// sense of.
/// </summary>
public sealed class TvdbApiException : Exception
{
    /// <summary>
    /// The status code TvDB answered with.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Whether the status code says the credentials are missing, wrong or not
    /// allowed, which no amount of retrying will fix.
    /// </summary>
    public bool IsAuthenticationFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvdbApiException"/> class.
    /// </summary>
    /// <param name="statusCode">The status code TvDB answered with.</param>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public TvdbApiException(HttpStatusCode statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
        => StatusCode = statusCode;
}
