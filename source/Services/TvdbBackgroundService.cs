using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.Tvdb.Metadata;
using Shoko.Plugin.Tvdb.Storage;

namespace Shoko.Plugin.Tvdb.Services;

/// <summary>
/// The little the plugin does on its own at start-up: registering the image
/// template, and once the server has started and after an upgrade,
/// refreshing every linked show once and dropping the file the shows used to
/// be kept in.
/// </summary>
/// <param name="systemService">The core's system service, for when the server has started.</param>
/// <param name="imageService">Registers the image template.</param>
/// <param name="seriesStore">The core's series store, asked whether anything is stored.</param>
/// <param name="crossReferences">The core's store of links, asked whether anything is linked.</param>
/// <param name="refreshService">The core's refresh service.</param>
/// <param name="applicationPaths">The server's directories, where the old file lived.</param>
/// <param name="logger">The logger.</param>
public sealed class TvdbBackgroundService(
    ISystemService systemService,
    TvdbImageService imageService,
    IMetadataSeriesStore seriesStore,
    IMetadataCrossReferenceStore crossReferences,
    IMetadataRefreshService refreshService,
    IApplicationPaths applicationPaths,
    ILogger<TvdbBackgroundService> logger
) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            imageService.RegisterTemplateUrl();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to register the TvDB image template URL.");
        }

        await WaitForStart(stoppingToken).ConfigureAwait(false);
        await RefreshIfNothingStored(stoppingToken).ConfigureAwait(false);
        RemoveOldStore();
    }

    /// <summary>
    /// Asks the core to refresh every linked show when the series store holds
    /// none of them, which is the case after an upgrade from a version that
    /// kept the shows in a file of its own.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many refreshes were queued.</returns>
    public async Task<int> RefreshIfNothingStored(CancellationToken cancellationToken = default)
    {
        try
        {
            if (seriesStore.GetAllSeries(MetadataSource.Tvdb).Count > 0)
                return 0;

            if (!crossReferences.GetAllSeriesLinks(MetadataSource.Tvdb).Any(link => link.ProviderID is not null))
                return 0;

            logger.LogInformation("No TvDB show is stored while some are linked. Refreshing every linked TvDB show.");
            return await refreshService.RefreshAllLinked(MetadataSource.Tvdb, entityType: MetadataEntityType.Series, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Unable to refresh the linked TvDB shows.");
            return 0;
        }
    }

    /// <summary>
    /// Deletes the file earlier versions kept the shows in, which nothing
    /// reads any more.
    /// </summary>
    /// <returns>Whether there was a file to delete.</returns>
    public bool RemoveOldStore()
    {
        var directory = Path.Combine(applicationPaths.DataPath, "Tvdb");
        var path = Path.Combine(directory, "store.json");
        try
        {
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);

            logger.LogInformation("Deleted the old TvDB store at {Path}; the shows are kept in the server's database now.", path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Unable to delete the old TvDB store at {Path}.", path);
            return false;
        }
    }

    private async Task WaitForStart(CancellationToken cancellationToken)
    {
        if (systemService.IsStarted)
            return;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStarted(object? sender, EventArgs eventArgs) => started.TrySetResult();
        systemService.Started += OnStarted;
        try
        {
            if (!systemService.IsStarted)
                await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            systemService.Started -= OnStarted;
        }
    }
}
