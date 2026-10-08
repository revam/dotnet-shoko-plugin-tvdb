using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shoko.Abstractions.Connectivity.Suspensions;
using Shoko.Plugin.Tvdb.Metadata;

namespace Shoko.Plugin.Tvdb.Api;

/// <summary>
/// The plugin's requests to TvDB, suspended while TvDB limits the rate,
/// answers with server errors or refuses the key. The rate limiter reports
/// through it.
/// </summary>
/// <param name="rateLimiter">The rate limiter, which keeps the refused key.</param>
public sealed class TvdbSuspensionProvider(TvdbRateLimiter rateLimiter) : ISuspensionProvider
{
    /// <inheritdoc/>
    public string Name => "TvDB";

    /// <inheritdoc/>
    public string? Description => "Requests to TvDB, held back while TvDB limits the rate, answers with server errors or refuses the key.";

    /// <inheritdoc/>
    public IReadOnlyList<Type> HeldProviderTypes => [typeof(TvdbMetadataProvider)];

    /// <summary>
    /// Lifts a refused key: forgets it, so the next request logs in again.
    /// The other suspensions cannot be lifted.
    /// </summary>
    /// <param name="kind">The kind of the suspension.</param>
    /// <param name="token">Cancels the lift.</param>
    /// <returns>A completed task.</returns>
    public Task Lift(SuspensionKind kind, CancellationToken token)
    {
        if (kind is SuspensionKind.AuthenticationFailed)
            rateLimiter.ForgetRefusedCredentials();

        return Task.CompletedTask;
    }
}
