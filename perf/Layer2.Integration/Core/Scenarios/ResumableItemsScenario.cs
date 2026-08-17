using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.PerfTests.Common;
using Jellyfin.PerfTests.Integration.SeedData;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.PerfTests.Integration.Scenarios;

/// <summary>
/// Exercises the real GET /Users/{userId}/Items/Resume endpoint — the resumability fast path
/// found silently bypassed this session, and the exact endpoint issue #17405/#17406 upstream
/// describes as taking 32-38s on a large real library with no automated test to catch a
/// regression before or after a fix.
///
/// Seeded episodes are registered under a real CollectionFolder (<see
/// cref="SeededHostFixture.EnsureLibraryAsync"/>) via <c>TopParentId</c>, same as
/// <see cref="SearchScenario"/> — see that method's doc comment for why this is required.
/// </summary>
public static class ResumableItemsScenario
{
    public static async Task<GateResult> Run(SeededHostFixture host)
    {
        var libraryId = await host.EnsureLibraryAsync().ConfigureAwait(false);
        var episodes = SeededGenerator.BuildEpisodes(500, libraryId);
        var inProgressIds = episodes.Take(10).Select(e => e.Id).ToList();

        await host.SeedAsync(async ctx =>
        {
            ctx.BaseItems.AddRange(episodes);
            await ctx.SaveChangesAsync().ConfigureAwait(false);

            // Raw SQL: UserData has required navigation properties (Item, User) that would need
            // fully-loaded tracked entities to satisfy via EF Add — unnecessary ceremony for
            // seeding, and the same lesson Layer 1 hit with the same entity.
            foreach (var itemId in inProgressIds)
            {
                await ctx.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO UserData (UserId, ItemId, CustomDataKey, PlaybackPositionTicks, Played, PlayCount, IsFavorite)
                    VALUES ({host.UserId}, {itemId}, {itemId.ToString("N")}, 12345, 0, 0, 0)
                    """).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        using var counter = EfCommandCounter.Start();

        var response = await host.Client.GetAsync($"/Users/{host.UserId}/Items/Resume?Limit=20").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<QueryResult<BaseItemDto>>(JsonDefaults.Options).ConfigureAwait(false)
            ?? new QueryResult<BaseItemDto>();

        var commandCount = counter.CommandCount;

        var returnedIds = (result.Items ?? []).Select(i => i.Id).ToHashSet();
        var allInProgressReturned = inProgressIds.All(id => returnedIds.Contains(id));
        var noExtraneousItems = returnedIds.Count <= inProgressIds.Count;

        var pass = allInProgressReturned && noExtraneousItems;

        return new GateResult
        {
            Id = "resume-endpoint-returns-in-progress-only",
            Description = "GET /Users/{userId}/Items/Resume returns exactly the seeded in-progress items, end to end through the real endpoint",
            Kind = ResultKind.OperationCount,
            Status = pass ? GateStatus.Pass : GateStatus.Fail,
            Gating = true,
            Metrics = new Dictionary<string, object?>
            {
                ["seededInProgressCount"] = inProgressIds.Count,
                ["returnedCount"] = returnedIds.Count,
                ["allInProgressReturned"] = allInProgressReturned,
                ["efCommandCount"] = commandCount,
            },
        };
    }
}
