using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System;
using Microsoft.EntityFrameworkCore;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Abstractions.Metadata;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;

namespace Shoko.Plugin.Tvdb.Storage;

/// <summary>
/// Everything the plugin writes: the shows, their seasons and episodes, tags,
/// studios, networks and people in the core's typed stores, and the little
/// only TvDB has in the plugin's own database.
/// </summary>
/// <remarks>
/// Every call opens a context of its own. Writes are serialized, so two
/// refreshes sharing a person or a portrait never race to insert it.
/// </remarks>
public sealed class TvdbStore
{
    private readonly IDbContextFactory<TvdbDbContext> _contexts;

    private readonly Lock _writeLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TvdbStore"/> class.
    /// </summary>
    /// <param name="contexts">Creates contexts on the plugin's own database.</param>
    /// <param name="series">The core's store of series with their seasons and episodes.</param>
    /// <param name="people">The core's store of characters, people and credits.</param>
    /// <param name="tags">The core's store of tags and genres.</param>
    /// <param name="studios">The core's store of studios and networks.</param>
    /// <param name="orderings">The core's store of orderings.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public TvdbStore(
        IDbContextFactory<TvdbDbContext> contexts,
        IMetadataSeriesStore series,
        IMetadataPeopleStore people,
        IMetadataTagStore tags,
        IMetadataStudioStore studios,
        IMetadataOrderingService orderings
    )
    {
        ArgumentNullException.ThrowIfNull(contexts);

        _contexts = contexts;
        Series = series;
        People = people;
        Tags = tags;
        Studios = studios;
        Orderings = orderings;
    }

    #region Typed Stores

    /// <summary>The core's store of series with their seasons and episodes.</summary>
    public IMetadataSeriesStore Series { get; }

    /// <summary>The core's store of characters, people and credits.</summary>
    public IMetadataPeopleStore People { get; }

    /// <summary>The core's store of tags and genres.</summary>
    public IMetadataTagStore Tags { get; }

    /// <summary>The core's store of studios and networks.</summary>
    public IMetadataStudioStore Studios { get; }

    /// <summary>The core's store of orderings.</summary>
    public IMetadataOrderingService Orderings { get; }

    /// <summary>
    /// A stored show, as the core reads it back.
    /// </summary>
    /// <param name="seriesID">TvDB series ID.</param>
    /// <returns>The series, or <see langword="null"/> when it is not stored.</returns>
    public ISeries? GetSeries(int seriesID)
        => seriesID <= 0 ? null : Series.GetSeries(TvdbUtility.SeriesGuid(seriesID));

    /// <summary>
    /// The global orderings the plugin stored for a show.
    /// </summary>
    /// <param name="seriesID">TvDB series ID.</param>
    /// <returns>The orderings.</returns>
    public IReadOnlyList<IOrdering> GetOrderings(int seriesID)
    {
        var series = TvdbUtility.SeriesGuid(seriesID);
        return [.. Orderings.GetStoredOrderings(MetadataSource.Tvdb).Where(ordering => ordering.SeriesID == series)];
    }

    #endregion

    #region Shows

    /// <summary>
    /// What the plugin keeps of one show besides the core's stores.
    /// </summary>
    /// <param name="seriesID">TvDB series ID.</param>
    /// <returns>The record, or <see langword="null"/> when there is none.</returns>
    public TvdbStoredSeries? GetShow(int seriesID)
    {
        if (seriesID <= 0)
            return null;

        using var context = _contexts.CreateDbContext();
        return context.Shows.AsNoTracking().FirstOrDefault(show => show.ID == seriesID);
    }

    /// <summary>
    /// Every show record.
    /// </summary>
    /// <returns>The records, by ID.</returns>
    public IReadOnlyList<TvdbStoredSeries> GetAllShows()
    {
        using var context = _contexts.CreateDbContext();
        return [.. context.Shows.AsNoTracking().OrderBy(show => show.ID)];
    }

    /// <summary>
    /// Stores a show record, replacing what was stored before.
    /// </summary>
    /// <param name="show">The record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="show"/> is <see langword="null"/>.</exception>
    public void SaveShow(TvdbStoredSeries show)
    {
        ArgumentNullException.ThrowIfNull(show);

        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            if (context.Shows.Any(row => row.ID == show.ID))
                context.Shows.Update(show);
            else
                context.Shows.Add(show);
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Forgets what the plugin keeps of a show. The core's stores are the
    /// core's to purge.
    /// </summary>
    /// <param name="seriesID">TvDB series ID.</param>
    /// <returns>Whether there was anything to forget.</returns>
    public bool RemoveShow(int seriesID)
    {
        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            return context.Shows.Where(show => show.ID == seriesID).ExecuteDelete() > 0;
        }
    }

    #endregion

    #region Portraits

    /// <summary>
    /// The key a portrait is filed under: the person's or character's
    /// identifier without its source.
    /// </summary>
    /// <param name="id">The creator or character.</param>
    /// <returns>The key, e.g. <c>creator/123</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    public static string PortraitKey(MetadataGuid id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return $"{id.EntityType.Value}/{id.ID}";
    }

    /// <summary>
    /// The portrait of a creator or a character, when TvDB had one.
    /// </summary>
    /// <param name="id">The creator or character.</param>
    /// <returns>The image's resource ID, or <see langword="null"/>.</returns>
    public string? GetPortrait(MetadataGuid id)
    {
        var key = PortraitKey(id);
        using var context = _contexts.CreateDbContext();
        return context.Portraits.AsNoTracking().Where(portrait => portrait.Key == key).Select(portrait => portrait.Path).FirstOrDefault() is { Length: > 0 } path
            ? path
            : null;
    }

    /// <summary>
    /// Stores the portraits of creators and characters, replacing the ones
    /// stored for them before.
    /// </summary>
    /// <param name="portraits">The portraits, by the creator or character.</param>
    /// <exception cref="ArgumentNullException"><paramref name="portraits"/> is <see langword="null"/>.</exception>
    public void SavePortraits(IEnumerable<KeyValuePair<MetadataGuid, string>> portraits)
    {
        ArgumentNullException.ThrowIfNull(portraits);

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, path) in portraits)
        {
            if (!string.IsNullOrEmpty(path))
                paths[PortraitKey(id)] = path;
        }

        if (paths.Count == 0)
            return;

        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            var keys = paths.Keys.ToList();
            foreach (var existing in context.Portraits.Where(portrait => keys.Contains(portrait.Key)))
            {
                existing.Path = paths[existing.Key];
                paths.Remove(existing.Key);
            }

            context.Portraits.AddRange(paths.Select(pair => new TvdbStoredPortrait { Key = pair.Key, Path = pair.Value }));
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Forgets the portraits of the creators and characters no stored show
    /// credits any more. The core keeps an uncredited person a while before
    /// purging it, and tells no one when it does, so the credits are what
    /// count here.
    /// </summary>
    /// <returns>How many portraits were forgotten.</returns>
    public int RemoveUncreditedPortraits()
    {
        var credited = CreditedKeys();
        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            var gone = context.Portraits.Select(portrait => portrait.Key).AsEnumerable().Where(key => !credited.Contains(key)).ToList();
            return gone.Count == 0 ? 0 : context.Portraits.Where(portrait => gone.Contains(portrait.Key)).ExecuteDelete();
        }
    }

    #endregion

    #region People

    /// <summary>
    /// What the plugin keeps of a person's own record.
    /// </summary>
    /// <param name="peopleID">TvDB person ID.</param>
    /// <returns>The record, or <see langword="null"/> when it was never fetched.</returns>
    public TvdbStoredPerson? GetPerson(int peopleID)
    {
        if (peopleID <= 0)
            return null;

        using var context = _contexts.CreateDbContext();
        return context.People.AsNoTracking().FirstOrDefault(person => person.ID == peopleID);
    }

    /// <summary>
    /// Stores what the plugin keeps of a person's own record, replacing what
    /// was stored before.
    /// </summary>
    /// <param name="person">The record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="person"/> is <see langword="null"/>.</exception>
    public void SavePerson(TvdbStoredPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);

        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            if (context.People.Any(row => row.ID == person.ID))
                context.People.Update(person);
            else
                context.People.Add(person);
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Forgets the records of the people no stored show credits any more,
    /// as <see cref="RemoveUncreditedPortraits"/> does their photos.
    /// </summary>
    /// <returns>How many records were forgotten.</returns>
    public int RemoveUncreditedPeople()
    {
        var credited = CreditedKeys();
        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            var gone = context.People
                .Select(person => person.ID)
                .AsEnumerable()
                .Where(id => !credited.Contains(PortraitKey(TvdbUtility.CreatorGuid(id))))
                .ToList();
            return gone.Count == 0 ? 0 : context.People.Where(person => gone.Contains(person.ID)).ExecuteDelete();
        }
    }

    // The keys of every person and character a stored show credits.
    private HashSet<string> CreditedKeys()
    {
        var credited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var series in Series.GetAllSeries(MetadataSource.Tvdb))
        {
            foreach (var cast in People.GetCast(series.ID))
            {
                if (cast.CreatorID is { } creatorID)
                    credited.Add(PortraitKey(creatorID));
                if (cast.CharacterID is { } characterID)
                    credited.Add(PortraitKey(characterID));
            }

            foreach (var crew in People.GetCrew(series.ID))
                credited.Add(PortraitKey(crew.CreatorID));
        }

        return credited;
    }

    #endregion
}
