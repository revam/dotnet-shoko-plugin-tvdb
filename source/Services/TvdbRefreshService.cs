using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Storage;

namespace Shoko.Plugin.Tvdb.Services;

/// <summary>
/// The half of the plugin that fetches a show from TvDB and writes it
/// into the core's stores.
/// </summary>
/// <remarks>
/// The core's refresh job calls in through the provider. It has already
/// decided the show is due and holds its lock, so nothing here checks
/// freshness or locks anything.
/// </remarks>
/// <param name="apiClient">The TvDB client.</param>
/// <param name="store">The plugin's store.</param>
/// <param name="linkingService">Matches the linked anime's episodes again.</param>
/// <param name="peopleService">Fetches the people's own records.</param>
/// <param name="textManager">The core's text manager, whose language order picks the translations kept.</param>
/// <param name="configurationProvider">The plugin's configuration.</param>
/// <param name="logger">The logger.</param>
public sealed class TvdbRefreshService(
    TvdbApiClient apiClient,
    TvdbStore store,
    TvdbLinkingService linkingService,
    TvdbPeopleService peopleService,
    IMetadataTextManager textManager,
    ConfigurationProvider<TvdbConfiguration> configurationProvider,
    ILogger<TvdbRefreshService> logger
)
{
    /// <summary>
    /// Fetches a show and everything the settings and options ask for, and
    /// writes it into the stores.
    /// </summary>
    /// <remarks>
    /// The show, its seasons, episodes, titles, overviews, tags, genres,
    /// studios, networks and content ratings are always written, and the cast
    /// and crew unless the refresh is quick; the core fetches the people,
    /// characters, studios and networks they name for the kinds turned on.
    /// The other season types as orderings follow the options, or the
    /// settings where the options leave it open; a quick refresh leaves out
    /// the cast and crew, the orderings and the matching of the linked
    /// anime's episodes. The people credited are written with what their own
    /// records add, fetching the ones missing or stale while the
    /// <c>creator</c> kind is on; a person whose record could not be fetched
    /// keeps what the credits say, and never fails the refresh. A show TvDB no longer has is left as it was
    /// stored, and so is one TvDB lists no episodes for while some are
    /// stored, which is more likely a hiccup than a show that lost them all:
    /// the refresh fails instead, so the core tries again later.
    /// </remarks>
    /// <param name="seriesID">TvDB series ID.</param>
    /// <param name="options">What kind of refresh it is.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether TvDB had the show.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="MetadataProviderNotConfiguredException">No API key is configured.</exception>
    /// <exception cref="TvdbApiException">
    /// TvDB refused the key, failed, answered with something unexpected,
    /// or listed no episodes for a show with episodes stored.
    /// </exception>
    public async Task<bool> RefreshSeries(int seriesID, MetadataRefreshOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (seriesID <= 0)
            return false;

        if (!apiClient.HasApiKey)
            throw new MetadataProviderNotConfiguredException(MetadataSource.Tvdb, TvdbApiClient.NoApiKeyReason);

        logger.LogInformation("Refreshing TvDB series {SeriesID}.", seriesID);
        var configuration = configurationProvider.Load();
        if (await apiClient.GetSeries(seriesID, cancellationToken).ConfigureAwait(false) is not { } remote)
        {
            logger.LogWarning("TvDB has no series with ID {SeriesID}. Keeping what is stored.", seriesID);
            return false;
        }

        // The show's record carries its names and overviews in every
        // language; its episodes are fetched in each language the core's
        // episode title and description orders name but the show's own,
        // their names already being in it.
        var languages = TvdbTextLanguages.From(configuration, textManager, remote.OriginalLanguage);
        var existingEpisodes = store.GetSeries(seriesID)?.Episodes.Select(episode => episode.ID).ToHashSet() ?? [];
        var episodes = await apiClient.GetEpisodes(seriesID, "default", cancellationToken).ConfigureAwait(false);
        if (episodes.Count is 0 && existingEpisodes.Count > 0)
        {
            throw new TvdbApiException(
                HttpStatusCode.NotFound,
                $"TvDB listed no episodes for series {seriesID.ToString(CultureInfo.InvariantCulture)}, which has {existingEpisodes.Count.ToString(CultureInfo.InvariantCulture)} stored. Keeping what is stored."
            );
        }

        var episodeTranslations = new Dictionary<string, IReadOnlyList<TvdbEpisode>>(StringComparer.Ordinal);
        foreach (var language in languages.EpisodeLanguages.Where(language => !string.Equals(language, remote.OriginalLanguage?.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var translated = await apiClient.GetTranslatedEpisodes(seriesID, "default", language, cancellationToken).ConfigureAwait(false);
            if (translated.Count > 0)
                episodeTranslations[language] = translated;
        }

        var seriesGuid = TvdbUtility.SeriesGuid(seriesID);
        var data = TvdbEntityMapper.ToSeriesData(remote, languages, episodes, episodeTranslations);
        // The core drops the links to any episode the save removed.
        var changes = store.Series.SaveSeries(data);

        // What the other stores hold for it.
        var (tags, entryTags) = TvdbEntityMapper.Tags(remote);
        store.Tags.SaveTags(tags);
        store.Tags.SetTags(seriesGuid, entryTags);
        var (studios, entryStudios) = TvdbEntityMapper.Studios(remote);
        store.Studios.SaveStudios(studios);
        store.Studios.SetStudios(seriesGuid, entryStudios);
        store.SaveCompanies(TvdbEntityMapper.Companies(remote));
        var networks = TvdbEntityMapper.Networks(remote);
        store.Studios.SaveNetworks(networks);
        store.Studios.SetNetworks(seriesGuid, [.. networks.Select(network => network.ID)]);
        if (!options.QuickRefresh)
        {
            var people = TvdbEntityMapper.People(remote);
            var details = await peopleService.GetPeople(PeopleIDs(people), cancellationToken).ConfigureAwait(false);
            var creators = new List<MetadataCreatorData>(people.Creators.Count);
            foreach (var (creatorID, creator) in people.Creators)
            {
                var person = TvdbUtility.TryGetID(creatorID, MetadataEntityType.Creator, out var peopleID) ? details.GetValueOrDefault(peopleID) : null;
                creators.Add(TvdbEntityMapper.WithPersonDetails(creator, person, languages.Overviews));
                if (person?.Image is { Length: > 0 } image)
                    people.Portraits.TryAdd(creatorID, image);
            }

            store.People.SaveCreators(creators);
            store.People.SaveCharacters(people.Characters.Values);
            store.People.SetCast(seriesGuid, people.Cast);
            store.People.SetCrew(seriesGuid, people.Crew);

            // After the credits, which are what keeps a portrait from being
            // forgotten when another show is purged.
            store.SavePortraits(people.Portraits);
        }

        // The other season types, as orderings of the show.
        var previous = store.GetShow(seriesID);
        var alternateSeasonTypes = previous?.AlternateSeasonTypes ?? [];
        if (!options.QuickRefresh && (options.DownloadAlternateOrdering ?? configuration.AutoDownloadAlternateOrderings))
            alternateSeasonTypes = await UpdateOrderings(remote, [.. data.Episodes.Select(episode => episode.ID)], cancellationToken).ConfigureAwait(false);

        store.SaveShow(TvdbEntityMapper.ToStoredSeries(remote, episodes, alternateSeasonTypes, DateTime.UtcNow));
        if (!options.QuickRefresh)
            await linkingService.MatchLinkedEpisodes(seriesID, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Refreshed TvDB series {SeriesID} ({Title}): {Changes} changes to the series and its {Seasons} seasons and {Episodes} episodes.",
            seriesID,
            remote.Name,
            changes,
            data.Seasons.Count,
            data.Episodes.Count
        );
        return true;
    }

    /// <summary>
    /// Fetches one person, character, studio or network on its own and
    /// writes it into the stores.
    /// </summary>
    /// <remarks>
    /// A person's own record is kept as the people service keeps it, so a
    /// show's refresh writes them with it again; one TvDB does not have is
    /// recorded as such. A character's page needs its show's slug, which is
    /// the stored show's. A company's slug is kept for its page.
    /// </remarks>
    /// <param name="entityID">The creator, character, studio or network, on the TvDB source.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether TvDB had the entry; any other ID is not had.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entityID"/> is <see langword="null"/>.</exception>
    /// <exception cref="MetadataProviderNotConfiguredException">No API key is configured.</exception>
    /// <exception cref="TvdbApiException">TvDB refused the key, failed or answered with something unexpected.</exception>
    public async Task<bool> RefreshEntity(MetadataGuid entityID, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityID);

        if (!apiClient.HasApiKey)
            throw new MetadataProviderNotConfiguredException(MetadataSource.Tvdb, TvdbApiClient.NoApiKeyReason);

        bool found;
        if (TvdbUtility.TryGetID(entityID, MetadataEntityType.Creator, out var peopleID))
            found = await RefreshPerson(peopleID, cancellationToken).ConfigureAwait(false);
        else if (entityID.Source == MetadataSource.Tvdb && entityID.EntityType == MetadataEntityType.Character && entityID.TryGetNumericID<long>(out var characterID) && characterID > 0)
            found = await RefreshCharacter(characterID, cancellationToken).ConfigureAwait(false);
        else if (TvdbUtility.TryGetID(entityID, MetadataEntityType.Studio, out var studioID))
            found = await RefreshCompany(studioID, network: false, cancellationToken).ConfigureAwait(false);
        else if (TvdbUtility.TryGetID(entityID, MetadataEntityType.Network, out var networkID))
            found = await RefreshCompany(networkID, network: true, cancellationToken).ConfigureAwait(false);
        else
            return false;

        if (found)
            logger.LogDebug("Refreshed TvDB entry {EntityID}.", entityID);

        return found;
    }

    /// <summary>
    /// Fetches a person's own record, keeps it and writes the person.
    /// </summary>
    /// <param name="peopleID">TvDB person ID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether TvDB had the person.</returns>
    private async Task<bool> RefreshPerson(int peopleID, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (await apiClient.GetPerson(peopleID, cancellationToken).ConfigureAwait(false) is not { } remote)
        {
            store.SavePerson(TvdbEntityMapper.MissingPerson(peopleID, now));
            return false;
        }

        var person = TvdbEntityMapper.ToStoredPerson(remote, now);
        person.ID = peopleID;
        store.SavePerson(person);
        var creator = TvdbEntityMapper.ToCreatorData(person, TvdbUtility.ToTvdbLanguageCodes(textManager.GetLanguageOrder(TextKind.Overview), null));
        store.People.SaveCreators([creator]);
        if (person.Image is { Length: > 0 } image)
            store.SavePortraits([new(creator.ID, image)]);

        return true;
    }

    /// <summary>
    /// Fetches a character's record and writes the character.
    /// </summary>
    /// <param name="characterID">TvDB character ID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether TvDB had the character, with a name.</returns>
    private async Task<bool> RefreshCharacter(long characterID, CancellationToken cancellationToken)
    {
        if (await apiClient.GetCharacter(characterID, cancellationToken).ConfigureAwait(false) is not { } remote)
            return false;

        // The record answers for the ID asked for, whatever it says.
        remote.ID = characterID;
        var slug = remote.SeriesID is { } seriesID ? store.GetShow(seriesID)?.Slug : null;
        if (TvdbEntityMapper.ToCharacterData(remote, slug) is not { } character)
            return false;

        store.People.SaveCharacters([character]);
        if (TvdbImages.ToResourceID(remote.Image) is { } image)
            store.SavePortraits([new(character.ID, image)]);

        return true;
    }

    /// <summary>
    /// Fetches a company's record, keeps its slug and writes it as a studio
    /// or a network.
    /// </summary>
    /// <param name="companyID">TvDB company ID.</param>
    /// <param name="network">Whether to write it as a network rather than a studio.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether TvDB had the company, with a name.</returns>
    private async Task<bool> RefreshCompany(int companyID, bool network, CancellationToken cancellationToken)
    {
        if (await apiClient.GetCompany(companyID, cancellationToken).ConfigureAwait(false) is not { } remote || string.IsNullOrWhiteSpace(remote.Name))
            return false;

        remote.ID = companyID;
        if (TvdbEntityMapper.ToStoredCompany(remote) is { } company)
            store.SaveCompanies([company]);

        if (network)
            store.Studios.SaveNetworks([TvdbEntityMapper.ToNetworkData(remote)]);
        else
            store.Studios.SaveStudios([TvdbEntityMapper.ToStudioData(remote)]);

        return true;
    }

    private static IEnumerable<int> PeopleIDs(TvdbPeople people)
        => people.Creators.Keys
            .Select(creatorID => TvdbUtility.TryGetID(creatorID, MetadataEntityType.Creator, out var peopleID) ? peopleID : 0)
            .Where(peopleID => peopleID > 0);

    /// <summary>
    /// Stores each of a show's other season types as an ordering of it, and
    /// removes the orderings of season types it no longer has.
    /// </summary>
    /// <remarks>
    /// A season type the show still has keeps its ordering even when none of
    /// its episodes could be placed this time; only one gone from the show's
    /// seasons is removed.
    /// </remarks>
    /// <param name="series">The show.</param>
    /// <param name="storedEpisodes">The episodes stored under the show.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The season types stored as orderings.</returns>
    private async Task<List<string>> UpdateOrderings(TvdbSeriesExtended series, IReadOnlyList<MetadataGuid> storedEpisodes, CancellationToken cancellationToken)
    {
        var episodeIDs = storedEpisodes
            .Select(episode => TvdbUtility.TryGetID(episode, MetadataEntityType.Episode, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
        var defaultSeasonType = TvdbEntityMapper.DefaultSeasonType(series);
        var stored = new List<string>();
        var kept = new HashSet<MetadataGuid>();
        foreach (var seasonType in TvdbEntityMapper.AlternateSeasonTypes(series, defaultSeasonType))
        {
            var orderingID = TvdbUtility.OrderingGuid(series.ID, seasonType);
            kept.Add(orderingID);
            var episodes = await apiClient.GetEpisodes(series.ID, seasonType, cancellationToken).ConfigureAwait(false);
            if (TvdbEntityMapper.ToOrderingData(series, seasonType, episodes, episodeIDs) is not { } ordering)
            {
                if (store.GetOrderings(series.ID).Any(existing => existing.ID == orderingID))
                    stored.Add(seasonType);
                continue;
            }

            try
            {
                store.Orderings.SaveOrdering(ordering);
                stored.Add(seasonType);
            }
            catch (ArgumentException ex)
            {
                // One season type the core will not take is no reason to fail
                // the refresh; whatever was stored for it stays.
                logger.LogWarning(ex, "Unable to store the {SeasonType} order of TvDB series {SeriesID}.", seasonType, series.ID);
            }
        }

        foreach (var ordering in store.GetOrderings(series.ID).Where(ordering => !kept.Contains(ordering.ID)))
        {
            logger.LogInformation("Removing ordering {OrderingID}, which TvDB no longer has.", ordering.ID);
            store.Orderings.RemoveOrdering(ordering.ID);
        }

        return stored;
    }
}
