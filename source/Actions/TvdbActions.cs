using System.Threading;
using System.Threading.Tasks;
using Shoko.Abstractions.Actions;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Services;

namespace Shoko.Plugin.Tvdb.Actions;

/// <summary>
/// The checks every TheTVDB series action runs before it can do anything.
/// </summary>
/// <remarks>
/// A helper rather than a base class: the core refuses at start-up an action
/// that does not derive from <see cref="SeriesAction"/> directly, or that
/// leaves its permission to a base type. Each action is a thin shell over one
/// of the core's services, kept so the three things a person does most with a
/// series' TheTVDB links sit under the plugin's own name.
/// </remarks>
internal static class TvdbActionValidation
{
    /// <summary>
    /// Refuses when the provider is switched off, or, for an action that
    /// reaches TheTVDB, while the provider is not configured, for want of an
    /// API key, or paused.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="providerManager">The registry, asked whether the provider is switched on.</param>
    /// <param name="reachesTheTvdb">Whether the action reaches TheTVDB.</param>
    /// <returns>Why the action cannot run, or <see langword="null"/>.</returns>
    public static Task<ActionValidationResult?> Validate(TvdbMetadataProvider provider, IMetadataProviderManager providerManager, bool reachesTheTvdb)
        => Task.FromResult<ActionValidationResult?>(
            !providerManager.IsProviderEnabled(provider)
                ? new ActionValidationResult("The TheTVDB metadata provider is switched off.")
                : reachesTheTvdb && !provider.IsConfigured
                    ? new ActionValidationResult(provider.NotConfiguredReason ?? "The TheTVDB metadata provider is not configured.")
                    : reachesTheTvdb && provider.PauseStatus is { IsPaused: true, Reason: var reason }
                        ? new ActionValidationResult(reason ?? "The TheTVDB metadata provider is paused.")
                        : null
        );
}

/// <summary>
/// Searches TheTVDB for a show matching a series and links the best hit.
/// </summary>
/// <param name="provider">The metadata provider.</param>
/// <param name="providerManager">The registry.</param>
/// <param name="linkingService">The core's linking service, which runs the provider's auto-linker.</param>
public sealed class AutoLinkTvdbSeriesAction(TvdbMetadataProvider provider, IMetadataProviderManager providerManager, IMetadataLinkingService linkingService) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Auto-link TheTVDB Show";

    /// <inheritdoc/>
    public override string? Description => "Searches TheTVDB for a show matching this series' titles and links the best match in place of its other TheTVDB links.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.User;

    /// <inheritdoc/>
    public override Task<ActionValidationResult?> Validate(CancellationToken token = default)
        => TvdbActionValidation.Validate(provider, providerManager, reachesTheTvdb: true);

    /// <inheritdoc/>
    public override Task Execute(CancellationToken token = default)
        => linkingService.AutoLink(TvdbSources.Tvdb, Series.AnidbAnimeID, token);
}

/// <summary>
/// Re-fetches the TheTVDB shows a series is linked to.
/// </summary>
/// <param name="provider">The metadata provider.</param>
/// <param name="providerManager">The registry.</param>
/// <param name="refreshService">The core's refresh service, which queues the refreshes.</param>
public sealed class RefreshTvdbSeriesAction(TvdbMetadataProvider provider, IMetadataProviderManager providerManager, IMetadataRefreshService refreshService) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Refresh TheTVDB Metadata";

    /// <inheritdoc/>
    public override string? Description => "Re-fetches every TheTVDB show this series is linked to, and re-matches its episodes.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.User;

    /// <inheritdoc/>
    public override Task<ActionValidationResult?> Validate(CancellationToken token = default)
        => TvdbActionValidation.Validate(provider, providerManager, reachesTheTvdb: true);

    /// <summary>
    /// Queues a forced refresh of every show the series is linked to.
    /// </summary>
    /// <remarks>
    /// A person asking for a refresh by hand means now, not "if it has gone
    /// stale", so the core's freshness window is skipped.
    /// </remarks>
    /// <param name="token">A cancellation token.</param>
    /// <returns>A task that completes once the refreshes are queued.</returns>
    public override Task Execute(CancellationToken token = default)
        => refreshService.RefreshForAnime(
            Series.AnidbAnimeID,
            TvdbSources.Tvdb,
            force: true,
            options: new() { Reason = MetadataRefreshReason.Requested, DownloadImages = true },
            cancellationToken: token
        );
}

/// <summary>
/// Drops every TheTVDB link a series has, and the shows nothing else links to.
/// </summary>
/// <param name="provider">The metadata provider.</param>
/// <param name="providerManager">The registry.</param>
/// <param name="linkingService">The plugin's linking service.</param>
public sealed class UnlinkTvdbSeriesAction(TvdbMetadataProvider provider, IMetadataProviderManager providerManager, TvdbLinkingService linkingService) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Unlink TheTVDB Show";

    /// <inheritdoc/>
    public override string? Description => "Removes this series' TheTVDB links and the stored shows, if nothing else is linked to them.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.User;

    /// <inheritdoc/>
    public override bool RequiresConfirmation => true;

    /// <inheritdoc/>
    public override string? ConfirmationMessage => "This removes the TheTVDB links and stored shows for this series. The data can be fetched again.";

    /// <summary>
    /// Refuses only when the provider is switched off: removing links needs
    /// neither a key nor TheTVDB.
    /// </summary>
    /// <param name="token">A cancellation token.</param>
    /// <returns>Why the action cannot run, or <see langword="null"/>.</returns>
    public override Task<ActionValidationResult?> Validate(CancellationToken token = default)
        => TvdbActionValidation.Validate(provider, providerManager, reachesTheTvdb: false);

    /// <summary>
    /// Unlinks every TheTVDB show and episode the series is linked to, and
    /// stops it being linked automatically again.
    /// </summary>
    /// <param name="token">A cancellation token.</param>
    /// <returns>A task that completes when every link is gone.</returns>
    public override Task Execute(CancellationToken token = default)
        => linkingService.RemoveAllLinks(Series.AnidbAnimeID, purge: true, token);
}
