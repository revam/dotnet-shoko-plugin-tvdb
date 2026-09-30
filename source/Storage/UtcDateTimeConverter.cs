using System;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Shoko.Plugin.Tvdb.Storage;

/// <summary>
/// Keeps every date in the database in UTC, and reads it back as UTC. SQLite
/// has no kind of its own, so without it a date reads back unspecified and a
/// later <see cref="DateTime.ToUniversalTime"/> would shift it.
/// </summary>
internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc)
);
