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
    private const int SeriesCount = 20;

    /// <summary>
    /// <paramref name="libraryId"/> is the seeded <c>CollectionFolder</c>'s item id (see
    /// <see cref="SeededHostFixture.EnsureLibraryAsync"/>) — <c>TopParentId</c> is the physical
    /// library root every item under it shares, not a per-series grouping key, per this
    /// session's trace of <c>LibraryManager.AddUserToQuery</c>/<c>ApplyTopParentFiltering</c>.
    /// Series grouping for cardinality purposes still comes from <c>SeriesPresentationUniqueKey</c>.
    /// </summary>
    public static IReadOnlyList<BaseItemEntity> BuildEpisodes(int count, Guid libraryId)
    {
        var random = new Random(DefaultSeed);
        var items = new List<BaseItemEntity>(count);
        var seriesKeys = new List<Guid>();
        for (var i = 0; i < SeriesCount; i++)
        {
            seriesKeys.Add(Guid.NewGuid());
        }

        for (var i = 0; i < count; i++)
        {
            var seriesIndex = i % seriesKeys.Count;
            items.Add(new BaseItemEntity
            {
                Id = Guid.NewGuid(),
                Type = "MediaBrowser.Controller.Entities.TV.Episode",
                Name = $"Episode {i}",
                CleanName = $"episode {i}",
                ParentId = libraryId,
                TopParentId = libraryId,
                SeriesName = $"Series {seriesIndex}",
                SeriesPresentationUniqueKey = seriesKeys[seriesIndex].ToString("N"),
                MediaType = "Video",
                IsVirtualItem = false,
                IsFolder = false,
                DateCreated = DateTime.UtcNow.AddMinutes(-random.Next(0, 500000)),
            });
        }

        return items;
    }
}
