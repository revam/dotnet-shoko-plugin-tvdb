using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Storage;

namespace Shoko.Plugin.Tvdb.Services;

/// <summary>
/// Fetches the own records of the people a refresh credits, when the plugin
/// has none for them or only a stale one.
/// </summary>
/// <remarks>
/// The core's people store stamps a person as updated whenever a refresh
/// writes them, which is every refresh that writes the credits, so its stamp
/// cannot say when a record was fetched. The plugin's own record says that
/// instead. TvDB's own <c>lastUpdated</c> is kept with it, but comparing
/// against it would need the record fetched first, so it does not decide.
/// </remarks>
/// <param name="apiClient">The TvDB client.</param>
/// <param name="store">The plugin's store.</param>
/// <param name="providerManager">The core's provider registry, asked whether the provider's <c>creator</c> kind is on.</param>
/// <param name="configurationProvider">The plugin's configuration.</param>
/// <param name="logger">The logger.</param>
public sealed class TvdbPeopleService(
    TvdbApiClient apiClient,
    TvdbStore store,
    IMetadataProviderManager providerManager,
    ConfigurationProvider<TvdbConfiguration> configurationProvider,
    ILogger<TvdbPeopleService> logger
)
{
    /// <summary>
    /// How old a person's record may be before a refresh fetches it again.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// The records of the people given, fetching the ones missing or stale
    /// first while the provider's <c>creator</c> kind is turned on.
    /// </summary>
    /// <remarks>
    /// Each person is fetched at most once, and no more than the settings'
    /// limit per call: the never fetched first, in the order given, then the
    /// stalest. A person TvDB does not have is recorded as such. A fetch
    /// that fails keeps what was stored and never fails the call; one that
    /// says TvDB will not take more work, such as a refused key, a rate
    /// limit or a server error, also stops the fetching for this call.
    /// </remarks>
    /// <param name="peopleIDs">TvDB person IDs, in credit order.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The records the plugin has, by person ID; a person with none is left out.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="peopleIDs"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IReadOnlyDictionary<int, TvdbStoredPerson>> GetPeople(IEnumerable<int> peopleIDs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peopleIDs);

        var ids = peopleIDs.Where(id => id > 0).Distinct().ToList();
        var people = new Dictionary<int, TvdbStoredPerson>();
        foreach (var id in ids)
            if (store.GetPerson(id) is { } stored)
                people[id] = stored;

        if (!IsCreatorKindOn() || !apiClient.HasApiKey)
            return people;

        var configuration = configurationProvider.Load();
        var now = DateTime.UtcNow;
        var due = ids
            .Where(id => !people.TryGetValue(id, out var stored) || stored.FetchedAt <= now - StaleAfter)
            .OrderBy(id => people.TryGetValue(id, out var stored) ? stored.FetchedAt : DateTime.MinValue)
            .ToList();
        var limit = Math.Max(1, configuration.PersonDetailsLimit);
        if (due.Count > limit)
        {
            logger.LogInformation("{Due} people are due a fetch from TvDB; fetching {Limit} now and the rest on a later refresh.", due.Count, limit);
            due = due[..limit];
        }

        foreach (var id in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TvdbPersonExtended? remote;
            try
            {
                remote = await apiClient.GetPerson(id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TvdbApiException ex) when (IsForThisPersonOnly(ex))
            {
                logger.LogWarning(ex, "Unable to fetch TvDB person {PeopleID}; keeping what their credits say.", id);
                continue;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to fetch TvDB person {PeopleID}; keeping what their credits say and fetching no more people this refresh.", id);
                break;
            }

            var person = remote is null ? TvdbEntityMapper.MissingPerson(id, DateTime.UtcNow) : TvdbEntityMapper.ToStoredPerson(remote, DateTime.UtcNow);
            person.ID = id;
            store.SavePerson(person);
            people[id] = person;
        }

        return people;
    }

    // Whether the core has the provider fetch TvDB's people, the same switch
    // its one-at-a-time refreshes of them follow.
    private bool IsCreatorKindOn()
        => providerManager.MetadataProviders.Any(info => info.Provider is TvdbMetadataProvider && info.EnabledEntityTypes.Contains(MetadataEntityType.Creator));

    // A failure about the one record, rather than about TvDB taking work
    // at all: a client error other than a refusal or a rate limit, or a body
    // that could not be read.
    private static bool IsForThisPersonOnly(TvdbApiException ex)
        => !ex.IsAuthenticationFailure
            && ex.StatusCode is not HttpStatusCode.TooManyRequests
            && (int)ex.StatusCode < 500;
}
