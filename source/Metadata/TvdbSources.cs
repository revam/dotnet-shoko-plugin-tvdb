using Shoko.Abstractions.Metadata;

namespace Shoko.Plugin.Tvdb.Metadata;

/// <summary>
/// The metadata source the plugin owns, registered once.
/// </summary>
/// <remarks>
/// The static constructor registers the source, and
/// <see cref="Plugin.RegisterServices(Microsoft.Extensions.DependencyInjection.IServiceCollection, Shoko.Abstractions.Plugin.IApplicationPaths)"/>
/// touches the class so that it runs before the core closes registration after
/// plugin setup. The old <c>DataSource</c> enum spelled it <c>TvDB</c>, which
/// the value already matches ignoring case; <c>thetvdb</c> is taken as input too.
/// </remarks>
public static class TvdbSources
{
    static TvdbSources()
    {
        Source = MetadataSource.Register("TvDB", "tvdb", ["thetvdb"], description: "The television and film database at thetvdb.com.");
    }

    /// <summary>
    /// The registered source, behind the extension member.
    /// </summary>
    private static MetadataSource Source { get; }

    extension(MetadataSource)
    {
        /// <summary>
        /// TvDB, as <c>tvdb</c>.
        /// </summary>
        public static MetadataSource Tvdb => Source;
    }
}
