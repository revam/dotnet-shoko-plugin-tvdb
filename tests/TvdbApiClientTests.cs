using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Connectivity.Suspensions;
using Shoko.Plugin.Tvdb;
using Shoko.Plugin.Tvdb.Api;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// How the client talks to TvDB: what it sends, when it logs in again, and
/// what it makes of each answer.
/// </summary>
public class TvdbApiClientTests
{
    private static TvdbApiClient CreateClient(
        HttpMessageHandler handler,
        string? apiKey = "api-key",
        string? subscriberPin = null,
        RecordingLogger<TvdbApiClient>? logger = null,
        TvdbConfiguration? configuration = null,
        FakeSuspensionReporter<TvdbSuspensionProvider>? reporter = null
    )
    {
        var configurationService = new FakeConfigurationService(configuration ?? new TvdbConfiguration()
        {
            ApiKey = apiKey,
            SubscriberPin = subscriberPin,
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api4.thetvdb.com/v4/") };

        return new TvdbApiClient(http, new TvdbRateLimiter(reporter: reporter), new ConfigurationProvider<TvdbConfiguration>(configurationService), logger ?? new RecordingLogger<TvdbApiClient>());
    }

    private static StubHttpMessageHandler LoggedIn()
        => new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, Fixture.Read("login-success.json"));

    [Fact]
    public async Task WithoutAnApiKey_NothingIsSent()
    {
        var client = CreateClient(new UnreachableHttpMessageHandler(), apiKey: null);

        Assert.False(client.HasApiKey);
        Assert.Null(await client.GetSeries(81797, TestContext.Current.CancellationToken));
        Assert.Empty(await client.GetEpisodes(81797, "default", TestContext.Current.CancellationToken));
        Assert.Empty(await client.SearchSeries("one piece", 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheFirstRequest_LogsInAndThenCarriesTheToken()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"));
        var client = CreateClient(handler);

        var series = await client.GetSeries(81797, TestContext.Current.CancellationToken);

        Assert.Equal(81797, series?.ID);
        Assert.Equal(2, handler.Requests.Count);

        var login = handler.Requests[0];
        Assert.Equal("POST", login.Method);
        Assert.Equal("https://api4.thetvdb.com/v4/login", login.Uri);
        Assert.Contains("\"apikey\":\"api-key\"", login.Body, StringComparison.Ordinal);
        Assert.Null(login.Authorization);

        var fetch = handler.Requests[1];
        Assert.Equal("GET", fetch.Method);
        Assert.Equal("https://api4.thetvdb.com/v4/series/81797/extended?meta=translations", fetch.Uri);
        Assert.Equal("Bearer a-bearer-token", fetch.Authorization);
    }

    [Fact]
    public async Task WithoutASubscriberPin_ThePinFieldIsOmittedEntirely()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"));
        var client = CreateClient(handler);

        await client.GetSeries(81797, TestContext.Current.CancellationToken);

        // Not `"pin":null` and not `"pin":""`: TvDB rejects both, where an
        // absent field authenticates as the program's own account.
        Assert.DoesNotContain("pin", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithASubscriberPin_ItIsSentAlongsideTheKey()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"));
        var client = CreateClient(handler, subscriberPin: "PIN123");

        await client.GetSeries(81797, TestContext.Current.CancellationToken);

        Assert.Contains("\"pin\":\"PIN123\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedSubscriberPin_IsLeftOutAndNotTriedAgain()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.Unauthorized, Fixture.Read("login-failure.json"))
            .Enqueue(HttpStatusCode.OK, Fixture.Read("login-success.json"))
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"))
            .Enqueue(HttpStatusCode.Unauthorized, "")
            .Enqueue(HttpStatusCode.OK, Fixture.Read("login-success.json"))
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"));
        var logger = new RecordingLogger<TvdbApiClient>();
        var client = CreateClient(handler, subscriberPin: "WRONG", logger: logger);

        Assert.Equal(81797, (await client.GetSeries(81797, TestContext.Current.CancellationToken))?.ID);
        Assert.Equal(81797, (await client.GetSeries(81797, TestContext.Current.CancellationToken))?.ID);

        Assert.Contains("\"pin\":\"WRONG\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("pin", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("pin", handler.Requests[4].Body, StringComparison.Ordinal);
        Assert.Single(logger.Entries, level => level is Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Null(client.RateLimiter.RefusedCredentials);
    }

    [Fact]
    public async Task TheTokenIsCached_SoASecondRequestDoesNotLogInAgain()
    {
        var handler = LoggedIn()
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"))
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"));
        var client = CreateClient(handler);

        await client.GetSeries(81797, TestContext.Current.CancellationToken);
        await client.GetSeries(81797, TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method is "POST");
    }

    [Fact]
    public async Task A401_LogsInAgainOnceAndRetries()
    {
        var handler = LoggedIn()
            .Enqueue(HttpStatusCode.Unauthorized, "")
            .Enqueue(HttpStatusCode.OK, Fixture.Read("login-success.json"))
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"));
        var client = CreateClient(handler);

        var series = await client.GetSeries(81797, TestContext.Current.CancellationToken);

        Assert.Equal(81797, series?.ID);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(2, handler.Uris.Count(uri => uri.EndsWith("/login", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ASecond401_GivesUpRatherThanLoopingOnBadCredentials()
    {
        var handler = LoggedIn()
            .Enqueue(HttpStatusCode.Unauthorized, "")
            .Enqueue(HttpStatusCode.OK, Fixture.Read("login-success.json"))
            .Enqueue(HttpStatusCode.Unauthorized, "");
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<TvdbApiException>(() => client.GetSeries(81797, TestContext.Current.CancellationToken));

        Assert.True(exception.IsAuthenticationFailure);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task RefusedCredentials_AreLoggedAndFailTheRequest()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, Fixture.Read("login-failure.json"));
        var logger = new RecordingLogger<TvdbApiClient>();
        var client = CreateClient(handler, logger: logger);

        var exception = await Assert.ThrowsAsync<TvdbApiException>(() => client.GetSeries(81797, TestContext.Current.CancellationToken));

        Assert.True(exception.IsAuthenticationFailure);
        Assert.Contains(Microsoft.Extensions.Logging.LogLevel.Error, logger.Entries);
    }

    [Fact]
    public async Task ARefusedLoginAfterA401_FailsTheRequest()
    {
        var handler = LoggedIn()
            .Enqueue(HttpStatusCode.Unauthorized, "")
            .Enqueue(HttpStatusCode.Unauthorized, Fixture.Read("login-failure.json"));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<TvdbApiException>(() => client.GetEpisodes(81797, "default", TestContext.Current.CancellationToken));

        Assert.True(exception.IsAuthenticationFailure);
        Assert.NotNull(client.RateLimiter.RefusedCredentials);
    }

    [Fact]
    public async Task APageLostAfterTheFirst_FailsTheListing()
    {
        var handler = LoggedIn()
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-episodes-page0.json"))
            .Enqueue(HttpStatusCode.NotFound, Fixture.Read("not-found.json"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<TvdbApiException>(() => client.GetEpisodes(81797, "default", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMissingFirstPage_IsAnEmptyListing()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.NotFound, Fixture.Read("not-found.json"));
        var client = CreateClient(handler);

        Assert.Empty(await client.GetEpisodes(999999, "default", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NotFound_IsNotAnError()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.NotFound, Fixture.Read("not-found.json"));
        var client = CreateClient(handler);

        Assert.Null(await client.GetSeries(999999, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AServerError_Throws()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.InternalServerError, "");
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<TvdbApiException>(() => client.GetSeries(81797, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.False(exception.IsAuthenticationFailure);
    }

    [Fact]
    public async Task ARateLimit_IsRetriedAfterTheDelayTheServerAsksFor()
    {
        var handler = LoggedIn()
            .Enqueue(HttpStatusCode.TooManyRequests, "", retryAfter: TimeSpan.Zero)
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"));
        var client = CreateClient(handler);

        var series = await client.GetSeries(81797, TestContext.Current.CancellationToken);

        Assert.Equal(81797, series?.ID);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task AnUnparseableBody_Throws()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.OK, "this is not json");
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<TvdbApiException>(() => client.GetSeries(81797, TestContext.Current.CancellationToken));

        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task Episodes_FollowThePagingLinksToTheEnd()
    {
        var handler = LoggedIn()
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-episodes-page0.json"))
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-episodes-page1.json"));
        var client = CreateClient(handler);

        var episodes = await client.GetEpisodes(81797, "default", TestContext.Current.CancellationToken);

        Assert.Equal(3, episodes.Count);
        Assert.Equal([361887, 361888, 619671], episodes.Select(episode => episode.ID));
        Assert.Equal("https://api4.thetvdb.com/v4/series/81797/episodes/default?page=0", handler.Uris[1]);
        Assert.Equal("https://api4.thetvdb.com/v4/series/81797/episodes/default?page=1", handler.Uris[2]);
    }

    [Fact]
    public async Task ASeasonTypeWithOddCharacters_IsEscapedIntoThePath()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.OK, Fixture.Read("not-found.json"));
        var client = CreateClient(handler);

        await client.GetEpisodes(81797, "alternate/dvd", TestContext.Current.CancellationToken);

        Assert.Contains("alternate%2Fdvd", handler.Uris[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_AsksForSeriesOnlyAndEscapesTheQuery()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.OK, Fixture.Read("search-one-piece.json"));
        var client = CreateClient(handler);

        var results = await client.SearchSeries("one piece & co", 5, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Contains("query=one%20piece%20%26%20co", handler.Uris[1], StringComparison.Ordinal);
        Assert.Contains("type=series", handler.Uris[1], StringComparison.Ordinal);
        Assert.Contains("limit=5", handler.Uris[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptySearchQuery_NeverReachesTheNetwork()
    {
        var client = CreateClient(new UnreachableHttpMessageHandler());

        Assert.Empty(await client.SearchSeries("   ", 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefusedCredentials_SuspendThePluginAndAreNotTriedAgain()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, Fixture.Read("login-failure.json"));
        var reporter = new FakeSuspensionReporter<TvdbSuspensionProvider>();
        var client = CreateClient(handler, reporter: reporter);

        await Assert.ThrowsAsync<TvdbApiException>(() => client.GetSeries(81797, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TvdbApiException>(() => client.GetSeries(81797, TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
        Assert.Equal([SuspensionKind.AuthenticationFailed], reporter.Active.Keys);
    }

    [Fact]
    public async Task NewCredentials_AfterARefusal_AreTriedAtOnce()
    {
        var configuration = new TvdbConfiguration { ApiKey = "wrong-key" };
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.Unauthorized, Fixture.Read("login-failure.json"))
            .Enqueue(HttpStatusCode.OK, Fixture.Read("login-success.json"))
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"));
        var reporter = new FakeSuspensionReporter<TvdbSuspensionProvider>();
        var client = CreateClient(handler, configuration: configuration, reporter: reporter);
        await Assert.ThrowsAsync<TvdbApiException>(() => client.GetSeries(81797, TestContext.Current.CancellationToken));

        configuration.ApiKey = "right-key";
        var series = await client.GetSeries(81797, TestContext.Current.CancellationToken);

        Assert.Equal(81797, series?.ID);
        Assert.Empty(reporter.Active);
        Assert.Contains("right-key", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerError_SuspendsThePluginForAWhile()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.BadGateway, "");
        var reporter = new FakeSuspensionReporter<TvdbSuspensionProvider>();
        var client = CreateClient(handler, reporter: reporter);

        await Assert.ThrowsAsync<TvdbApiException>(() => client.GetSeries(81797, TestContext.Current.CancellationToken));

        Assert.NotNull(client.RateLimiter.ServerErrorsUntil);
        Assert.NotNull(reporter.Active[SuspensionKind.ServerErrors].ResumesAt);
    }

    [Fact]
    public async Task ARateLimit_SuspendsThePluginForAsLongAsTheServerAsks()
    {
        var handler = LoggedIn()
            .Enqueue(HttpStatusCode.TooManyRequests, "", retryAfter: TimeSpan.FromMilliseconds(1500))
            .Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"));
        var reporter = new FakeSuspensionReporter<TvdbSuspensionProvider>();
        var client = CreateClient(handler, reporter: reporter);
        var before = DateTime.UtcNow;

        var series = await client.GetSeries(81797, TestContext.Current.CancellationToken);

        Assert.Equal(81797, series?.ID);
        Assert.InRange(reporter.Active[SuspensionKind.RateLimited].ResumesAt!.Value, before.AddSeconds(1), DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public async Task TranslatedEpisodes_AreReadFromTheShowTheListingAnswersWith()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-episodes-default-eng.json"));
        var client = CreateClient(handler);

        var episodes = await client.GetTranslatedEpisodes(81797, "default", "eng", TestContext.Current.CancellationToken);

        Assert.Equal(["I'm Luffy! The Man Who's Gonna Be King of the Pirates!", "Enter the Great Swordsman! Pirate Hunter Roronoa Zoro!"], episodes.Select(episode => episode.Name));
        Assert.Equal("https://api4.thetvdb.com/v4/series/81797/episodes/default/eng?page=0", handler.Uris[1]);
    }

    [Fact]
    public async Task AnExtendedShow_CarriesEverythingTheRefreshReads()
    {
        var handler = LoggedIn().Enqueue(HttpStatusCode.OK, Fixture.Read("series-81797-extended.json"));
        var client = CreateClient(handler);

        var series = await client.GetSeries(81797, TestContext.Current.CancellationToken);

        Assert.NotNull(series);
        Assert.Equal(1, series.DefaultSeasonType);
        Assert.Equal(5, series.SeasonTypes?.Count);
        Assert.Equal(9, series.Artworks?.Count);
        Assert.Equal(6, series.Characters?.Count);
        Assert.Equal(2, series.Genres?.Count);
        Assert.Single(series.Tags!);
        Assert.Equal(7, series.RemoteIDs?.Count);
        Assert.Equal(111, series.OriginalNetwork?.ID);
        Assert.Equal(1, series.Companies?[0].CompanyType?.CompanyTypeID);
        Assert.Equal(3, series.Companies?[4].PrimaryCompanyType);
        Assert.Equal("Story Order", series.SeasonTypes?[3].AlternateName);
    }
}
