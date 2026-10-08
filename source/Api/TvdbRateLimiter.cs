using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Connectivity.Suspensions;

namespace Shoko.Plugin.Tvdb.Api;

/// <summary>
/// Token bucket rate limiter for TvDB calls, and the suspensions the plugin
/// reports while TvDB will not take work. Thread-safe.
/// </summary>
/// <remarks>
/// <para>
///   TvDB publishes no request-per-second figure for v4; its terms say only
///   that excessive use may be throttled, and the API answers HTTP 429 when it
///   decides you are (<see cref="TvdbApiClient"/> honours <c>Retry-After</c>).
///   The defaults here, a burst of five refilling at two per second, are
///   therefore a politeness budget rather than a documented limit.
/// </para>
/// <para>
///   The suspensions do not hold requests back themselves. The client
///   raises one when TvDB rate limits it, fails or refuses the key, and the
///   core holds the plugin's jobs back until it runs out or is resumed. The
///   waits of each kind are kept apart, and the latest is the one to wait for.
/// </para>
/// </remarks>
public sealed class TvdbRateLimiter : IDisposable
{
    private readonly int _maxTokens;
    private readonly double _tokensPerSecond;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TvdbRateLimiter> _logger;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly Lock _lock = new();

    private double _currentTokens;
    private DateTimeOffset _lastRefill;

    private readonly ISuspensionReporter<TvdbSuspensionProvider>? _reporter;

    private DateTimeOffset? _rateLimitedUntil;
    private DateTimeOffset? _serverErrorsUntil;
    private (string ApiKey, string? Pin)? _refusedCredentials;
    private (string ApiKey, string? Pin)? _refusedPin;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvdbRateLimiter"/> class.
    /// </summary>
    /// <param name="maxTokens">Maximum burst capacity, and the maximum number of concurrent requests.</param>
    /// <param name="tokensPerSecond">Tokens refilled per second.</param>
    /// <param name="timeProvider">
    /// The time source to measure refills against. Defaults to
    /// <see cref="TimeProvider.System"/>; a test passes its own so refills can
    /// be simulated without a real wait.
    /// </param>
    /// <param name="logger">
    /// Where suspensions are logged. Defaults to a logger that drops
    /// everything.
    /// </param>
    /// <param name="reporter">
    /// Where the suspensions are reported. Defaults to nowhere.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxTokens"/> or
    /// <paramref name="tokensPerSecond"/> is not positive.
    /// </exception>
    public TvdbRateLimiter(
        int maxTokens = 5,
        double tokensPerSecond = 2.0,
        TimeProvider? timeProvider = null,
        ILogger<TvdbRateLimiter>? logger = null,
        ISuspensionReporter<TvdbSuspensionProvider>? reporter = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokensPerSecond);

        _maxTokens = maxTokens;
        _tokensPerSecond = tokensPerSecond;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<TvdbRateLimiter>.Instance;
        _reporter = reporter;
        _concurrencySemaphore = new SemaphoreSlim(maxTokens, maxTokens);
        _currentTokens = maxTokens;
        _lastRefill = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Acquires a token, waiting until one is available. Callers must pair this
    /// with a <see cref="Release"/> call in a <c>finally</c> block.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes once a token has been acquired.</returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> is cancelled while
    /// waiting.
    /// </exception>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!TryConsumeToken())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _concurrencySemaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// Releases the concurrency slot acquired by <see cref="WaitAsync"/>. Must
    /// be called exactly once per successful <see cref="WaitAsync"/> call.
    /// </summary>
    public void Release() => _concurrencySemaphore.Release();

    #region Suspending

    /// <summary>
    /// When the wait TvDB asked for with a 429 runs out, while one is on.
    /// </summary>
    public DateTimeOffset? RateLimitedUntil
    {
        get
        {
            lock (_lock)
                return Active(_rateLimitedUntil);
        }
    }

    /// <summary>
    /// When the wait after a server error runs out, while one is on.
    /// </summary>
    public DateTimeOffset? ServerErrorsUntil
    {
        get
        {
            lock (_lock)
                return Active(_serverErrorsUntil);
        }
    }

    /// <summary>
    /// When TvDB may be given work again: the later of the two waits, while
    /// either is on.
    /// </summary>
    public DateTimeOffset? ResumesAt
    {
        get
        {
            lock (_lock)
                return (Active(_rateLimitedUntil), Active(_serverErrorsUntil)) switch
                {
                    ({ } rate, { } errors) => rate > errors ? rate : errors,
                    ({ } rate, null) => rate,
                    (null, { } errors) => errors,
                    _ => null,
                };
        }
    }

    /// <summary>
    /// TvDB answered 429: suspends the plugin for as long as it asked, or for
    /// longer when a longer wait of the kind is already on.
    /// </summary>
    /// <param name="delay">How long TvDB asked to wait.</param>
    public void NotifyRateLimited(TimeSpan delay)
        => Suspend(SuspensionKind.RateLimited, delay, ref _rateLimitedUntil, "TvDB is rate limiting requests.");

    /// <summary>
    /// TvDB answered with a server error: suspends the plugin for a while,
    /// or for longer when a longer wait of the kind is already on.
    /// </summary>
    /// <param name="duration">How long to wait.</param>
    public void NotifyServerError(TimeSpan duration)
        => Suspend(SuspensionKind.ServerErrors, duration, ref _serverErrorsUntil, "TvDB answered with a server error.");

    // The core clears the suspension once its end passes, so nothing resumes it here.
    private void Suspend(SuspensionKind kind, TimeSpan duration, ref DateTimeOffset? slot, string message)
    {
        if (duration <= TimeSpan.Zero)
            return;

        DateTimeOffset until;
        lock (_lock)
        {
            until = _timeProvider.GetUtcNow() + duration;
            if (Active(slot) is { } current && current >= until)
                return;

            slot = until;
        }

        _logger.LogInformation("{Message} All TvDB jobs paused for {Duration}. They will resume automatically.", message, duration);
        Report(reporter => reporter.Suspend(kind, resumesAt: until.UtcDateTime), kind);
    }

    // Called under the lock.
    private DateTimeOffset? Active(DateTimeOffset? until)
        => until is { } at && at > _timeProvider.GetUtcNow() ? at : null;

    #endregion

    #region Credentials

    /// <summary>
    /// The key and PIN TvDB last refused, which are not tried again until
    /// either is changed or the suspension is lifted.
    /// </summary>
    internal (string ApiKey, string? Pin)? RefusedCredentials
    {
        get
        {
            lock (_lock)
                return _refusedCredentials;
        }
    }

    /// <summary>
    /// The key and PIN whose login TvDB refused while the key alone was
    /// taken, so later logins skip the PIN until either is changed.
    /// </summary>
    internal (string ApiKey, string? Pin)? RefusedPin
    {
        get
        {
            lock (_lock)
                return _refusedPin;
        }
        set
        {
            lock (_lock)
                _refusedPin = value;
        }
    }

    /// <summary>
    /// TvDB refused the key: suspends the plugin until the key or PIN is
    /// changed or a person lifts the suspension.
    /// </summary>
    /// <param name="credentials">The refused key and PIN.</param>
    internal void RefuseCredentials((string ApiKey, string? Pin) credentials)
    {
        lock (_lock)
            _refusedCredentials = credentials;

        Report(reporter => reporter.Suspend(SuspensionKind.AuthenticationFailed, "TvDB refused the API key.", isLiftable: true), SuspensionKind.AuthenticationFailed);
    }

    /// <summary>
    /// Forgets the refused key and PIN, so the next request logs in again,
    /// and resumes the suspension the refusal raised.
    /// </summary>
    internal void ForgetRefusedCredentials()
    {
        lock (_lock)
        {
            _refusedCredentials = null;
            _refusedPin = null;
        }

        Report(reporter => reporter.Resume(SuspensionKind.AuthenticationFailed), SuspensionKind.AuthenticationFailed);
    }

    private void Report(Action<ISuspensionReporter<TvdbSuspensionProvider>> report, SuspensionKind kind)
    {
        if (_reporter is null)
            return;

        try
        {
            report(_reporter);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not report the TvDB {Kind} suspension.", kind);
        }
    }

    #endregion

    /// <summary>
    /// The number of tokens currently available, after refilling for elapsed
    /// time. Exposed for tests; callers use <see cref="WaitAsync"/>.
    /// </summary>
    internal double AvailableTokens
    {
        get
        {
            lock (_lock)
            {
                Refill();
                return _currentTokens;
            }
        }
    }

    private bool TryConsumeToken()
    {
        lock (_lock)
        {
            Refill();
            if (_currentTokens < 1)
                return false;

            _currentTokens--;
            return true;
        }
    }

    private void Refill()
    {
        var now = _timeProvider.GetUtcNow();
        var elapsedSeconds = (now - _lastRefill).TotalSeconds;
        if (elapsedSeconds <= 0)
            return;

        _currentTokens = Math.Min(_maxTokens, _currentTokens + (elapsedSeconds * _tokensPerSecond));
        _lastRefill = now;
    }

    /// <inheritdoc/>
    public void Dispose() => _concurrencySemaphore.Dispose();
}
