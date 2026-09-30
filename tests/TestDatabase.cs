using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shoko.Plugin.Tvdb.Storage;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The plugin's database in memory, migrated the way the server migrates it,
/// for as long as the instance lives.
/// </summary>
internal sealed class TestDatabase : IDbContextFactory<TvdbDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private readonly DbContextOptions<TvdbDbContext> _options;

    public TestDatabase()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<TvdbDbContext>().UseSqlite(_connection).Options;
        using var context = CreateDbContext();
        context.Database.Migrate();
    }

    public TvdbDbContext CreateDbContext()
        => new(_options);

    public void Dispose()
        => _connection.Dispose();
}
