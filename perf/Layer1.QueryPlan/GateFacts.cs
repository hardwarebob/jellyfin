using Jellyfin.PerfTests.QueryPlan.Gates;
using Xunit;

namespace Jellyfin.PerfTests.QueryPlan;

/// <summary>
/// Thin xunit wrappers around each gate for ad-hoc `dotnet test` / IDE test-explorer use.
/// perf/run-perf.sh does not use this class — it invokes Program.Main directly (`dotnet run`)
/// to get the standardized JSON output all gates in one pass.
/// </summary>
public sealed class GateFacts
{
    [Fact]
    public void FullTextSearchIndex()
    {
        using var factory = new PerfDbContextFactory();
        var result = FullTextSearchIndexGate.Run(factory);
        Assert.NotEqual(GateStatus.Fail, result.Status);
    }

    [Fact]
    public void ResumeFastPath()
    {
        using var factory = new PerfDbContextFactory();
        var result = ResumeFastPathGate.Run(factory);
        Assert.NotEqual(GateStatus.Fail, result.Status);
    }

    [Fact]
    public void SplitQueryDispatch()
    {
        using var factory = new PerfDbContextFactory();
        var result = SplitQueryDispatchGate.Run(factory);
        Assert.NotEqual(GateStatus.Fail, result.Status);
    }

    [Fact]
    public void RecentEpisodesOrdering()
    {
        using var factory = new PerfDbContextFactory();
        var result = RecentEpisodesOrderingGate.Run(factory);
        Assert.NotEqual(GateStatus.Fail, result.Status);
    }

    [Fact]
    public void CoveringIndexNoHeapFetch()
    {
        using var factory = new PerfDbContextFactory();
        var result = CoveringIndexNoHeapFetchGate.Run(factory);
        Assert.NotEqual(GateStatus.Fail, result.Status);
    }
}
