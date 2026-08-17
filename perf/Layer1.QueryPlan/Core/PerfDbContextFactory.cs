using System;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.PerfTests.QueryPlan;

/// <summary>
/// Builds an in-memory SQLite <see cref="JellyfinDbContext"/> through real EF Core migrations
/// (<see cref="Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder"/>), unlike every
/// existing test in tests/Jellyfin.Server.Implementations.Tests, which uses
/// <c>Database.EnsureCreated()</c> — a call that builds schema purely from the current EF model
/// snapshot and silently skips every migration-only raw SQL statement (FTS5 virtual
/// tables/triggers, partial/covering indexes). Query-plan gates need the real schema a
/// production server would actually have.
/// </summary>
public sealed class PerfDbContextFactory : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;

    public RoundTripCountingInterceptor RoundTrips { get; } = new();

    public CommandCapturingInterceptor CommandCapture { get; } = new();

    public PerfDbContextFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        // Same MigrationsAssembly wiring SqliteDatabaseProvider.cs uses in production, so
        // Migrate() finds and applies the real Migrations/ folder, not just the model snapshot.
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection, o => o.MigrationsAssembly(typeof(SqliteDatabaseProvider).Assembly.GetName().Name))
            .AddInterceptors(RoundTrips, CommandCapture)
            .Options;

        using var ctx = CreateContext();
        ctx.Database.Migrate();
    }

    public JellyfinDbContext CreateContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
