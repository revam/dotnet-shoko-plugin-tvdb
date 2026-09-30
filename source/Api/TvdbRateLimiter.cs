using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shoko.Plugin.Tvdb.Api;

/// <summary>
/// Token bucket rate limiter for TheTVDB calls, and the pause the plugin
/// reports while TheTVDB will not take work. Thread-safe.
/// </summary>
/// <remarks>
/// <para>
///   TheTVDB publishes no request-per-second figure for v4; its terms say only
///   that excessive use may be throttled, and the API answers HTTP 429 when it
///   decides you are (<see cref="TvdbApiClient"/> honours <c>Retry-After</c>).
///   The defaults here, a burst of five refilling at two per second, are
///   therefore a politeness budget rather than a documented limit.
/// </para>
/// <para>
///   The pause does not hold requests back itself. The client sets it when
///   TheTVDB rate limits it, fails or refuses the key, and the provider hands
///   it to the core, which holds the plugin's jobs back until it runs out.
/// </para>
/// </remarks>
public sealed class TvdbRateLimiter : IDisposable
{
    private readonly int _maxTokens;
    private readonly double _tokensPerSecond;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly Lock _lock = new();

    private double _currentTokens;
    private DateTimeOffset _lastRefill;

    private DateTimeOffset? _pausedUntil;
    private string? _pauseReason;
    private ITimer? _pauseTimer;

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
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxTokens"/> or
    /// <paramref name="tokensPerSecond"/> is not positive.
    /// </exception>
    public TvdbRateLimiter(int maxTokens = 5, double tokensPerSecond = 2.0, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokensPerSecond);

        _maxTokens = maxTokens;
        _tokensPerSecond = tokensPerSecond;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    #region Pausing

    /// <summary>
    /// Raised when <see cref="IsPaused"/> flips either way, or the reason or
    /// end of a pause changes.
    /// </summary>
    public event EventHandler? PauseStateChanged;

    /// <summary>
    /// Whether TheTVDB is not to be given work right now.
    /// </summary>
    public bool IsPaused
    {
        get
        {
            lock (_lock)
                return _pausedUntil is { } until && until > _timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// Why TheTVDB is not to be given work, while <see cref="IsPaused"/>.
    /// </summary>
    public string? PauseReason
    {
        get
        {
            lock (_lock)
                return _pausedUntil is { } until && until > _timeProvider.GetUtcNow() ? _pauseReason : null;
        }
    }

    /// <summary>
    /// When the pause runs out, while <see cref="IsPaused"/>.
    /// </summary>
    public DateTimeOffset? ResumesAt
    {
        get
        {
            lock (_lock)
                return _pausedUntil is { } until && until > _timeProvider.GetUtcNow() ? until : null;
        }
    }

    /// <summary>
    /// Pauses the plugin's work for a while, or for longer when it is already
    /// paused for less.
    /// </summary>
    /// <param name="duration">How long to pause for.</param>
    /// <param name="reason">Why, for the core to show.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is empty.</exception>
    public void Pause(TimeSpan duration, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (duration <= TimeSpan.Zero)
            return;

        lock (_lock)
        {
            var until = _timeProvider.GetUtcNow() + duration;
            if (_pausedUntil is { } current && current >= until && string.Equals(_pauseReason, reason, StringComparison.Ordinal))
                return;

            if (_pausedUntil is not { } existing || existing < until)
                _pausedUntil = until;
            _pauseReason = reason;
            _pauseTimer?.Dispose();
            _pauseTimer = _timeProvider.CreateTimer(_ => Expire(), null, _pausedUntil.Value - _timeProvider.GetUtcNow(), Timeout.InfiniteTimeSpan);
        }

        PauseStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Lifts the pause at once, such as when the key it was for was replaced.
    /// </summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (_pausedUntil is null)
                return;

            _pausedUntil = null;
            _pauseReason = null;
            _pauseTimer?.Dispose();
            _pauseTimer = null;
        }

        PauseStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Expire()
    {
        lock (_lock)
        {
            if (_pausedUntil is not { } until)
                return;

            // A timer that fired early waits out the rest of the pause.
            var remaining = until - _timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                _pauseTimer?.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            _pausedUntil = null;
            _pauseReason = null;
            _pauseTimer?.Dispose();
            _pauseTimer = null;
        }

        PauseStateChanged?.Invoke(this, EventArgs.Empty);
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
    public void Dispose()
    {
        _pauseTimer?.Dispose();
        _concurrencySemaphore.Dispose();
    }
}
