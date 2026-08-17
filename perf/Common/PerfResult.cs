using System.Collections.Generic;

namespace Jellyfin.PerfTests.Common;

/// <summary>
/// The kind of measurement backing a <see cref="GateResult"/>. Determines how
/// perf/compare/compare.py diffs two runs of the same gate id.
/// </summary>
public static class ResultKind
{
    /// <summary>Host-independent: does the query use the expected index, or scan the table.</summary>
    public const string QueryPlan = "queryplan";

    /// <summary>Host-independent: same-process ratio between two code paths (e.g. optimized/baseline).</summary>
    public const string Ratio = "ratio";

    /// <summary>Host-independent: a count of logical operations (rows read, round trips), not time.</summary>
    public const string OperationCount = "operationcount";

    /// <summary>NOT host-independent. Always gating:false — informational/trend-watching only.</summary>
    public const string Timing = "timing";
}

/// <summary>Pass/fail/absent status for a single gate result.</summary>
public static class GateStatus
{
    public const string Pass = "pass";
    public const string Fail = "fail";

    /// <summary>
    /// The thing being checked doesn't exist on this ref yet (e.g. the FTS5 gate against a
    /// pre-migration baseline). Distinct from Fail: this is expected on some refs, not a bug.
    /// </summary>
    public const string NotApplicable = "not_applicable";

    public const string Error = "error";
}

/// <summary>One gate's result, matching perf/schema/perf-result.schema.json.</summary>
public sealed class GateResult
{
    public required string Id { get; init; }

    public required string Description { get; init; }

    public required string Kind { get; init; }

    public required string Status { get; init; }

    /// <summary>False for Kind == Timing: informational only, never fails perf/compare/compare.py --fail-on-regression.</summary>
    public required bool Gating { get; init; }

    public Dictionary<string, object?> Metrics { get; init; } = new();

    public Dictionary<string, object?> Details { get; init; } = new();
}

/// <summary>The full standardized output for one layer's run against one git ref.</summary>
public sealed class PerfRunResult
{
    public string SchemaVersion { get; init; } = "1.0";

    public required RunInfo Run { get; init; }

    public required List<GateResult> Results { get; init; }
}

public sealed class RunInfo
{
    public required string Ref { get; init; }

    public string? Sha { get; init; }

    public required string Timestamp { get; init; }

    public Dictionary<string, object?> Host { get; init; } = new();

    public required string Layer { get; init; }
}
