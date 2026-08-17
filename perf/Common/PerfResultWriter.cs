using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jellyfin.PerfTests.Common;

/// <summary>Serializes a run's <see cref="GateResult"/> list to the standardized JSON schema.</summary>
public static class PerfResultWriter
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write(string outputPath, string gitRef, string? sha, List<GateResult> results, string layer = "layer1-queryplan")
    {
        var run = new PerfRunResult
        {
            Run = new RunInfo
            {
                Ref = gitRef,
                Sha = sha,
                Timestamp = DateTime.UtcNow.ToString("O"),
                Layer = layer,
                Host = new Dictionary<string, object?>
                {
                    ["os"] = Environment.OSVersion.Platform.ToString(),
                    ["cpuCount"] = Environment.ProcessorCount,
                    ["note"] = "informational only, never used for pass/fail",
                },
            },
            Results = results,
        };

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(run, _jsonOptions));
    }
}
