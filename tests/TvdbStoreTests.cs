using Microsoft.EntityFrameworkCore;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Storage;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The plugin's own database: the shows, the portraits and the people, read
/// back from an in-memory SQLite database migrated as the server migrates it.
/// </summary>
public sealed class TvdbStoreTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
        => _database.Dispose();

    private (TvdbStore Store, FakePeopleStore People) CreateStore()
    {
        var people = new FakePeopleStore();
        var tags = new FakeTagStore();
        var studios = new FakeStudioStore();
        var series = new FakeSeriesStore(tags, studios, people);
        return (new TvdbStore(_database, series, people, tags, studios, new FakeOrderingService(series)), people);
    }

    [Fact]
    public void AShow_IsSavedReadAndForgotten()
    {
        var (store, _) = CreateStore();

        store.SaveShow(new() { ID = 81797, Slug = "one-piece" });

        Assert.Equal("one-piece", store.GetShow(81797)?.Slug);
        Assert.Single(store.GetAllShows());
        Assert.True(store.RemoveShow(81797));
        Assert.Null(store.GetShow(81797));
        Assert.False(store.RemoveShow(81797));
    }

    [Fact]
    public void ANonPositiveID_IsNoShow()
        => Assert.Null(CreateStore().Store.GetShow(0));

    [Fact]
    public void Portraits_AreKeptByThePersonOrCharacter()
    {
        var (store, _) = CreateStore();

        store.SavePortraits([new(TvdbUtility.CreatorGuid(412417), "person/412417.jpg"), new(TvdbUtility.CharacterGuid(65111900), string.Empty)]);

        Assert.Equal("creator/412417", TvdbStore.PortraitKey(TvdbUtility.CreatorGuid(412417)));
        Assert.Equal("person/412417.jpg", store.GetPortrait(TvdbUtility.CreatorGuid(412417)));
        Assert.Null(store.GetPortrait(TvdbUtility.CharacterGuid(65111900)));
    }

    [Fact]
    public void ThePortraitsOfPeopleNoStoredShowCredits_AreForgotten()
    {
        var (store, people) = CreateStore();
        var show = TvdbUtility.SeriesGuid(81797);
        var gone = TvdbUtility.SeriesGuid(81798);
        store.Series.SaveSeries(new() { ID = show, Titles = [], Overviews = [], Seasons = [], Episodes = [] });
        people.SaveCreators(
        [
            new MetadataCreatorData { ID = TvdbUtility.CreatorGuid(412417), Name = "Mayumi Tanaka" },
            new MetadataCreatorData { ID = TvdbUtility.CreatorGuid(602), Name = "Eiichiro Oda" },
            new MetadataCreatorData { ID = TvdbUtility.CreatorGuid(603), Name = "Kazuya Nakai" },
        ]);
        people.SaveCharacters([new MetadataCharacterData { ID = TvdbUtility.CharacterGuid(65111900), Name = "Monkey D. Luffy" }]);
        people.SetCast(show, [new MetadataCastData { CreatorID = TvdbUtility.CreatorGuid(412417), CharacterID = TvdbUtility.CharacterGuid(65111900), Name = "Monkey D. Luffy" }]);
        people.SetCrew(show, [new MetadataCrewData { CreatorID = TvdbUtility.CreatorGuid(602), Name = "Original Creator" }]);
        people.SetCrew(gone, [new MetadataCrewData { CreatorID = TvdbUtility.CreatorGuid(603), Name = "Director" }]);
        store.SavePortraits(
        [
            new(TvdbUtility.CreatorGuid(412417), "person/412417.jpg"),
            new(TvdbUtility.CreatorGuid(602), "person/602.jpg"),
            new(TvdbUtility.CreatorGuid(603), "person/603.jpg"),
            new(TvdbUtility.CharacterGuid(65111900), "characters/65111900.jpg"),
        ]);

        Assert.Equal(1, store.RemoveUncreditedPortraits());

        Assert.NotNull(store.GetPortrait(TvdbUtility.CreatorGuid(412417)));
        Assert.NotNull(store.GetPortrait(TvdbUtility.CreatorGuid(602)));
        Assert.NotNull(store.GetPortrait(TvdbUtility.CharacterGuid(65111900)));
        Assert.Null(store.GetPortrait(TvdbUtility.CreatorGuid(603)));
    }

    [Fact]
    public void TheMigrationsMatchTheModel()
    {
        using var context = _database.CreateDbContext();

        Assert.Empty(context.Database.GetPendingMigrations());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void AShow_KeepsItsListsAndLookups_AndIsReplacedWhole()
    {
        var (store, _) = CreateStore();
        store.SaveShow(new()
        {
            ID = 81797,
            Slug = "one-piece",
            SeasonType = "official",
            AlternateSeasonTypes = ["dvd", "absolute"],
            Artworks = [new() { ID = 1, Type = 2, Path = "posters/81797-1.jpg", Language = "eng", Width = 680, Height = 1000 }, new() { ID = 2, Type = 7, Path = "seasons/81797-1.jpg", SeasonID = 31893 }],
            SeasonPosters = { [31893] = "seasons/81797-1.jpg" },
            EpisodeThumbnails = { [361887] = "episodes/81797/361887.jpg" },
            FetchedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
        });

        var show = store.GetShow(81797);
        Assert.NotNull(show);
        Assert.Equal(["dvd", "absolute"], show.AlternateSeasonTypes);
        Assert.Equal(2, show.Artworks.Count);
        Assert.Equal(31893, show.Artworks[1].SeasonID);
        Assert.Equal(680, show.Artworks[0].Width);
        Assert.Equal("seasons/81797-1.jpg", show.SeasonPosters[31893]);
        Assert.Equal("episodes/81797/361887.jpg", show.EpisodeThumbnails[361887]);
        Assert.Equal(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), show.FetchedAt);
        Assert.Equal(DateTimeKind.Utc, show.FetchedAt.Kind);

        store.SaveShow(new() { ID = 81797, Slug = "one-piece", SeasonType = "dvd" });

        show = store.GetShow(81797);
        Assert.NotNull(show);
        Assert.Equal("dvd", show.SeasonType);
        Assert.Empty(show.AlternateSeasonTypes);
        Assert.Empty(show.Artworks);
        Assert.Empty(show.SeasonPosters);
        Assert.Empty(show.EpisodeThumbnails);
    }

    [Fact]
    public void APortrait_IsReplacedByALaterOne()
    {
        var (store, _) = CreateStore();

        store.SavePortraits([new(TvdbUtility.CreatorGuid(412417), "person/old.jpg")]);
        store.SavePortraits([new(TvdbUtility.CreatorGuid(412417), "person/new.jpg"), new(TvdbUtility.CreatorGuid(602), "person/602.jpg")]);

        Assert.Equal("person/new.jpg", store.GetPortrait(TvdbUtility.CreatorGuid(412417)));
        Assert.Equal("person/602.jpg", store.GetPortrait(TvdbUtility.CreatorGuid(602)));
    }

    [Fact]
    public async Task Refreshes_SharingPortraits_DoNotRace()
    {
        var (store, _) = CreateStore();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            store.SavePortraits(Enumerable.Range(1, 50).Select(id => new KeyValuePair<Shoko.Abstractions.Metadata.MetadataGuid, string>(TvdbUtility.CreatorGuid(id), $"person/{id}-{index}.jpg"))))));

        Assert.StartsWith("person/1-", store.GetPortrait(TvdbUtility.CreatorGuid(1)));
    }

    [Fact]
    public void APerson_IsSavedReadReplacedAndForgottenOnceUncredited()
    {
        var (store, people) = CreateStore();
        var show = TvdbUtility.SeriesGuid(81797);
        store.Series.SaveSeries(new() { ID = show, Titles = [], Overviews = [], Seasons = [], Episodes = [] });
        people.SaveCreators([new MetadataCreatorData { ID = TvdbUtility.CreatorGuid(412417), Name = "Mayumi Tanaka" }]);
        people.SetCrew(show, [new MetadataCrewData { CreatorID = TvdbUtility.CreatorGuid(412417), Name = "Voice" }]);

        store.SavePerson(new()
        {
            ID = 412417,
            Found = true,
            Name = "Mayumi Tanaka",
            Names = [new() { Language = "jpn", Value = "田中真弓" }],
            Biographies = [new() { Language = "eng", Value = "A voice actress." }],
            RemoteIDs = [new() { ID = "nm0848695", Type = 2, SourceName = "IMDB" }],
            FetchedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        store.SavePerson(new() { ID = 603, Found = false });

        var person = store.GetPerson(412417);
        Assert.NotNull(person);
        Assert.Equal("田中真弓", Assert.Single(person.Names).Value);
        Assert.Equal("eng", Assert.Single(person.Biographies).Language);
        Assert.Equal("IMDB", Assert.Single(person.RemoteIDs).SourceName);

        store.SavePerson(new() { ID = 412417, Found = true, Name = "Mayumi Tanaka" });
        Assert.Empty(store.GetPerson(412417)!.Names);

        Assert.Equal(1, store.RemoveUncreditedPeople());
        Assert.NotNull(store.GetPerson(412417));
        Assert.Null(store.GetPerson(603));
        Assert.Null(store.GetPerson(0));
    }
}
