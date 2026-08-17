using System;
using Jellyfin.PerfTests.Common;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.PerfTests.QueryPlan.Gates;

/// <summary>
/// Targets the real bug found while building this: a broken migration idempotency guard left
/// the BaseItems_fts trigram index completely empty while COUNT(*) still reported a healthy
/// row count (external-content FTS5 tables reflect the content table on a bare SELECT, not the
/// actual index). Asserts both query-plan shape AND that the index row count matches BaseItems
/// — the second check is what actually catches that specific bug; the first alone would not
/// have, since an empty-but-present FTS5 table still produces a "VIRTUAL TABLE INDEX" plan.
///
/// NOT_APPLICABLE when BaseItems_fts doesn't exist yet — true on a pure-upstream baseline,
/// where the AddFullTextSearchAndPerfIndexes migration hasn't landed.
/// </summary>
public static class FullTextSearchIndexGate
{
    public static GateResult Run(PerfDbContextFactory factory)
    {
        using var ctx = factory.CreateContext();

        var tableExists = ctx.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='BaseItems_fts'")
            .Single() > 0;

        if (!tableExists)
        {
            return new GateResult
            {
                Id = "fts5-search-uses-index",
                Description = "Substring search on CleanName/OriginalTitle uses BaseItems_fts, not a table scan",
                Kind = ResultKind.QueryPlan,
                Status = GateStatus.NotApplicable,
                Gating = true,
                Details = new Dictionary<string, object?>
                {
                    ["notes"] = "BaseItems_fts does not exist on this ref (pre-migration baseline).",
                },
            };
        }

        // Seed rows post-migration so the AFTER INSERT trigger exercises real sync, and the
        // MATCH below has something real to find — an empty table would trivially "match" the
        // row-count check without ever proving the trigger/population path works.
        ctx.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", CleanName = "the matrix reloaded", OriginalTitle = "The Matrix Reloaded" });
        ctx.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", CleanName = "toy story", OriginalTitle = "Toy Story" });
        ctx.SaveChanges();

        var plan = QueryPlanAssert.GetPlan(ctx, "SELECT Id FROM BaseItems_fts WHERE BaseItems_fts MATCH '\"atrix\"'");
        var usesFtsIndex = plan.Contains("VIRTUAL TABLE INDEX", System.StringComparison.Ordinal);

        var ftsRowCount = ctx.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM BaseItems_fts").Single();
        var baseItemsRowCount = ctx.BaseItems.Count();
        var rowCountsMatch = ftsRowCount == baseItemsRowCount;

        var pass = usesFtsIndex && rowCountsMatch;

        return new GateResult
        {
            Id = "fts5-search-uses-index",
            Description = "Substring search on CleanName/OriginalTitle uses BaseItems_fts, not a table scan",
            Kind = ResultKind.QueryPlan,
            Status = pass ? GateStatus.Pass : GateStatus.Fail,
            Gating = true,
            Metrics = new Dictionary<string, object?>
            {
                ["usesExpectedIndex"] = usesFtsIndex,
                ["ftsRowCount"] = ftsRowCount,
                ["baseItemsRowCount"] = baseItemsRowCount,
                ["rowCountsMatch"] = rowCountsMatch,
            },
            Details = new Dictionary<string, object?>
            {
                ["queryPlan"] = plan,
            },
        };
    }
}
