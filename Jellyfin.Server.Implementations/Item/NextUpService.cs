#pragma warning disable RS0030 // Do not use banned APIs

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.EntityFrameworkCore;
using BaseItemDto = MediaBrowser.Controller.Entities.BaseItem;

namespace Jellyfin.Server.Implementations.Item;

/// <summary>
/// Provides next-up episode query operations.
/// </summary>
public class NextUpService : INextUpService
{
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IItemTypeLookup _itemTypeLookup;
    private readonly IItemQueryHelpers _queryHelpers;

    /// <summary>
    /// Initializes a new instance of the <see cref="NextUpService"/> class.
    /// </summary>
    /// <param name="dbProvider">The database context factory.</param>
    /// <param name="itemTypeLookup">The item type lookup.</param>
    /// <param name="queryHelpers">The shared query helpers.</param>
    public NextUpService(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IItemTypeLookup itemTypeLookup,
        IItemQueryHelpers queryHelpers)
    {
        _dbProvider = dbProvider;
        _itemTypeLookup = itemTypeLookup;
        _queryHelpers = queryHelpers;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNextUpSeriesKeys(InternalItemsQuery filter, DateTime dateCutoff)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(filter.User);

        using var context = _dbProvider.CreateDbContext();

        var userId = filter.User.Id;
        var episodeType = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];

        // Step 1: materialise the user's watch history filtered to the cutoff window.
        // IX_UserData_UserId_ItemId_LastPlayedDate is a covering index, so this is a
        // tight range scan on a small result (~recently-watched items only).
        // Doing this as a separate round-trip prevents SQLite from flattening the join
        // and reverting to a full 15k-episode scan of BaseItems.
        var watchedData = context.UserData
            .AsNoTracking()
            .Where(u => u.UserId == userId
                     && u.ItemId != EF.Constant(BaseItemRepository.PlaceholderId)
                     && u.LastPlayedDate >= dateCutoff)
            .Select(u => new { u.ItemId, u.LastPlayedDate })
            .ToList();

        if (watchedData.Count == 0)
        {
            return Array.Empty<string>();
        }

        var watchedIds = watchedData.Select(w => w.ItemId).ToList();
        var lastPlayedByItem = watchedData.ToDictionary(w => w.ItemId, w => w.LastPlayedDate);

        // Step 2: look up only those specific episodes in BaseItems (primary-key IN lookup).
        var episodeSeries = context.BaseItems
            .AsNoTracking()
            .Where(b => watchedIds.Contains(b.Id)
                     && b.Type == episodeType
                     && filter.TopParentIds.Contains(b.TopParentId!.Value)
                     && b.SeriesPresentationUniqueKey != null)
            .Select(b => new { b.Id, b.SeriesPresentationUniqueKey })
            .ToList();

        // Step 3: group and order in memory — result set is small at this point.
        var grouped = episodeSeries
            .GroupBy(e => e.SeriesPresentationUniqueKey!)
            .Select(g => new
            {
                Key = g.Key,
                LastPlayed = g.Max(e => lastPlayedByItem.GetValueOrDefault(e.Id))
            })
            .OrderByDescending(g => g.LastPlayed)
            .Select(g => g.Key);

        if (filter.Limit.HasValue)
        {
            grouped = grouped.Take(filter.Limit.Value);
        }

        return grouped.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, NextUpEpisodeBatchResult> GetNextUpEpisodesBatch(
        InternalItemsQuery filter,
        IReadOnlyList<string> seriesKeys,
        bool includeSpecials,
        bool includeWatchedForRewatching)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(filter.User);

        if (seriesKeys.Count == 0)
        {
            return new Dictionary<string, NextUpEpisodeBatchResult>();
        }

        _queryHelpers.PrepareFilterQuery(filter);
        using var context = _dbProvider.CreateDbContext();

        var userId = filter.User.Id;
        var episodeTypeName = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];

        var lastWatchedBase = context.BaseItems
            .AsNoTracking()
            .Where(e => e.Type == episodeTypeName)
            .Where(e => e.SeriesPresentationUniqueKey != null && seriesKeys.Contains(e.SeriesPresentationUniqueKey))
            .Where(e => e.ParentIndexNumber != 0)
            .Where(e => e.UserData!.Any(ud => ud.UserId == userId && ud.Played));
        lastWatchedBase = _queryHelpers.ApplyAccessFiltering(context, lastWatchedBase, filter);

        // Use lightweight projection + client-side dedup to avoid the correlated scalar subquery
        // per group that EF generates for GroupBy+OrderByDescending+FirstOrDefault.
        var allPlayedLite = lastWatchedBase
            .Select(e => new
            {
                e.Id,
                e.SeriesPresentationUniqueKey,
                e.ParentIndexNumber,
                e.IndexNumber
            })
            .ToList();

        var lastWatchedInfo = allPlayedLite
            .OrderByDescending(e => e.ParentIndexNumber)
            .ThenByDescending(e => e.IndexNumber)
            .DistinctBy(e => e.SeriesPresentationUniqueKey)
            .ToDictionary(e => e.SeriesPresentationUniqueKey!, e => e.Id);

        Dictionary<string, Guid> lastWatchedByDateInfo = new();
        if (includeWatchedForRewatching)
        {
            var lastWatchedByDateBase = context.BaseItems
                .AsNoTracking()
                .Where(e => e.Type == episodeTypeName)
                .Where(e => e.SeriesPresentationUniqueKey != null && seriesKeys.Contains(e.SeriesPresentationUniqueKey))
                .Where(e => e.ParentIndexNumber != 0);
            lastWatchedByDateBase = _queryHelpers.ApplyAccessFiltering(context, lastWatchedByDateBase, filter);

            // Use an explicit Join (INNER JOIN) instead of SelectMany on a collection navigation.
            // SelectMany on UserData with a correlated Where would translate to APPLY,
            // which SQLite does not support.
            var playedWithDates = lastWatchedByDateBase
                .Join(
                    context.UserData
                        .AsNoTracking()
                        .Where(ud => ud.ItemId != EF.Constant(BaseItemRepository.PlaceholderId))
                        .Where(ud => ud.Played),
                    e => new { UserId = userId, ItemId = e.Id },
                    ud => new { ud.UserId, ud.ItemId },
                    (e, ud) => new { EpisodeId = e.Id, e.SeriesPresentationUniqueKey, ud.LastPlayedDate })
                .ToList();

            lastWatchedByDateInfo = playedWithDates
                .OrderByDescending(x => x.LastPlayedDate)
                .DistinctBy(x => x.SeriesPresentationUniqueKey)
                .ToDictionary(x => x.SeriesPresentationUniqueKey!, x => x.EpisodeId);
        }

        var allLastWatchedIds = lastWatchedInfo.Values
            .Concat(lastWatchedByDateInfo.Values)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var lwQuery = context.BaseItems.AsNoTracking().Where(e => allLastWatchedIds.Contains(e.Id));
        lwQuery = _queryHelpers.ApplyNavigations(lwQuery, filter);
        var lastWatchedEpisodes = lwQuery.ToDictionary(e => e.Id);

        Dictionary<string, List<BaseItemEntity>> specialsBySeriesKey = new();
        if (includeSpecials)
        {
            var specialsQuery = context.BaseItems
                .AsNoTracking()
                .Where(e => e.Type == episodeTypeName)
                .Where(e => e.SeriesPresentationUniqueKey != null && seriesKeys.Contains(e.SeriesPresentationUniqueKey))
                .Where(e => e.ParentIndexNumber == 0)
                .Where(e => !e.IsVirtualItem);
            specialsQuery = _queryHelpers.ApplyAccessFiltering(context, specialsQuery, filter);
            specialsQuery = _queryHelpers.ApplyNavigations(specialsQuery, filter).AsSingleQuery();

            foreach (var special in specialsQuery)
            {
                var key = special.SeriesPresentationUniqueKey!;
                if (!specialsBySeriesKey.TryGetValue(key, out var list))
                {
                    list = new List<BaseItemEntity>();
                    specialsBySeriesKey[key] = list;
                }

                list.Add(special);
            }
        }

        var positionLookup = new Dictionary<string, (int Season, int Episode)>();
        foreach (var kvp in lastWatchedInfo)
        {
            if (kvp.Value != Guid.Empty
                && lastWatchedEpisodes.TryGetValue(kvp.Value, out var lw)
                && lw.ParentIndexNumber.HasValue
                && lw.IndexNumber.HasValue)
            {
                positionLookup[kvp.Key] = (lw.ParentIndexNumber.Value, lw.IndexNumber.Value);
            }
        }

        var allUnplayedBase = context.BaseItems
            .AsNoTracking()
            .Where(e => e.Type == episodeTypeName)
            .Where(e => e.SeriesPresentationUniqueKey != null && seriesKeys.Contains(e.SeriesPresentationUniqueKey))
            .Where(e => e.ParentIndexNumber != 0)
            .Where(e => !e.IsVirtualItem)
            .Where(e => !e.UserData!.Any(ud => ud.UserId == userId && ud.Played));
        allUnplayedBase = _queryHelpers.ApplyAccessFiltering(context, allUnplayedBase, filter);
        var allUnplayedCandidates = allUnplayedBase
            .Select(e => new
            {
                e.Id,
                e.SeriesPresentationUniqueKey,
                e.ParentIndexNumber,
                EpisodeNumber = e.IndexNumber
            })
            .ToList();

        var nextEpisodeIds = new HashSet<Guid>();
        var seriesNextIdMap = new Dictionary<string, Guid>();

        foreach (var seriesKey in seriesKeys)
        {
            var candidates = allUnplayedCandidates
                .Where(c => c.SeriesPresentationUniqueKey == seriesKey);

            if (positionLookup.TryGetValue(seriesKey, out var pos))
            {
                candidates = candidates.Where(c =>
                    c.ParentIndexNumber > pos.Season
                    || (c.ParentIndexNumber == pos.Season && c.EpisodeNumber > pos.Episode));
            }

            var nextCandidate = candidates
                .OrderBy(c => c.ParentIndexNumber)
                .ThenBy(c => c.EpisodeNumber)
                .FirstOrDefault();

            if (nextCandidate is not null && nextCandidate.Id != Guid.Empty)
            {
                nextEpisodeIds.Add(nextCandidate.Id);
                seriesNextIdMap[seriesKey] = nextCandidate.Id;
            }
        }

        var seriesNextPlayedIdMap = new Dictionary<string, Guid>();
        if (includeWatchedForRewatching)
        {
            var allPlayedBase = context.BaseItems
                .AsNoTracking()
                .Where(e => e.Type == episodeTypeName)
                .Where(e => e.SeriesPresentationUniqueKey != null && seriesKeys.Contains(e.SeriesPresentationUniqueKey))
                .Where(e => e.ParentIndexNumber != 0)
                .Where(e => !e.IsVirtualItem)
                .Where(e => e.UserData!.Any(ud => ud.UserId == userId && ud.Played));
            allPlayedBase = _queryHelpers.ApplyAccessFiltering(context, allPlayedBase, filter);
            var allPlayedCandidates = allPlayedBase
                .Select(e => new
                {
                    e.Id,
                    e.SeriesPresentationUniqueKey,
                    e.ParentIndexNumber,
                    EpisodeNumber = e.IndexNumber
                })
                .ToList();

            foreach (var seriesKey in seriesKeys)
            {
                if (!lastWatchedByDateInfo.TryGetValue(seriesKey, out var lastByDateId))
                {
                    continue;
                }

                var lastByDateEntity = lastWatchedEpisodes.GetValueOrDefault(lastByDateId);
                if (lastByDateEntity is null)
                {
                    continue;
                }

                var playedCandidates = allPlayedCandidates
                    .Where(c => c.SeriesPresentationUniqueKey == seriesKey);

                if (lastByDateEntity.ParentIndexNumber.HasValue && lastByDateEntity.IndexNumber.HasValue)
                {
                    var lastSeason = lastByDateEntity.ParentIndexNumber.Value;
                    var lastEp = lastByDateEntity.IndexNumber.Value;
                    playedCandidates = playedCandidates.Where(c =>
                        c.ParentIndexNumber > lastSeason
                        || (c.ParentIndexNumber == lastSeason && c.EpisodeNumber > lastEp));
                }

                var nextPlayedCandidate = playedCandidates
                    .OrderBy(c => c.ParentIndexNumber)
                    .ThenBy(c => c.EpisodeNumber)
                    .FirstOrDefault();

                if (nextPlayedCandidate is not null && nextPlayedCandidate.Id != Guid.Empty)
                {
                    nextEpisodeIds.Add(nextPlayedCandidate.Id);
                    seriesNextPlayedIdMap[seriesKey] = nextPlayedCandidate.Id;
                }
            }
        }

        var nextEpisodes = new Dictionary<Guid, BaseItemEntity>();
        if (nextEpisodeIds.Count > 0)
        {
            var nextQuery = context.BaseItems.AsNoTracking().Where(e => nextEpisodeIds.Contains(e.Id));
            nextQuery = _queryHelpers.ApplyNavigations(nextQuery, filter).AsSingleQuery();
            nextEpisodes = nextQuery.ToDictionary(e => e.Id);
        }

        var result = new Dictionary<string, NextUpEpisodeBatchResult>();
        foreach (var seriesKey in seriesKeys)
        {
            var batchResult = new NextUpEpisodeBatchResult();

            if (lastWatchedInfo.TryGetValue(seriesKey, out var lwId) && lwId != Guid.Empty)
            {
                if (lastWatchedEpisodes.TryGetValue(lwId, out var entity))
                {
                    batchResult.LastWatched = _queryHelpers.DeserializeBaseItem(entity, filter.SkipDeserialization);
                }
            }

            if (seriesNextIdMap.TryGetValue(seriesKey, out var nextId) && nextEpisodes.TryGetValue(nextId, out var nextEntity))
            {
                batchResult.NextUp = _queryHelpers.DeserializeBaseItem(nextEntity, filter.SkipDeserialization);
            }

            if (includeSpecials && specialsBySeriesKey.TryGetValue(seriesKey, out var specials))
            {
                batchResult.Specials = specials.Select(s => _queryHelpers.DeserializeBaseItem(s, filter.SkipDeserialization)!).ToList();
            }
            else
            {
                batchResult.Specials = Array.Empty<BaseItemDto>();
            }

            if (includeWatchedForRewatching)
            {
                if (lastWatchedByDateInfo.TryGetValue(seriesKey, out var lastByDateId) &&
                    lastWatchedEpisodes.TryGetValue(lastByDateId, out var lastByDateEntity))
                {
                    batchResult.LastWatchedForRewatching = _queryHelpers.DeserializeBaseItem(lastByDateEntity, filter.SkipDeserialization);
                }

                if (seriesNextPlayedIdMap.TryGetValue(seriesKey, out var nextPlayedId) &&
                    nextEpisodes.TryGetValue(nextPlayedId, out var nextPlayedEntity))
                {
                    batchResult.NextPlayedForRewatching = _queryHelpers.DeserializeBaseItem(nextPlayedEntity, filter.SkipDeserialization);
                }
            }

            result[seriesKey] = batchResult;
        }

        return result;
    }
}
