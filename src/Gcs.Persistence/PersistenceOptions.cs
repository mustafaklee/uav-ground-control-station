using System.ComponentModel.DataAnnotations;

namespace Gcs.Persistence;

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>Name of the entry under <c>ConnectionStrings</c> that holds the PostgreSQL connection string.</summary>
    public const string ConnectionStringName = "Postgres";

    /// <summary>Seconds before a database command is aborted.</summary>
    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>How many times a transient PostgreSQL failure is retried before giving up.</summary>
    [Range(0, 10)]
    public int MaxRetryCount { get; init; } = 3;
}
