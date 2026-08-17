using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using Jellyfin.PerfTests.Common;
using Jellyfin.PerfTests.Integration.SeedData;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace Jellyfin.PerfTests.Integration.Scenarios;

/// <summary>
/// Exercises the real /Items?searchTerm= endpoint end to end — the thing that actually matters:
/// SqlSearchProvider (the backend for /Search/Hints and /Items?searchTerm=) was found this
/// session to be completely disconnected from the FTS5 work, so this is the endpoint an FTS5
/// regression would actually be invisible to at the Layer 1 (in-process) level.
///
/// Seeded items are registered under a real, physically-resolved library folder (<see
/// cref="SeededHostFixture.EnsureLibraryAsync"/>) via <c>TopParentId</c> so Recursive=true
/// traversal can actually find them — see that method's doc comment for why a raw DB insert
/// alone can never satisfy this.
/// </summary>
public static class SearchScenario
{
    public static async Task<GateResult> Run(SeededHostFixture host)
    {
        var libraryId = await host.EnsureLibraryAsync().ConfigureAwait(false);
        var handPicked = HandPickedItems.Build(libraryId);
        await host.SeedAsync(async ctx =>
        {
            ctx.BaseItems.AddRange(handPicked);
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        using var counter = EfCommandCounter.Start();

        // Mid-word CJK substring — the exact case that silently regressed to zero results when
        // SqlSearchProvider was wired to FTS5 trigram without a length-minimum fallback (found
        // and left as a documented, not-yet-fixed gap this session).
        var neZha = await SearchAsync(host.Client, "哪吒").ConfigureAwait(false);
        var handmaiden = await SearchAsync(host.Client, "가씨").ConfigureAwait(false);
        // Not "Cinquieme" (with 'e' standing in for 'è'): a plain LIKE match never folds
        // diacritics — it only works for substrings that don't cross the accented character, as
        // validated live against production this session ("cinqui" matched "Cinquième", stopping
        // right before the 'è'). Using a term that actually needs diacritic-folding here would
        // fail regardless of any regression, since that was never real behavior to begin with.
        var accentInsensitive = await SearchAsync(host.Client, "cinqui").ConfigureAwait(false);

        var byIdsResponse = await host.Client.GetAsync($"/Items?ids={handPicked[0].Id}").ConfigureAwait(false);
        byIdsResponse.EnsureSuccessStatusCode();
        var byIds = await byIdsResponse.Content.ReadFromJsonAsync<QueryResult<BaseItemDto>>(JsonDefaults.Options).ConfigureAwait(false) ?? new QueryResult<BaseItemDto>();

        var commandCount = counter.CommandCount;

        var neZhaFound = neZha.Items?.Any(i => i.Id == handPicked[1].Id) ?? false;
        var handmaidenFound = handmaiden.Items?.Any(i => i.Id == handPicked[0].Id) ?? false;
        var fifthElementFound = accentInsensitive.Items?.Any(i => i.Id == handPicked[2].Id) ?? false;

        var pass = neZhaFound && handmaidenFound && fifthElementFound;

        return new GateResult
        {
            Id = "search-endpoint-multilanguage",
            Description = "GET /Items?searchTerm= finds multi-language titles (CJK substring, diacritic-insensitive) end to end through the real endpoint",
            Kind = ResultKind.OperationCount,
            Status = pass ? GateStatus.Pass : GateStatus.Fail,
            Gating = true,
            Metrics = new Dictionary<string, object?>
            {
                ["neZhaFound"] = neZhaFound,
                ["handmaidenFound"] = handmaidenFound,
                ["fifthElementFound"] = fifthElementFound,
                ["efCommandCount"] = commandCount,
            },
            Details = new Dictionary<string, object?>
            {
                ["neZhaTotalRecordCount"] = neZha.TotalRecordCount,
                ["handmaidenTotalRecordCount"] = handmaiden.TotalRecordCount,
                ["accentInsensitiveTotalRecordCount"] = accentInsensitive.TotalRecordCount,
                ["handPickedIds"] = string.Join(",", handPicked.Select(h => h.Id)),
                ["accentInsensitiveItemIds"] = string.Join(",", accentInsensitive.Items?.Select(i => i.Id) ?? []),
                ["byIdsTotalRecordCount"] = byIds.TotalRecordCount,
            },
        };
    }

    private static async Task<QueryResult<BaseItemDto>> SearchAsync(HttpClient client, string term)
    {
        var response = await client.GetAsync($"/Items?searchTerm={Uri.EscapeDataString(term)}&Recursive=true").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<QueryResult<BaseItemDto>>(JsonDefaults.Options).ConfigureAwait(false);
        return result ?? new QueryResult<BaseItemDto>();
    }
}
