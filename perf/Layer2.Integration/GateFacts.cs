using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.PerfTests.Common;
using Jellyfin.PerfTests.Integration;
using Jellyfin.PerfTests.Integration.Scenarios;
using Xunit;

namespace Jellyfin.PerfTests.Integration.Facts;

/// <summary>
/// Layer 2's actual entry point. Run via `dotnet test` from this directory (not `dotnet run`,
/// and not from elsewhere — see SeededHostFixture.cs's comment on why). One consolidated Fact
/// boots a single real host, runs every scenario against it in sequence, and writes the
/// standardized JSON as a side effect — perf/run-perf.sh reads PERF_OUT/PERF_GIT_REF from the
/// environment to control where that lands and what ref to record, since there's no `dotnet run`
/// arg list to pass flags through here.
/// </summary>
public sealed class GateFacts
{
    [Fact]
    public async Task RunAllAndWriteResults()
    {
        var outputPath = Environment.GetEnvironmentVariable("PERF_OUT") ?? "layer2-result.json";
        var gitRef = Environment.GetEnvironmentVariable("PERF_GIT_REF") ?? "unknown";
        var sha = Environment.GetEnvironmentVariable("PERF_GIT_SHA");

        var results = new List<GateResult>();

        await using (var host = await SeededHostFixture.CreateAsync())
        {
            results.Add(await SearchScenario.Run(host));
            results.Add(await ResumableItemsScenario.Run(host));
        }

        PerfResultWriter.Write(outputPath, gitRef, sha, results, "layer2-integration");

        var failed = 0;
        foreach (var r in results)
        {
            if (r.Status == GateStatus.Fail && r.Gating)
            {
                failed++;
            }
        }

        // Don't hard-fail the xunit run on a gating failure: a real, expected result (e.g. a
        // regression this layer is specifically built to catch) should still let JSON get
        // written and be visible in perf/compare's diff, not just show up as a red xunit test
        // with no artifact. run-perf.sh's own --fail-on-regression path is what gates CI.
        if (failed > 0)
        {
            Console.WriteLine($"{failed} gating failure(s) — see {Path.GetFullPath(outputPath)}");
        }
    }
}
