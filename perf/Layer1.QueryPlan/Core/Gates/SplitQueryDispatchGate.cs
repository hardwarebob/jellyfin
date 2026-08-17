using System;
using System.Collections.Generic;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.PerfTests.QueryPlan.Gates;

/// <summary>
/// Targets a real bug found while building this: LoadItems' unbounded/bounded split-query
/// dispatch condition was silently altered by a rebase, so it always took the split-query path
/// (2 round trips: id projection, then a bounded WhereOneOrMany re-query) even for unbounded
/// queries, where LoadItems' own doc comment says it should stay single-query (1 round trip) —
/// the per-id parameter/memory cost of the split path outweighs its row-explosion savings once
/// the id list can span the whole result set. Asserted via round-trip *count*
/// (host-independent — an integer, not a duration), not timing.
/// </summary>
public static class SplitQueryDispatchGate
{
    public static GateResult Run(PerfDbContextFactory factory)
    {
        var repository = BuildRepository(factory);

        factory.RoundTrips.Reset();
        repository.GetItemList(new InternalItemsQuery { Limit = 5 });
        var boundedRoundTrips = factory.RoundTrips.Count;

        factory.RoundTrips.Reset();
        repository.GetItemList(new InternalItemsQuery());
        var unboundedRoundTrips = factory.RoundTrips.Count;

        // Bounded/split: id-projection query, then AsSplitQuery() fans out to one further SELECT
        // *per* attached collection navigation (providers, images, user data, ...) — not a fixed
        // count, so assert "more than one round trip" rather than an exact number. Unbounded:
        // everything in one query via JOINs, so exactly one round trip.
        var pass = boundedRoundTrips > 1 && unboundedRoundTrips == 1;

        return new GateResult
        {
            Id = "split-query-dispatch-matches-bounded-state",
            Description = "Bounded queries use the multi-round-trip split-query path; unbounded queries stay single-query",
            Kind = ResultKind.OperationCount,
            Status = pass ? GateStatus.Pass : GateStatus.Fail,
            Gating = true,
            Metrics = new Dictionary<string, object?>
            {
                ["boundedRoundTrips"] = boundedRoundTrips,
                ["unboundedRoundTrips"] = unboundedRoundTrips,
                ["expectedBoundedRoundTripsMinimum"] = 2,
                ["expectedUnboundedRoundTrips"] = 1,
            },
        };
    }

    private static BaseItemRepository BuildRepository(PerfDbContextFactory factory)
    {
        var dbFactoryMock = new Mock<IDbContextFactory<JellyfinDbContext>>();
        dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(factory.CreateContext);

        var itemTypeLookup = new ItemTypeLookup();

        var serverConfigurationManager = new Mock<IServerConfigurationManager>();
        serverConfigurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        return new BaseItemRepository(
            dbFactoryMock.Object,
            new Mock<IServerApplicationHost>().Object,
            itemTypeLookup,
            serverConfigurationManager.Object,
            NullLogger<BaseItemRepository>.Instance);
    }
}
