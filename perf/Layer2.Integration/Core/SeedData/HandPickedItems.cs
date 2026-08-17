using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.PerfTests.Integration.SeedData;

/// <summary>
/// The exact real multi-language edge cases found in the production library this session
/// (Korean, Chinese, French with diacritics) — hand-entered rather than generated, since their
/// entire value is being the *specific* strings that broke things (short CJK substring search,
/// diacritic-insensitive matching), not representative volume.
/// </summary>
public static class HandPickedItems
{
    /// <summary>
    /// <paramref name="libraryId"/> is the seeded <c>CollectionFolder</c>'s item id (see
    /// <see cref="SeededHostFixture.EnsureLibraryAsync"/>) — without it set as both
    /// <c>ParentId</c>/<c>TopParentId</c>, these items are queryable by direct id but invisible
    /// to <c>Recursive=true</c> traversal, which filters on the <c>TopParentId</c> column.
    /// </summary>
    public static IReadOnlyList<BaseItemEntity> Build(Guid libraryId)
    {
        return new[]
        {
            Movie(libraryId, "The Handmaiden", "the handmaiden", "아가씨"),
            Movie(libraryId, "Ne Zha", "ne zha", "哪吒之魔童降世"),
            Movie(libraryId, "The Fifth Element", "the fifth element", "Le Cinquième Élément"),
            Movie(libraryId, "Shogun", "shogun", "Shōgun"),
        };
    }

    private static BaseItemEntity Movie(Guid libraryId, string name, string cleanName, string originalTitle) => new()
    {
        Id = Guid.NewGuid(),
        Type = "MediaBrowser.Controller.Entities.Movies.Movie",
        Name = name,
        CleanName = cleanName,
        OriginalTitle = originalTitle,
        ParentId = libraryId,
        TopParentId = libraryId,
        IsVirtualItem = false,
        IsFolder = false,
        MediaType = "Video",
        DateCreated = DateTime.UtcNow,
    };
}
