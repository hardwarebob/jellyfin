using System.Data.Common;
using System.Threading;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.PerfTests.QueryPlan;

/// <summary>
/// Counts distinct SELECT round trips executed against the database — a host-independent
/// operation-count proxy for "how many separate queries did this call issue", used to catch
/// regressions like the split-query dispatch silently always (or never) doing a second
/// id-projection round trip.
/// </summary>
public sealed class RoundTripCountingInterceptor : DbCommandInterceptor
{
    private int _count;

    public int Count => _count;

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        if (command.CommandText.TrimStart().StartsWith("SELECT", System.StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _count);
        }

        return base.ReaderExecuted(command, eventData, result);
    }
}
