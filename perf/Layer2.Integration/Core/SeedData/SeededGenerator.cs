using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.PerfTests.Integration.SeedData;

/// <summary>
/// Deterministic (fixed-seed System.Random) bulk volume generator, so the covering
/// indexes/GROUP BY queries this layer exercises see realistic cardinality — an empty or
/// single-row table doesn't give SQLite's planner anything meaningful to choose between. No new
/// dependency (Bogus) needed for this: reproducibility, not realistic prose, is what matters
/// here.
/// </summary>
public static class SeededGenerator
{
    private const int DefaultSeed = 424242;

    public static IReadOnlyList<BaseItemEntity> BuildEpisodes(int count, out IReadOnlyList<Guid> seriesTopParentIds)
    {
        var random = new Random(DefaultSeed);
        var items = new List<BaseItemEntity>(count);
        var topParentIds = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            topParentIds.Add(Guid.NewGuid());
        }

        for (var i = 0; i < count; i++)
        {
            var seriesIndex = i % topParentIds.Count;
            items.Add(new BaseItemEntity
            {
                Id = Guid.NewGuid(),
                Type = "MediaBrowser.Controller.Entities.TV.Episode",
                Name = $"Episode {i}",
                CleanName = $"episode {i}",
                TopParentId = topParentIds[seriesIndex],
                SeriesName = $"Series {seriesIndex}",
                SeriesPresentationUniqueKey = topParentIds[seriesIndex].ToString("N"),
                MediaType = "Video",
                IsVirtualItem = false,
                IsFolder = false,
                DateCreated = DateTime.UtcNow.AddMinutes(-random.Next(0, 500000)),
            });
        }

        seriesTopParentIds = topParentIds;
        return items;
    }
}
