using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Shoko.Plugin.Tvdb.Storage;

/// <summary>
/// The plugin's own database: what only TheTVDB has, one table per kind of
/// record. The server configures it and applies its migrations while it
/// starts.
/// </summary>
/// <remarks>
/// Scalars are columns. Lists of small values are JSON columns, as complex
/// collections or, for the lookups by ID, a converted dictionary. Dates are
/// kept and read back in UTC. Nothing here names a provider, so the
/// migrations stay provider-neutral.
/// </remarks>
/// <param name="options">The options the server built.</param>
public class TvdbDbContext(DbContextOptions<TvdbDbContext> options) : DbContext(options)
{
    /// <summary>
    /// The name the database is registered under, and its file name.
    /// </summary>
    public const string DatabaseName = "tvdb";

    /// <summary>What the plugin keeps of each show.</summary>
    public DbSet<TvdbStoredSeries> Shows => Set<TvdbStoredSeries>();

    /// <summary>The portraits of people and characters.</summary>
    public DbSet<TvdbStoredPortrait> Portraits => Set<TvdbStoredPortrait>();

    /// <summary>What the plugin keeps of each person's own record.</summary>
    public DbSet<TvdbStoredPerson> People => Set<TvdbStoredPerson>();

    /// <inheritdoc/>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TvdbStoredSeries>(show =>
        {
            show.ToTable("Shows");
            show.HasKey(row => row.ID);
            show.Property(row => row.ID).ValueGeneratedNever();
            show.Property(row => row.SeasonType).HasMaxLength(64);
            show.ComplexCollection(row => row.Artworks, artwork => artwork.ToJson());
            show.Property(row => row.SeasonPosters).HasConversion(ImagesByID.Converter, ImagesByID.Comparer);
            show.Property(row => row.EpisodeThumbnails).HasConversion(ImagesByID.Converter, ImagesByID.Comparer);
        });

        modelBuilder.Entity<TvdbStoredPortrait>(portrait =>
        {
            portrait.ToTable("Portraits");
            portrait.HasKey(row => row.Key);
            portrait.Property(row => row.Key).HasMaxLength(128);
        });

        modelBuilder.Entity<TvdbStoredPerson>(person =>
        {
            person.ToTable("People");
            person.HasKey(row => row.ID);
            person.Property(row => row.ID).ValueGeneratedNever();
            person.ComplexCollection(row => row.Names, text => text.ToJson());
            person.ComplexCollection(row => row.Biographies, text => text.ToJson());
            person.ComplexCollection(row => row.RemoteIDs, remoteID => remoteID.ToJson());
        });
    }

    // Stores an image-by-ID lookup as a JSON object.
    private static class ImagesByID
    {
        public static readonly ValueConverter<Dictionary<int, string>, string> Converter = new(
            images => JsonSerializer.Serialize(images, (JsonSerializerOptions?)null),
            json => JsonSerializer.Deserialize<Dictionary<int, string>>(json, (JsonSerializerOptions?)null) ?? new Dictionary<int, string>()
        );

        public static readonly ValueComparer<Dictionary<int, string>> Comparer = new(
            (left, right) => left!.Count == right!.Count && !left.Except(right).Any(),
            images => images.Aggregate(0, (hash, pair) => hash ^ pair.Key.GetHashCode() ^ pair.Value.GetHashCode()),
            images => new Dictionary<int, string>(images)
        );
    }
}
