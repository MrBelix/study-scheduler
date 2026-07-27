using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace StudyScheduler.API.Core.Persistence;

/// <summary>PostgreSQL error classification for <see cref="DbUpdateException"/> catch filters.</summary>
public static class SqlErrors
{
    /// <summary>
    /// True when the failed save violated a unique index or constraint (SQLSTATE 23505,
    /// <c>unique_violation</c>). Catch filters that treat "someone materialized this slot first" as
    /// a benign race must check this, or an FK violation (23503) or any other constraint failure
    /// would be mislabeled and swallowed instead of reaching the global exception handler.
    /// </summary>
    public static bool IsDuplicateKey(DbUpdateException exception)
    {
        // EF wraps the provider exception; walk to the innermost PostgresException in the chain.
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException postgres)
                return postgres.SqlState == PostgresErrorCodes.UniqueViolation;
        }

        return false;
    }
}
