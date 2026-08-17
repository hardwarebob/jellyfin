using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.Integration.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.PerfTests.Integration;

/// <summary>
/// Boots a real, fully-migrated Jellyfin server (reusing JellyfinApplicationFactory/TestAppHost
/// from tests/Jellyfin.Server.Integration.Tests — the same class the repo's own integration
/// tests use, which goes through the real ApplyStartupMigrationAsync path) against a fresh temp
/// directory, completes the startup wizard, and seeds deterministic data directly via
/// IDbContextFactory&lt;JellyfinDbContext&gt; rather than a filesystem library scan — a real scan
/// pulls in ffprobe/metadata-provider network calls, which are slow, non-deterministic, and
/// orthogonal to what this layer measures (DB/query performance, not scan performance).
/// </summary>
public sealed class SeededHostFixture : IAsyncDisposable
{
    private readonly JellyfinApplicationFactory _innerFactory;
    private readonly WebApplicationFactory<Jellyfin.Server.Startup> _factory;

    public HttpClient Client { get; }

    public Guid UserId { get; private set; }

    public string AccessToken { get; private set; } = string.Empty;

    private SeededHostFixture(JellyfinApplicationFactory innerFactory, WebApplicationFactory<Jellyfin.Server.Startup> factory, HttpClient client)
    {
        _innerFactory = innerFactory;
        _factory = factory;
        Client = client;
    }

    public static async Task<SeededHostFixture> CreateAsync()
    {
        var innerFactory = new JellyfinApplicationFactory();

        // WebApplicationFactory<Startup>'s own content-root auto-detection breaks when run from
        // a project other than the one directly authoring [Fact]s against the web project — it
        // guesses a broken path unrelated to anything under our control (confirmed: not fixed by
        // matching Jellyfin.Server.Integration.Tests.csproj's exact reference shape, nor by
        // Environment.CurrentDirectory). WithWebHostBuilder + an explicit UseContentRoot is the
        // documented override; it returns a DelegatedWebApplicationFactory wrapper rather than
        // JellyfinApplicationFactory itself, but that wrapper still delegates to the inner
        // factory's own CreateHost override (real migrations) — only HttpClient/Services are
        // needed from it here, both available on the base WebApplicationFactory<T> type.
        var contentRoot = FindJellyfinServerContentRoot();
        var factory = innerFactory.WithWebHostBuilder(builder => builder.UseContentRoot(contentRoot));

        var client = factory.CreateClient();

        var fixture = new SeededHostFixture(innerFactory, factory, client);
        fixture.AccessToken = await AuthHelper.CompleteStartupAsync(client).ConfigureAwait(false);
        client.DefaultRequestHeaders.AddAuthHeader(fixture.AccessToken);

        var userDto = await AuthHelper.GetUserDtoAsync(client).ConfigureAwait(false);
        fixture.UserId = userDto.Id;

        return fixture;
    }

    /// <summary>
    /// Runs <paramref name="seed"/> against a real <see cref="JellyfinDbContext"/> resolved from
    /// the booted host's own DI container — the exact same context type/configuration
    /// production code uses, just written to directly instead of through the library-scan
    /// pipeline.
    /// </summary>
    public async Task SeedAsync(Func<JellyfinDbContext, Task> seed)
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        await seed(context).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _factory.Dispose();
        _innerFactory.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string FindJellyfinServerContentRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "Jellyfin.Server");
            if (System.IO.File.Exists(System.IO.Path.Combine(candidate, "Jellyfin.Server.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Could not locate Jellyfin.Server/ by walking up from {AppContext.BaseDirectory}");
    }
}
