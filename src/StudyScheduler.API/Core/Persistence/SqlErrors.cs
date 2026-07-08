using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace StudyScheduler.API.Core.Persistence;

/// <summary>SQL Server error classification for <see cref="DbUpdateException"/> catch filters.</summary>
public static class SqlErrors
{
    /// <summary>
    /// True when the failed save violated a unique index or constraint:
    /// 2601 = duplicate key row in a unique index, 2627 = violation of a UNIQUE/PK constraint.
    /// Catch filters that treat "someone materialized this slot first" as a benign race must
    /// check this, or an FK violation (547) or any other constraint failure would be mislabeled
    /// and swallowed instead of reaching the global exception handler.
    /// </summary>
    public static bool IsDuplicateKey(DbUpdateException exception)
    {
        // EF wraps the provider exception; walk to the innermost SqlException in the chain.
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException sql)
                return sql.Number is 2601 or 2627;
        }

        return false;
    }
}
