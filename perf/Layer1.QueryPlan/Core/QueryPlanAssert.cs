using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.PerfTests.QueryPlan;

/// <summary>
/// Captures SQLite's <c>EXPLAIN QUERY PLAN</c> output for a query, for host-independent
/// assertions on query *shape* (does it use the expected index, or scan the table) instead of
/// wall-clock timing. SQLite's query planner picks the same plan for the same schema/data
/// regardless of machine speed, so this is deterministic across any host.
/// </summary>
public static class QueryPlanAssert
{
    /// <summary>
    /// Gets the query plan for an EF Core LINQ query, by materializing it once (so EF actually
    /// sends a command to SQLite) and capturing the exact command text + bound parameter values
    /// via <see cref="CommandCapturingInterceptor"/> — not <c>ToQueryString()</c>, whose
    /// parameter-declaration rendering isn't guaranteed to be valid, directly re-executable SQL.
    /// The <see cref="JellyfinDbContext"/> passed in must come from a
    /// <see cref="PerfDbContextFactory"/> so its options include that interceptor.
    /// </summary>
    public static string GetPlan(PerfDbContextFactory factory, JellyfinDbContext context, IQueryable query)
    {
        // Force execution (non-generic enumeration, since the compile-time element type varies
        // per gate) so EF actually sends the command for CommandCapturingInterceptor to see.
        var enumerator = query.GetEnumerator();
        while (enumerator.MoveNext())
        {
        }
        return GetPlanFromCapture(context, factory.CommandCapture);
    }

    /// <summary>
    /// Gets the query plan for a raw SQL string (used for statements with no LINQ equivalent,
    /// e.g. the FTS5 MATCH query).
    /// </summary>
    public static string GetPlan(JellyfinDbContext context, string sql)
    {
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            connection.Open();
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            return ExecutePlan(cmd);
        }
        finally
        {
            if (wasClosed)
            {
                connection.Close();
            }
        }
    }

    private static string GetPlanFromCapture(JellyfinDbContext context, CommandCapturingInterceptor capture)
    {
        var commandText = capture.LastCommandText;
        if (string.IsNullOrEmpty(commandText))
        {
            return string.Empty;
        }

        var connection = (SqliteConnection)context.Database.GetDbConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + commandText;

        if (capture.LastParameters is not null)
        {
            foreach (SqliteParameter p in capture.LastParameters)
            {
                var clone = cmd.CreateParameter();
                clone.ParameterName = p.ParameterName;
                clone.Value = p.Value;
                cmd.Parameters.Add(clone);
            }
        }

        return ExecutePlan(cmd);
    }

    private static string ExecutePlan(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            // EXPLAIN QUERY PLAN columns: id, parent, notused, detail — "detail" is the
            // human-readable plan line ("SEARCH ... USING INDEX ...", "SCAN ...").
            lines.Add(reader.GetString(3));
        }

        return string.Join("\n", lines);
    }
}
