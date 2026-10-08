using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Connectivity.Suspensions;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Abstractions.Metadata.Stub;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Services;
using Shoko.Plugin.Tvdb.Storage;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers each request by its path
/// and query under <c>/v4/</c>, recording what it was asked for. A path with
/// no answer gets TvDB's 404.
/// </summary>
internal sealed class RoutingHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode StatusCode, string Body)> _routes = new(StringComparer.Ordinal);

    public List<RecordedRequest> Requests { get; } = [];

    public List<string> Paths => [.. Requests.Select(request => request.Uri.Replace("https://api4.thetvdb.com/v4/", string.Empty, StringComparison.Ordinal))];

    public RoutingHttpMessageHandler Route(string pathAndQuery, string body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _routes[pathAndQuery] = (statusCode, body);
        return this;
    }

    /// <summary>
    /// Answers everything a refresh of One Piece asks for, from the fixtures.
    /// </summary>
    public static RoutingHttpMessageHandler OnePiece()
        => new RoutingHttpMessageHandler()
            .Route("login", Fixture.Read("login-success.json"))
            .Route("series/81797/extended?meta=translations", Fixture.Read("series-81797-extended.json"))
            .Route("series/81797/episodes/default?page=0", Fixture.Read("series-81797-episodes-page0.json"))
            .Route("series/81797/episodes/default?page=1", Fixture.Read("series-81797-episodes-page1.json"))
            .Route("series/81797/episodes/default/eng?page=0", Fixture.Read("series-81797-episodes-default-eng.json"))
            .Route("series/81797/episodes/dvd?page=0", Fixture.Read("series-81797-episodes-dvd.json"))
            .Route("series/81797/episodes/absolute?page=0", Fixture.Read("series-81797-episodes-absolute.json"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        Requests.Add(new(request.Method.Method, uri, body, request.Headers.Authorization?.ToString()));
        var path = uri.Replace("https://api4.thetvdb.com/v4/", string.Empty, StringComparison.Ordinal);
        var (statusCode, content) = _routes.TryGetValue(path, out var route) ? route : (HttpStatusCode.NotFound, Fixture.Read("not-found.json"));
        return new HttpResponseMessage(statusCode) { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// The plugin's services wired together over fakes of the core's stores and
/// mocks of its services, with a routing HTTP handler in place of TvDB.
/// </summary>
internal sealed class ServiceHarness : IDisposable
{
    private readonly ServiceProvider _services;

    public ServiceHarness(TvdbConfiguration? configuration = null, RoutingHttpMessageHandler? http = null)
    {
        Configuration = configuration ?? new TvdbConfiguration { ApiKey = "api-key" };
        ConfigurationService = new FakeConfigurationService(Configuration);
        ConfigurationProvider = new ConfigurationProvider<TvdbConfiguration>(ConfigurationService);
        Http = http ?? RoutingHttpMessageHandler.OnePiece();
        Series = new FakeSeriesStore(Tags, Studios, People);
        Orderings = new FakeOrderingService(Series);
        RateLimiter = new TvdbRateLimiter(maxTokens: 50, tokensPerSecond: 1000, reporter: SuspensionReporter);
        Store = new TvdbStore(Database, Series, People, Tags, Studios, Orderings);

        LinkingService
            .Setup(service => service.MatchEpisodes(It.IsAny<int>(), It.IsAny<MetadataGuid>(), It.IsAny<MetadataGuid?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        LinkingService
            .Setup(service => service.GetCrossSourceHints(It.IsAny<MetadataSource>(), It.IsAny<int>()))
            .Returns(() => CrossSourceHints);
        MetadataService.Setup(service => service.GetSeason(It.IsAny<MetadataGuid>())).Returns((MetadataGuid id) => Series.GetSeason(id));
        SystemService.SetupGet(service => service.IsStarted).Returns(true);
        ProviderManager.SetupGet(manager => manager.MetadataProviders).Returns(() => [ProviderInfo()]);
        TextManager
            .Setup(manager => manager.GetLanguageOrder(It.IsAny<TextKind>(), It.IsAny<MetadataEntityType?>()))
            .Returns((TextKind kind, MetadataEntityType? entityType) => kind is TextKind.Overview ? OverviewOrder : entityType == MetadataEntityType.Episode ? EpisodeTitleOrder : SeriesTitleOrder);

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(ConfigurationProvider);
        collection.AddSingleton(RateLimiter);
        collection.AddSingleton<ISuspensionReporter<TvdbSuspensionProvider>>(SuspensionReporter);
        collection.AddSingleton(Store);
        collection.AddSingleton<IMetadataSeriesStore>(Series);
        collection.AddSingleton<IMetadataCrossReferenceStore>(CrossReferences);
        collection.AddSingleton(LinkingService.Object);
        collection.AddSingleton(MatchingEngine.Object);
        collection.AddSingleton(MetadataService.Object);
        collection.AddSingleton(ProviderManager.Object);
        collection.AddSingleton(ImageManager.Object);
        collection.AddSingleton(RefreshService.Object);
        collection.AddSingleton(SystemService.Object);
        collection.AddSingleton(TextManager.Object);
        collection.AddSingleton(ApplicationPaths.Object);
        collection.AddSingleton(new TvdbApiClient(new HttpClient(Http) { BaseAddress = new Uri("https://api4.thetvdb.com/v4/") }, RateLimiter, ConfigurationProvider, NullLogger<TvdbApiClient>.Instance));
        collection.AddSingleton<TvdbImageService>();
        collection.AddSingleton<TvdbLinkingService>();
        collection.AddSingleton<TvdbSearchService>();
        collection.AddSingleton<TvdbPeopleService>();
        collection.AddSingleton<TvdbRefreshService>();
        collection.AddSingleton<TvdbMetadataProvider>();
        collection.AddSingleton<TvdbBackgroundService>();
        _services = collection.BuildServiceProvider();
    }

    public TvdbConfiguration Configuration { get; }

    public FakeConfigurationService ConfigurationService { get; }

    public ConfigurationProvider<TvdbConfiguration> ConfigurationProvider { get; }

    public RoutingHttpMessageHandler Http { get; }

    public TvdbRateLimiter RateLimiter { get; }

    public FakeSuspensionReporter<TvdbSuspensionProvider> SuspensionReporter { get; } = new();

    public TestDatabase Database { get; } = new();

    public FakeSeriesStore Series { get; }

    public FakePeopleStore People { get; } = new();

    public FakeTagStore Tags { get; } = new();

    public FakeStudioStore Studios { get; } = new();

    public FakeOrderingService Orderings { get; }

    public FakeCrossReferenceStore CrossReferences { get; } = new();

    public TvdbStore Store { get; }

    public Mock<IMetadataLinkingService> LinkingService { get; } = new();

    /// <summary>
    /// What the core's linking service answers for the shows the anime's
    /// links on other sources name.
    /// </summary>
    public List<MetadataAutoLinkHint> CrossSourceHints { get; } = [];

    public Mock<IMetadataMatchingEngine> MatchingEngine { get; } = new();

    public Mock<IMetadataService> MetadataService { get; } = new();

    public Mock<IMetadataProviderManager> ProviderManager { get; } = new();

    public Mock<IImageManager> ImageManager { get; } = new();

    public Mock<IMetadataRefreshService> RefreshService { get; } = new();

    public Mock<ISystemService> SystemService { get; } = new();

    public Mock<IMetadataTextManager> TextManager { get; } = new();

    /// <summary>The kinds the core has the provider turned on for.</summary>
    public HashSet<MetadataEntityType> EnabledKinds { get; } =
    [
        MetadataEntityType.Series,
        MetadataEntityType.Season,
        MetadataEntityType.Episode,
        MetadataEntityType.Creator,
        MetadataEntityType.Character,
        MetadataEntityType.Studio,
        MetadataEntityType.Network,
    ];

    /// <summary>The core's series title language order.</summary>
    public List<TitleLanguage> SeriesTitleOrder { get; } = [TitleLanguage.Main, TitleLanguage.English];

    /// <summary>The core's episode title language order.</summary>
    public List<TitleLanguage> EpisodeTitleOrder { get; } = [TitleLanguage.English];

    /// <summary>The core's description language order.</summary>
    public List<TitleLanguage> OverviewOrder { get; } = [TitleLanguage.English];

    public Mock<IApplicationPaths> ApplicationPaths { get; } = new();

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private MetadataProviderInfo ProviderInfo() => new()
    {
        ID = Guid.Empty,
        Version = new(1, 0),
        Name = "TvDB",
        Description = "TvDB",
        Provider = Get<TvdbMetadataProvider>(),
        ConfigurationInfo = null,
        PluginInfo = null!,
        SupportsSeries = true,
        SupportsMovies = false,
        SupportsCollections = false,
        SupportsAutoLinking = true,
        Source = MetadataSource.Tvdb,
        AvailableEntityTypes = EnabledKinds,
        EnabledEntityTypes = EnabledKinds,
    };

    /// <summary>
    /// Refreshes One Piece through the provider, as the core's refresh job would.
    /// </summary>
    public Task Refresh(MetadataRefreshOptions? options = null)
        => Get<TvdbMetadataProvider>().RefreshSeries(new MetadataGuid(MetadataSource.Tvdb, MetadataEntityType.Series, "81797"), options ?? new MetadataRefreshOptions(), TestContext.Current.CancellationToken);

    /// <summary>
    /// Adds an AniDB anime with some normal episodes, with a Shoko series over it.
    /// </summary>
    public (Mock<IAnidbAnime> Anime, List<IAnidbEpisode> Episodes) AddAnime(int anidbAnimeID, int episodeCount, string? title = null, DateOnly? firstAired = null)
    {
        var anime = new Mock<IAnidbAnime>();
        var episodes = new List<IAnidbEpisode>();
        var anidbID = new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, anidbAnimeID.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var number = 1; number <= episodeCount; number++)
        {
            var anidbEpisodeID = (anidbAnimeID * 1000) + number;
            var episode = new Mock<IAnidbEpisode>();
            episode.SetupGet(e => e.ID).Returns(new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Episode, anidbEpisodeID.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            episode.SetupGet(e => e.AnidbID).Returns(anidbEpisodeID);
            episode.SetupGet(e => e.AnidbAnimeID).Returns(anidbAnimeID);
            episode.SetupGet(e => e.Type).Returns(EpisodeType.Episode);
            episode.SetupGet(e => e.EpisodeNumber).Returns(number);
            episodes.Add(episode.Object);
        }

        anime.SetupGet(a => a.ID).Returns(anidbID);
        anime.SetupGet(a => a.AnidbID).Returns(anidbAnimeID);
        anime.SetupGet(a => a.Episodes).Returns(episodes);
        anime.SetupGet(a => a.AirDate).Returns(firstAired is { } date ? new PartialDateOnly(date) : null);
        anime.SetupGet(a => a.Titles).Returns(title is null ? [] : [new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.English, LanguageCode = "en", Value = title, Type = TitleType.Official }]);
        MetadataService.Setup(service => service.GetSeries(anidbID)).Returns(anime.Object);
        return (anime, episodes);
    }

    /// <summary>
    /// Makes the matching engine pair the AniDB episodes with the provider's
    /// by position, recording what it was handed.
    /// </summary>
    public List<(IReadOnlyList<IAnidbEpisode> Anidb, IReadOnlyList<IEpisode> Provider, EpisodeMatchOptions? Options)> MatchByPosition()
    {
        var calls = new List<(IReadOnlyList<IAnidbEpisode>, IReadOnlyList<IEpisode>, EpisodeMatchOptions?)>();
        MatchingEngine
            .Setup(engine => engine.MatchEpisodes(It.IsAny<IReadOnlyList<IAnidbEpisode>>(), It.IsAny<IReadOnlyList<IEpisode>>(), It.IsAny<IReadOnlyList<IMetadataEpisodeCrossReference>?>(), It.IsAny<EpisodeMatchOptions?>()))
            .Returns((IReadOnlyList<IAnidbEpisode> anidb, IReadOnlyList<IEpisode> provider, IReadOnlyList<IMetadataEpisodeCrossReference>? _, EpisodeMatchOptions? options) =>
            {
                calls.Add((anidb, provider, options));
                return [.. anidb.Select((episode, index) => new EpisodeMatch
                {
                    AnidbEpisode = episode,
                    Candidate = index < provider.Count ? provider[index] : null,
                    Rating = index < provider.Count ? MatchRating.DateMatches : MatchRating.None,
                })];
            });
        return calls;
    }

    /// <summary>
    /// Makes the matching engine judge the shows it is handed by the ratings
    /// given for their TvDB IDs, keeping the order it was handed them in
    /// and taking the first unless nothing about it agreed, recording what it
    /// was handed.
    /// </summary>
    public List<(IReadOnlyList<MetadataSeriesSearchResult> Candidates, SeriesMatchOptions? Options)> JudgeSeriesBy(IReadOnlyDictionary<int, MatchRating> ratings)
    {
        var calls = new List<(IReadOnlyList<MetadataSeriesSearchResult>, SeriesMatchOptions?)>();
        MatchingEngine
            .Setup(engine => engine.MatchSeries(It.IsAny<IAnidbAnime>(), It.IsAny<IReadOnlyList<MetadataSeriesSearchResult>>(), It.IsAny<SeriesMatchOptions?>()))
            .Returns((IAnidbAnime anime, IReadOnlyList<MetadataSeriesSearchResult> candidates, SeriesMatchOptions? options) =>
            {
                calls.Add((candidates, options));
                var judged = candidates
                    .Select(candidate => (Candidate: candidate, Rating: ratings.GetValueOrDefault(int.Parse(candidate.ID.ID, System.Globalization.CultureInfo.InvariantCulture))))
                    .OrderBy(pair => pair.Rating is MatchRating.None)
                    .ToList();
                return [.. judged.Select((pair, index) => new SeriesMatch
                {
                    AnidbAnime = anime,
                    Candidate = pair.Candidate,
                    Rating = pair.Rating,
                    Rejection = pair.Rating is MatchRating.None ? MatchRejectionReason.TitleMismatch : index is 0 ? MatchRejectionReason.None : MatchRejectionReason.Outranked,
                })];
            });
        return calls;
    }

    public void Dispose()
    {
        _services.Dispose();
        RateLimiter.Dispose();
        Database.Dispose();
    }
}
