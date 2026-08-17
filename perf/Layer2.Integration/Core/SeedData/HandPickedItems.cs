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
    public static IReadOnlyList<BaseItemEntity> Build()
    {
        return new[]
        {
            Movie("The Handmaiden", "the handmaiden", "아가씨"),
            Movie("Ne Zha", "ne zha", "哪吒之魔童降世"),
            Movie("The Fifth Element", "the fifth element", "Le Cinquième Élément"),
            Movie("Shogun", "shogun", "Shōgun"),
        };
    }

    private static BaseItemEntity Movie(string name, string cleanName, string originalTitle) => new()
    {
        Id = Guid.NewGuid(),
        Type = "MediaBrowser.Controller.Entities.Movies.Movie",
        Name = name,
        CleanName = cleanName,
        OriginalTitle = originalTitle,
        IsVirtualItem = false,
        IsFolder = false,
        MediaType = "Video",
        DateCreated = DateTime.UtcNow,
    };
}
