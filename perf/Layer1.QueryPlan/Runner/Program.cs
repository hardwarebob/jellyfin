using System;
using Jellyfin.PerfTests.Common;
using System.Collections.Generic;
using Jellyfin.PerfTests.QueryPlan.Gates;

namespace Jellyfin.PerfTests.QueryPlan;

/// <summary>
/// Runs every Layer 1 gate against one fresh in-memory, fully-migrated database and writes the
/// standardized JSON result. Invoked by perf/run-perf.sh; `dotnet test` also works directly for
/// ad-hoc IDE/local pass-fail feedback via the [Fact]-wrapped copies in GateFacts.cs.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var outputPath = "results/layer1.json";
        var gitRef = Environment.GetEnvironmentVariable("PERF_GIT_REF") ?? "unknown";
        var sha = Environment.GetEnvironmentVariable("PERF_GIT_SHA");

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--out")
            {
                outputPath = args[i + 1];
            }
            else if (args[i] == "--ref")
            {
                gitRef = args[i + 1];
            }
        }

        using var factory = new PerfDbContextFactory();

        var results = new List<GateResult>
        {
            FullTextSearchIndexGate.Run(factory),
            ResumeFastPathGate.Run(factory),
            SplitQueryDispatchGate.Run(factory),
            RecentEpisodesOrderingGate.Run(factory),
            CoveringIndexNoHeapFetchGate.Run(factory),
        };

        PerfResultWriter.Write(outputPath, gitRef, sha, results);

        var failed = 0;
        foreach (var r in results)
        {
            var marker = r.Status switch
            {
                GateStatus.Pass => "PASS",
                GateStatus.NotApplicable => "N/A ",
                _ => "FAIL",
            };
            Console.WriteLine($"[{marker}] {r.Id} ({r.Kind})");
            if (r.Status == GateStatus.Fail && r.Gating)
            {
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Wrote {outputPath} — {results.Count} gates, {failed} gating failure(s).");

        return failed > 0 ? 1 : 0;
    }
}
