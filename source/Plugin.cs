using System;
using System.Net.Http.Headers;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Services;
using Shoko.Plugin.Tvdb.Storage;

namespace Shoko.Plugin.Tvdb;

/// <summary>
/// Plugin supplying TvDB series, season and episode metadata through
/// Shoko's metadata provider contract.
/// </summary>
/// <remarks>
/// This class carries the plugin's identity. It is built twice during start-up,
/// and the first of the two, during discovery, uses
/// <c>Activator.CreateInstance</c> before any container exists, so it must keep
/// a public parameterless constructor and take no dependencies at all.
/// Everything the plugin needs is registered in
/// <see cref="RegisterServices(IServiceCollection, IApplicationPaths)"/>
/// instead.
/// </remarks>
public class Plugin : IPlugin, IPluginServiceRegistration
{
    /// <inheritdoc/>
    public Guid ID { get; private init; } = new("12126642-8c32-455f-aca1-e1a2f7b35a8c");

    /// <inheritdoc/>
    public string Name { get; private set; } = "TvDB";

    /// <inheritdoc/>
    public string Description { get; private set; } = """
        Supplies TvDB series, season and episode metadata for the tvdb metadata source
        through Shoko's metadata provider contract: titles, overviews, images, cast and
        crew, studios and networks, genres and tags, content ratings and alternate episode
        orderings. Official builds ship a licensed TvDB key and need no setup; a build
        from source needs its own project key in the settings.
    """;

    /// <inheritdoc/>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        // Touching the class runs its static constructor, which registers the
        // source before the core closes registration after plugin setup.
        _ = MetadataSource.Tvdb;

        // Concrete singletons, because the plugin's own code resolves them.
        // The provider is also discovered by the server, which prefers a
        // registered instance, so registering it here is what makes the
        // server and the plugin share one object.
        serviceCollection.AddPluginDbContext<Plugin, TvdbDbContext>(TvdbDbContext.DatabaseName);
        serviceCollection.AddSingleton<TvdbRateLimiter>();
        serviceCollection.AddSingleton<TvdbStore>();
        serviceCollection.AddSingleton<TvdbImageService>();
        serviceCollection.AddSingleton<TvdbLinkingService>();
        serviceCollection.AddSingleton<TvdbSearchService>();
        serviceCollection.AddSingleton<TvdbPeopleService>();
        serviceCollection.AddSingleton<TvdbRefreshService>();
        serviceCollection.AddSingleton<TvdbMetadataProvider>();
        serviceCollection.AddHostedService<TvdbBackgroundService>();

        serviceCollection
            // The contact URL is read back from the plugin's own registered
            // info rather than written here, so it names wherever this build
            // was published from instead of hard-coding one host into the
            // source. A local build has no repository URL stamped, so the
            // comment is left off rather than sent empty.
            .AddHttpClient<TvdbApiClient>((provider, client) =>
            {
                var info = provider.GetRequiredService<IPluginManager>().GetPluginInfo<Plugin>();
                client.BaseAddress = new Uri("https://api4.thetvdb.com/v4/");
                client.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("Shoko.Plugin.Tvdb", info?.Version.Version.ToString(3) ?? typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.1.0")
                );
                if (info?.RepositoryUrl is { Length: > 0 } repositoryUrl)
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"(+{repositoryUrl})"));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            });
    }
}
