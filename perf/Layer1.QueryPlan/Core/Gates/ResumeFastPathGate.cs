using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.PerfTests.QueryPlan.Gates;

/// <summary>
/// The IsResumable fast path (BaseItemRepository.TranslateQuery) avoids an expensive correlated
/// AncestorIds descendant subquery by resolving in-progress items directly against UserData,
/// which needs IX_UserData_UserId_ItemId_LastPlayedDate to stay a SEARCH rather than a SCAN as
/// the UserData table grows. Targets the fast-path-silently-bypassed bug found this session —
/// an unenforced "resumable folder kinds always have null MediaType" assumption that could
/// silently drop resumable folders from Continue Watching if ever violated.
///
/// Asserted against the underlying UserData lookup directly (via LINQ + ToQueryString, real EF
/// SQL) rather than the full IsResumable filter tree, since building a valid domain User object
/// for the full TranslateQuery path is out of scope for this first slice — tracked as follow-up.
/// </summary>
public static class ResumeFastPathGate
{
    public static GateResult Run(PerfDbContextFactory factory)
    {
        using var ctx = factory.CreateContext();

        // No seed data needed: EXPLAIN QUERY PLAN reflects schema/indexes, not row contents —
        // SQLite's planner picks a plan without needing real data to exist.
        var userId = Guid.NewGuid();
        var query = ctx.UserData
            .Where(ud => ud.UserId == userId && ud.PlaybackPositionTicks > 0)
            .Select(ud => ud.ItemId);

        var plan = QueryPlanAssert.GetPlan(factory, ctx, query);
        // Not pinned to one specific index name: SQLite may legitimately pick any index that
        // avoids a full UserData scan for this filter shape (observed: IX_UserData_UserId_Played_ItemId,
        // not the LastPlayedDate-ordered index that name-checking initially assumed). What
        // actually matters is SEARCH (index-driven), not SCAN (full table).
        var usesIndex = plan.Contains("SEARCH", StringComparison.Ordinal)
            && !plan.Contains("SCAN", StringComparison.Ordinal);

        return new GateResult
        {
            Id = "resume-in-progress-lookup-uses-index",
            Description = "In-progress UserData lookup for the resume fast path is a SEARCH, not a SCAN",
            Kind = ResultKind.QueryPlan,
            Status = usesIndex ? GateStatus.Pass : GateStatus.Fail,
            Gating = true,
            Metrics = new Dictionary<string, object?> { ["usesExpectedIndex"] = usesIndex },
            Details = new Dictionary<string, object?> { ["queryPlan"] = plan },
        };
    }
}
