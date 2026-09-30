using System.Text.Json;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Storage;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// What a person's own record adds to what their credits say.
/// </summary>
public class TvdbPersonDetailsTests
{
    private static readonly DateTime _fetchedAt = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    internal static TvdbPersonExtended ReadPerson(int peopleID)
        => JsonSerializer.Deserialize<TvdbResponse<TvdbPersonExtended>>(Fixture.Read($"people-{peopleID}-extended.json"), TvdbJson.Options)!.Data!;

    private static MetadataCreatorData Credited(int peopleID, string name)
    {
        var id = TvdbUtility.FormatID(peopleID);
        return new()
        {
            ID = TvdbUtility.CreatorGuid(peopleID),
            Name = name,
            Resources = [new() { Type = ResourceType.Metadata, Name = "TheTVDB", Url = $"https://thetvdb.com/dereferrer/people/{id}", ID = id }],
        };
    }

    private static MetadataCreatorData Map(int peopleID, string name, IReadOnlyList<string>? languages = null)
        => TvdbEntityMapper.WithPersonDetails(Credited(peopleID, name), TvdbEntityMapper.ToStoredPerson(ReadPerson(peopleID), _fetchedAt), languages ?? ["eng", "jpn"]);

    [Fact]
    public void APersonWithAFullRecord_GetsTheirBirthdayGenderBiographyAndNames()
    {
        var creator = Map(412417, "Mayumi Tanaka");

        Assert.Equal("Mayumi Tanaka", creator.Name);
        Assert.Equal(new FuzzyDateOnly(1955, 1, 15), creator.BirthDay);
        Assert.Null(creator.DeathDay);
        Assert.Equal(PersonGender.Female, creator.Gender);
        Assert.StartsWith("田中 真弓は、日本の声優", creator.Overview);
        Assert.Equal(
            [("en", "Tanaka Mayumi"), ("ja", "たなか まゆみ"), ("ja", "田中真弓"), ("zh-hant", "田中真弓")],
            creator.AlternativeNames.Select(alias => (alias.LanguageCode, alias.Name))
        );
    }

    [Fact]
    public void APersonWithAFullRecord_KeepsTheDereferrerAndGainsTheirPageAndIDsElsewhere()
    {
        var creator = Map(412417, "Mayumi Tanaka");

        Assert.Equal(
            [
                "https://thetvdb.com/dereferrer/people/412417",
                "https://thetvdb.com/people/412417-mayumi-tanaka",
                "https://www.imdb.com/name/nm0849028/",
                "https://www.themoviedb.org/person/65510",
            ],
            creator.Resources.Select(resource => resource.Url)
        );
        Assert.Contains(creator.Resources, resource => resource is { Name: "IMDb", ID: "nm0849028", Type: ResourceType.CrossReference });
        Assert.Contains(creator.Resources, resource => resource is { Name: "TMDB", ID: "65510", Type: ResourceType.CrossReference });
    }

    [Fact]
    public void TheBiography_IsInTheFirstConfiguredLanguageThatHasOne()
    {
        // TheTVDB's English biography for her is blank, so English is passed
        // over for the first that has one.
        Assert.StartsWith("Mayumi Tanaka (15 de enero de 1955)", Map(412417, "Mayumi Tanaka", ["spa", "jpn"]).Overview);
        Assert.StartsWith("田中 真弓は", Map(412417, "Mayumi Tanaka", ["fra"]).Overview);
        Assert.Equal("Eine Beispielperson.", Map(700001, "Example Person").Overview);
    }

    [Fact]
    public void APersonKnownOnlyByTheirBirthYear_GetsTheYear()
    {
        var creator = Map(602, "Kazuya Nakai");

        Assert.Equal(new FuzzyDateOnly(1967), creator.BirthDay);
        Assert.Null(creator.DeathDay);
        Assert.Equal(PersonGender.Male, creator.Gender);
        Assert.Null(creator.Overview);
        Assert.Equal([("ja", "中井和哉")], creator.AlternativeNames.Select(alias => (alias.LanguageCode, alias.Name)));
    }

    [Fact]
    public void APersonKnownOnlyByTheirBirthYear_LinksOnlyTheIDsThatNameAPerson()
    {
        var creator = Map(602, "Kazuya Nakai");

        // A title's IMDb ID is dropped, a TMDB ID without its site's name is
        // read from its type, and a slug with the ID already in front is not
        // given it twice.
        Assert.Equal(
            [
                "https://thetvdb.com/dereferrer/people/602",
                "https://thetvdb.com/people/602-kazuya-nakai",
                "https://www.themoviedb.org/person/54321",
            ],
            creator.Resources.Select(resource => resource.Url)
        );
    }

    [Fact]
    public void ADeadPerson_GetsTheirDeathAndWhatIsKnownOfTheirBirth()
    {
        var creator = Map(700001, "Example Person");

        Assert.Equal(new FuzzyDateOnly(1930, 4), creator.BirthDay);
        Assert.Equal(new FuzzyDateOnly(2001, 11, 23), creator.DeathDay);
        Assert.Equal(PersonGender.NonBinary, creator.Gender);
    }

    [Fact]
    public void APersonTheTvdbDoesNotHave_KeepsWhatTheCreditsSay()
    {
        var credited = Credited(604, "Eiichiro Oda");

        Assert.Same(credited, TvdbEntityMapper.WithPersonDetails(credited, TvdbEntityMapper.MissingPerson(604, _fetchedAt)));
        Assert.Same(credited, TvdbEntityMapper.WithPersonDetails(credited, null));
    }

    [Fact]
    public void TheCreditedName_IsKeptAsAnotherNameWhenTheRecordNamesThePersonOtherwise()
    {
        var creator = TvdbEntityMapper.WithPersonDetails(Credited(412417, "M. Tanaka"), TvdbEntityMapper.ToStoredPerson(ReadPerson(412417), _fetchedAt));

        Assert.Equal("Mayumi Tanaka", creator.Name);
        Assert.Contains(creator.AlternativeNames, alias => alias is { Name: "M. Tanaka", LanguageCode: null });
    }

    [Fact]
    public void ANameRepeatedWithoutItsLanguage_IsKeptOnlyWithIt()
    {
        var person = new TvdbPersonExtended
        {
            ID = 412417,
            Name = "Mayumi Tanaka",
            Aliases = [new() { Language = "eng", Name = "Tanaka Mayumi" }, new() { Language = "jpn", Name = "たなか まゆみ" }],
            Translations = new() { Aliases = ["Tanaka Mayumi", "たなか まゆみ", "Mayu"] },
        };

        var creator = TvdbEntityMapper.WithPersonDetails(Credited(412417, "M. Tanaka"), TvdbEntityMapper.ToStoredPerson(person, _fetchedAt));

        Assert.Equal(
            [("en", "Tanaka Mayumi"), ("ja", "たなか まゆみ"), (null, "M. Tanaka"), (null, "Mayu")],
            creator.AlternativeNames.Select(alias => (alias.LanguageCode, alias.Name)).OrderBy(alias => alias.LanguageCode is null).ThenBy(alias => alias.LanguageCode, StringComparer.Ordinal).ThenBy(alias => alias.Name, StringComparer.Ordinal)
        );
    }

    [Fact]
    public void TheLanguagelessAliases_AreReadFromTheTranslations()
    {
        var person = JsonSerializer.Deserialize<TvdbPersonExtended>("""{ "id": 412417, "name": "Mayumi Tanaka", "translations": { "aliases": ["Mayu"] } }""", TvdbJson.Options)!;

        Assert.Equal(["Mayu"], person.Translations?.Aliases);
        Assert.Contains(TvdbEntityMapper.ToStoredPerson(person, _fetchedAt).Names, name => name is { Language: null, Value: "Mayu" });
    }

    [Fact]
    public void TheStoredRecord_KeepsTheRawDatesAndWhenItWasFetched()
    {
        var person = TvdbEntityMapper.ToStoredPerson(ReadPerson(412417), _fetchedAt);

        Assert.True(person.Found);
        Assert.Equal("1955-01-15", person.Birth);
        Assert.Null(person.Death);
        Assert.Equal("2024-08-18 07:14:30", person.LastUpdated);
        Assert.Equal(_fetchedAt, person.FetchedAt);
        Assert.Equal("person/412417/primary.jpg", person.Image);
        Assert.Equal("412417-mayumi-tanaka", person.Slug);
    }

    [Theory]
    [InlineData(16, null, "nm0849028", "https://www.imdb.com/name/nm0849028/")]
    [InlineData(15, null, "65510", "https://www.themoviedb.org/person/65510")]
    [InlineData(21, null, "55875", "https://www.tvmaze.com/people/55875")]
    [InlineData(18, null, "Q1234567", "https://www.wikidata.org/wiki/Q1234567")]
    [InlineData(null, "IMDB", "nm0849028", "https://www.imdb.com/name/nm0849028/")]
    [InlineData(null, "TheMovieDB.com", "65510", "https://www.themoviedb.org/person/65510")]
    [InlineData(null, "TV Maze", "55875", "https://www.tvmaze.com/people/55875")]
    [InlineData(6, "X (Twitter)", "tanaka_mayumi", "https://x.com/tanaka_mayumi")]
    [InlineData(null, "X (Twitter)", "tanaka_mayumi", "https://x.com/tanaka_mayumi")]
    [InlineData(2, "IMDB", "tt0388629", null)]
    [InlineData(12, "TheMovieDB.com", "37854", null)]
    [InlineData(19, "TV Maze", "1505", null)]
    [InlineData(14, "EIDR", "10.5240/0000-0000-0000-0000-0000-X", null)]
    [InlineData(99, null, "12345", null)]
    public void APersonsIDElsewhere_IsReadByTheTvdbsNumberForTheSite(int? type, string? sourceName, string id, string? expected)
        => Assert.Equal(expected, TvdbEntityMapper.ToPersonResource(new() { ID = id, Type = type, SourceName = sourceName })?.Url);

    [Theory]
    [InlineData("1955-01-15", 1955, 1, 15)]
    [InlineData("1955-01-15T00:00:00Z", 1955, 1, 15)]
    [InlineData("1955-01-15 12:30:00", 1955, 1, 15)]
    [InlineData("1955-01", 1955, 1, null)]
    [InlineData("1955", 1955, null, null)]
    [InlineData("1955-00-00", 1955, null, null)]
    [InlineData("1955-02-30", 1955, 2, null)]
    [InlineData("1956-02-29", 1956, 2, 29)]
    [InlineData("0000-05-12", null, 5, 12)]
    [InlineData("1955-99999999999-01", 1955, null, null)]
    [InlineData("1955-01-99999999999", 1955, 1, null)]
    public void APartialDate_KeepsTheKnownParts(string value, int? year, int? month, int? day)
        => Assert.Equal(new FuzzyDateOnly(year, month, day), TvdbEntityMapper.ParseFuzzyDate(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0000-00-00")]
    [InlineData("unknown")]
    [InlineData("55-01-15")]
    [InlineData("1955-01-15-01")]
    [InlineData("1955--15")]
    public void AnUnreadableDate_IsNoDate(string? value)
        => Assert.Null(TvdbEntityMapper.ParseFuzzyDate(value));

    [Theory]
    [InlineData(null, PersonGender.Unknown)]
    [InlineData(0, PersonGender.Unknown)]
    [InlineData(1, PersonGender.Male)]
    [InlineData(2, PersonGender.Female)]
    [InlineData(3, PersonGender.NonBinary)]
    [InlineData(9, PersonGender.Unknown)]
    public void TheGender_IsReadFromTheTvdbsNumber(int? gender, PersonGender expected)
        => Assert.Equal(expected, TvdbEntityMapper.ToPersonGender(gender));
}
