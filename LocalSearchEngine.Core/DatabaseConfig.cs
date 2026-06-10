namespace LocalSearchEngine.Core;

public class DatabaseConfig
{
    public string ConnectionString { get; }

    public DatabaseConfig(string connectionString)
    {
        ConnectionString = connectionString;
    }
}
