using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.PerfTests.QueryPlan;

/// <summary>
/// Captures the last SELECT command EF actually sent to SQLite — command text and bound
/// parameter values — so QueryPlanAssert can re-run it prefixed with EXPLAIN QUERY PLAN.
/// DbContext.Database.GetCommandText()/ToQueryString() isn't used because its parameter
/// declaration rendering isn't guaranteed to be valid, re-executable SQL for every provider;
/// this captures the literal command SQLite received instead.
/// </summary>
public sealed class CommandCapturingInterceptor : DbCommandInterceptor
{
    public string? LastCommandText { get; private set; }

    public DbParameterCollection? LastParameters { get; private set; }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        LastCommandText = command.CommandText;
        LastParameters = command.Parameters;
        return base.ReaderExecuted(command, eventData, result);
    }
}
