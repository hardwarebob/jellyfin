using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Extensions.Json;
using Jellyfin.Server.Integration.Tests;
using MediaBrowser.Model.Entities;
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

    public IServiceProvider Services => _factory.Services;

    private Guid? _libraryId;

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

    /// <summary>
    /// Creates a real library via the actual /Library/VirtualFolders API rather than a raw DB
    /// insert, and returns its <b>physical folder</b> item id — the value seeded items'
    /// <c>TopParentId</c> must match, confirmed by tracing <c>LibraryManager.AddUserToQuery</c> /
    /// <c>GetTopParentIdsForQuery</c>: for a <c>CollectionFolder</c> it returns
    /// <c>collectionFolder.PhysicalFolderIds</c>, which are the ids of the resolved on-disk media
    /// path Folder item(s) — a <i>different</i>, separate item from the CollectionFolder's own
    /// id. Two things are required for that resolution to succeed, both confirmed empirically:
    /// <list type="bullet">
    /// <item>a real <c>paths</c> entry — <c>CollectionFolder.GetPhysicalFolders</c> explicitly
    /// excludes the CollectionFolder's own on-disk directory from its physical locations, so a
    /// library created with zero configured media paths leaves <c>PhysicalFolderIds</c>
    /// permanently empty;</item>
    /// <item>that path must contain at least one entry — <c>LibraryManager.FindByPath</c> (the
    /// fallback <c>GetPhysicalParents</c> uses to resolve a configured path into a Folder item)
    /// does not resolve a completely empty top-level directory into anything; one placeholder
    /// subdirectory is enough, no real media file needed.</item>
    /// </list>
    /// Both are satisfied synchronously by the time <c>AddVirtualFolder</c>'s own internal
    /// (awaited) shallow, non-recursive <c>ValidateTopLibraryFolders</c> pass completes — no
    /// separate full library scan is needed. <c>refreshLibrary=false</c> still avoids the slow,
    /// non-deterministic background scan that flag would otherwise trigger. Cached after the
    /// first call so multiple scenarios can share one seeded library within a run.
    /// </summary>
    public async Task<Guid> EnsureLibraryAsync(string name = "PerfTestLibrary")
    {
        if (_libraryId is not null)
        {
            return _libraryId.Value;
        }

        var mediaPath = System.IO.Directory.CreateTempSubdirectory("jellyfin-perf-lib-").FullName;
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(mediaPath, "placeholder"));

        var addResponse = await Client.PostAsync(
            $"/Library/VirtualFolders?name={Uri.EscapeDataString(name)}&collectionType=mixed&refreshLibrary=false&paths={Uri.EscapeDataString(mediaPath)}",
            null).ConfigureAwait(false);
        addResponse.EnsureSuccessStatusCode();

        var listResponse = await Client.GetAsync("/Library/VirtualFolders").ConfigureAwait(false);
        listResponse.EnsureSuccessStatusCode();
        var folders = await listResponse.Content.ReadFromJsonAsync<List<VirtualFolderInfo>>(JsonDefaults.Options).ConfigureAwait(false)
            ?? [];
        var folder = folders.First(f => string.Equals(f.Name, name, StringComparison.Ordinal));
        var collectionFolderId = Guid.Parse(folder.ItemId);

        var libraryManager = Services.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>();
        var collectionFolder = libraryManager.GetItemById(collectionFolderId) as MediaBrowser.Controller.Entities.CollectionFolder;
        var physicalFolderId = collectionFolder?.PhysicalFolderIds.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "CollectionFolder.PhysicalFolderIds is empty after library creation — the configured media path did not resolve to a physical Folder item.");

        _libraryId = physicalFolderId;
        return _libraryId.Value;
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
