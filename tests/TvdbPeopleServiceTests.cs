using System.Net;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Services;
using Shoko.Plugin.Tvdb.Storage;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// When a refresh fetches the people it credits, and what it makes of the
/// answer.
/// </summary>
public class TvdbPeopleServiceTests
{
    private static RoutingHttpMessageHandler OnePieceWithPeople()
        => RoutingHttpMessageHandler.OnePiece()
            .Route("people/412417/extended?meta=translations", Fixture.Read("people-412417-extended.json"))
            .Route("people/602/extended?meta=translations", Fixture.Read("people-602-extended.json"))
            .Route("people/605/extended?meta=translations", Fixture.Read("people-700001-extended.json"));

    private static List<string> PeoplePaths(ServiceHarness harness)
        => [.. harness.Http.Paths.Where(path => path.StartsWith("people/", StringComparison.Ordinal))];

    private static ICreator Creator(ServiceHarness harness, int peopleID)
        => harness.People.GetCreator(TvdbUtility.CreatorGuid(peopleID))!;

    [Fact]
    public async Task ARefresh_FetchesEachCreditedPersonOnceInCreditOrder()
    {
        using var harness = new ServiceHarness(http: OnePieceWithPeople());

        await harness.Refresh();

        // Eiichiro Oda (604) is credited twice, and the guest star (603) only
        // on an episode.
        Assert.Equal(
            [
                "people/412417/extended?meta=translations",
                "people/602/extended?meta=translations",
                "people/604/extended?meta=translations",
                "people/605/extended?meta=translations",
            ],
            PeoplePaths(harness)
        );
    }

    [Fact]
    public async Task ARefresh_WritesThePeopleWithTheirDetails()
    {
        using var harness = new ServiceHarness(http: OnePieceWithPeople());

        await harness.Refresh();

        var tanaka = Creator(harness, 412417);
        Assert.Equal(new FuzzyDateOnly(1955, 1, 15), tanaka.BirthDay);
        Assert.Equal(PersonGender.Female, tanaka.Gender);
        Assert.Contains(tanaka.Resources, resource => resource.ID == "nm0849028");
        Assert.Equal(new FuzzyDateOnly(1967), Creator(harness, 602).BirthDay);
        Assert.Equal(new FuzzyDateOnly(2001, 11, 23), Creator(harness, 605).DeathDay);
    }

    [Fact]
    public async Task APersonTheTvdbDoesNotHave_KeepsWhatTheCreditsSayAndIsRecordedAsMissing()
    {
        using var harness = new ServiceHarness(http: OnePieceWithPeople());

        await harness.Refresh();

        var oda = Creator(harness, 604);
        Assert.Equal("Eiichiro Oda", oda.Name);
        Assert.Null(oda.BirthDay);
        Assert.Equal(["https://thetvdb.com/dereferrer/people/604"], oda.Resources.Select(resource => resource.Url));
        Assert.False(harness.Store.GetPerson(604)!.Found);
    }

    [Fact]
    public async Task APersonWithoutAPhotoInTheCredits_TakesTheOneFromTheirRecord()
    {
        using var harness = new ServiceHarness(http: OnePieceWithPeople());

        await harness.Refresh();

        // The credit's own photo wins where there is one.
        Assert.Equal("person/412417/primary.jpg", harness.Store.GetPortrait(TvdbUtility.CreatorGuid(412417)));
        Assert.Null(harness.Store.GetPortrait(TvdbUtility.CreatorGuid(602)));
    }

    [Fact]
    public async Task ASecondRefresh_FetchesNobodyAndStillWritesTheDetails()
    {
        using var harness = new ServiceHarness(http: OnePieceWithPeople());
        await harness.Refresh();
        harness.Http.Requests.Clear();

        await harness.Refresh();

        Assert.Empty(PeoplePaths(harness));
        Assert.Equal(new FuzzyDateOnly(1955, 1, 15), Creator(harness, 412417).BirthDay);
    }

    [Fact]
    public async Task AStaleRecord_IsFetchedAgain()
    {
        using var harness = new ServiceHarness(http: OnePieceWithPeople());
        await harness.Refresh();
        var stale = harness.Store.GetPerson(412417)!;
        stale.FetchedAt = DateTime.UtcNow - TvdbPeopleService.StaleAfter - TimeSpan.FromHours(1);
        harness.Store.SavePerson(stale);
        var fresh = harness.Store.GetPerson(604)!;
        fresh.FetchedAt = DateTime.UtcNow - TvdbPeopleService.StaleAfter + TimeSpan.FromHours(1);
        harness.Store.SavePerson(fresh);
        harness.Http.Requests.Clear();

        await harness.Refresh();

        Assert.Equal(["people/412417/extended?meta=translations"], PeoplePaths(harness));
        Assert.True(harness.Store.GetPerson(412417)!.FetchedAt > DateTime.UtcNow - TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task TheLimit_LeavesTheRestForALaterRefresh()
    {
        using var harness = new ServiceHarness(new TvdbConfiguration { ApiKey = "api-key", PersonDetailsLimit = 2 }, OnePieceWithPeople());

        await harness.Refresh();
        Assert.Equal(["people/412417/extended?meta=translations", "people/602/extended?meta=translations"], PeoplePaths(harness));
        Assert.Null(harness.Store.GetPerson(605));
        Assert.Null(Creator(harness, 605).DeathDay);
        harness.Http.Requests.Clear();

        await harness.Refresh();
        Assert.Equal(["people/604/extended?meta=translations", "people/605/extended?meta=translations"], PeoplePaths(harness));
        Assert.Equal(new FuzzyDateOnly(2001, 11, 23), Creator(harness, 605).DeathDay);
    }

    [Fact]
    public async Task TheSettingOff_FetchesNobodyButStillWritesWhatWasFetchedBefore()
    {
        using var harness = new ServiceHarness(http: OnePieceWithPeople());
        await harness.Refresh();
        var stale = harness.Store.GetPerson(412417)!;
        stale.FetchedAt = DateTime.UnixEpoch;
        harness.Store.SavePerson(stale);
        harness.Configuration.AutoDownloadPersonDetails = false;
        harness.Http.Requests.Clear();

        await harness.Refresh();

        Assert.Empty(PeoplePaths(harness));
        Assert.Equal(new FuzzyDateOnly(1955, 1, 15), Creator(harness, 412417).BirthDay);
    }

    [Fact]
    public async Task TheSettingOff_FromTheStart_WritesOnlyWhatTheCreditsSay()
    {
        using var harness = new ServiceHarness(new TvdbConfiguration { ApiKey = "api-key", AutoDownloadPersonDetails = false }, OnePieceWithPeople());

        await harness.Refresh();

        Assert.Empty(PeoplePaths(harness));
        Assert.Null(Creator(harness, 412417).BirthDay);
        Assert.Null(harness.Store.GetPerson(412417));
    }

    [Fact]
    public async Task AServerErrorForAPerson_StopsTheFetchingButNotTheRefresh()
    {
        var http = OnePieceWithPeople().Route("people/412417/extended?meta=translations", "{}", HttpStatusCode.InternalServerError);
        using var harness = new ServiceHarness(http: http);

        await harness.Refresh();

        Assert.Equal(["people/412417/extended?meta=translations"], PeoplePaths(harness));
        Assert.Null(harness.Store.GetPerson(412417));
        Assert.Equal("Mayumi Tanaka", Creator(harness, 412417).Name);
        Assert.Equal(2, harness.People.GetCast(TvdbUtility.SeriesGuid(81797)).Count);
    }

    [Fact]
    public async Task AnUnreadableRecord_IsSkippedAndTheRestAreStillFetched()
    {
        var http = OnePieceWithPeople().Route("people/412417/extended?meta=translations", "not json");
        using var harness = new ServiceHarness(http: http);

        await harness.Refresh();

        Assert.Equal(4, PeoplePaths(harness).Count);
        Assert.Null(harness.Store.GetPerson(412417));
        Assert.Equal(new FuzzyDateOnly(1967), Creator(harness, 602).BirthDay);
    }

    [Fact]
    public async Task ACancelledCall_Throws()
    {
        using var harness = new ServiceHarness(http: OnePieceWithPeople());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Get<TvdbPeopleService>().GetPeople([412417], cancellation.Token));
        Assert.Empty(PeoplePaths(harness));
    }

    [Fact]
    public async Task CleaningUp_ForgetsTheRecordsOfPeopleNoShowCredits()
    {
        using var harness = new ServiceHarness(http: OnePieceWithPeople());
        await harness.Refresh();
        harness.Store.SavePerson(TvdbEntityMapper.MissingPerson(999, DateTime.UtcNow));

        Assert.Equal(1, harness.Store.RemoveUncreditedPeople());
        Assert.Null(harness.Store.GetPerson(999));
        Assert.NotNull(harness.Store.GetPerson(412417));
    }
}
