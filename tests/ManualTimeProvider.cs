using System;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when the test tells it
/// to, so rate limiter refills can be asserted without a real wait.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
