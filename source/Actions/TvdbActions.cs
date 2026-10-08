using System.Threading;
using System.Threading.Tasks;
using Shoko.Abstractions.Actions;
using Shoko.Abstractions.Connectivity.Suspensions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Tvdb.Api;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Services;

namespace Shoko.Plugin.Tvdb.Actions;

/// <summary>
/// The checks every TvDB series action runs before it can do anything.
/// </summary>
/// <remarks>
/// A helper rather than a base class: the core refuses at start-up an action
/// that does not derive from <see cref="SeriesAction"/> directly, or that
/// leaves its permission to a base type. Each action is a thin shell over one
/// of the core's services, kept so the three things a person does most with a
/// series' TvDB links sit under the plugin's own name.
/// </remarks>
internal static class TvdbActionValidation
{
    /// <summary>
    /// Refuses when the provider is switched off, or, for an action that
    /// reaches TvDB, while the provider is not configured, for want of an
    /// API key, or suspended.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="providerManager">The registry, asked whether the provider is switched on.</param>
    /// <param name="suspensionReporter">The plugin's suspensions, or <c>null</c> for an action that does not reach TvDB.</param>
    /// <returns>Why the action cannot run, or <c>null</c>.</returns>
    public static Task<ActionValidationResult?> Validate(
        TvdbMetadataProvider provider,
        IMetadataProviderManager providerManager,
        ISuspensionReporter<TvdbSuspensionProvider>? suspensionReporter
    )
        => Task.FromResult<ActionValidationResult?>(
            !providerManager.IsProviderEnabled(provider)
                ? new ActionValidationResult("The TvDB metadata provider is switched off.")
                : suspensionReporter is not null && !provider.IsConfigured
                    ? new ActionValidationResult(provider.NotConfiguredReason ?? "The TvDB metadata provider is not configured.")
                    : suspensionReporter?.Current is { IsSuspended: true, Suspensions: [var suspension, ..] }
                        ? new ActionValidationResult(suspension.Reason ?? Describe(suspension.Kind))
                        : null
        );

    private static string Describe(SuspensionKind kind) => kind switch
    {
        SuspensionKind.RateLimited => "TvDB is rate limiting requests.",
        SuspensionKind.ServerErrors => "TvDB is answering with server errors.",
        SuspensionKind.AuthenticationFailed => "TvDB refused the API key.",
        _ => "The TvDB metadata provider is suspended.",
    };
}

/// <summary>
/// Searches TvDB for a show matching a series and links the best hit.
/// </summary>
/// <param name="provider">The metadata provider.</param>
/// <param name="providerManager">The registry.</param>
/// <param name="linkingService">The core's linking service, which runs the provider's auto-linker.</param>
/// <param name="suspensionReporter">The plugin's suspensions.</param>
public sealed class AutoLinkTvdbSeriesAction(
    TvdbMetadataProvider provider,
    IMetadataProviderManager providerManager,
    IMetadataLinkingService linkingService,
    ISuspensionReporter<TvdbSuspensionProvider> suspensionReporter
) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Auto-link TvDB Show";

    /// <inheritdoc/>
    public override string? Description => "Searches TvDB for a show matching this series' titles and links the best match in place of its other TvDB links.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.User;

    /// <inheritdoc/>
    public override Task<ActionValidationResult?> Validate(CancellationToken token = default)
        => TvdbActionValidation.Validate(provider, providerManager, suspensionReporter);

    /// <inheritdoc/>
    public override Task Execute(CancellationToken token = default)
        => linkingService.AutoLink(MetadataSource.Tvdb, Series.AnidbAnimeID, token);
}

/// <summary>
/// Re-fetches the TvDB shows a series is linked to.
/// </summary>
/// <param name="provider">The metadata provider.</param>
/// <param name="providerManager">The registry.</param>
/// <param name="refreshService">The core's refresh service, which queues the refreshes.</param>
/// <param name="suspensionReporter">The plugin's suspensions.</param>
public sealed class RefreshTvdbSeriesAction(
    TvdbMetadataProvider provider,
    IMetadataProviderManager providerManager,
    IMetadataRefreshService refreshService,
    ISuspensionReporter<TvdbSuspensionProvider> suspensionReporter
) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Refresh TvDB Metadata";

    /// <inheritdoc/>
    public override string? Description => "Re-fetches every TvDB show this series is linked to, and re-matches its episodes.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.User;

    /// <inheritdoc/>
    public override Task<ActionValidationResult?> Validate(CancellationToken token = default)
        => TvdbActionValidation.Validate(provider, providerManager, suspensionReporter);

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
            MetadataSource.Tvdb,
            force: true,
            options: new() { Reason = MetadataRefreshReason.Requested, DownloadImages = true },
            cancellationToken: token
        );
}

/// <summary>
/// Drops every TvDB link a series has, and the shows nothing else links to.
/// </summary>
/// <param name="provider">The metadata provider.</param>
/// <param name="providerManager">The registry.</param>
/// <param name="linkingService">The plugin's linking service.</param>
public sealed class UnlinkTvdbSeriesAction(TvdbMetadataProvider provider, IMetadataProviderManager providerManager, TvdbLinkingService linkingService) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Unlink TvDB Show";

    /// <inheritdoc/>
    public override string? Description => "Removes this series' TvDB links and the stored shows, if nothing else is linked to them.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.User;

    /// <inheritdoc/>
    public override bool RequiresConfirmation => true;

    /// <inheritdoc/>
    public override string? ConfirmationMessage => "This removes the TvDB links and stored shows for this series. The data can be fetched again.";

    /// <summary>
    /// Refuses only when the provider is switched off: removing links needs
    /// neither a key nor TvDB.
    /// </summary>
    /// <param name="token">A cancellation token.</param>
    /// <returns>Why the action cannot run, or <c>null</c>.</returns>
    public override Task<ActionValidationResult?> Validate(CancellationToken token = default)
        => TvdbActionValidation.Validate(provider, providerManager, suspensionReporter: null);

    /// <summary>
    /// Unlinks every TvDB show and episode the series is linked to, and
    /// stops it being linked automatically again.
    /// </summary>
    /// <param name="token">A cancellation token.</param>
    /// <returns>A task that completes when every link is gone.</returns>
    public override Task Execute(CancellationToken token = default)
        => linkingService.RemoveAllLinks(Series.AnidbAnimeID, purge: true, token);
}
