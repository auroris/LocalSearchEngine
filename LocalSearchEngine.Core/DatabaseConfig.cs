namespace LocalSearchEngine.Core;

/// <summary>
/// Represents the configuration settings for the database, containing the connection details.
/// </summary>
public class DatabaseConfig
{
    /// <summary>
    /// Gets the SQLite connection string used to connect to the database.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseConfig"/> class with the specified connection string.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string to use.</param>
    public DatabaseConfig(string connectionString)
    {
        ConnectionString = connectionString;
    }
}
