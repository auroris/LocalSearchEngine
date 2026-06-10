using System;
using Microsoft.Data.Sqlite;

class Program
{
    static void Main()
    {
        var dbPath = @"e:\LocalSearchEngine\LocalSearchEngine.Web\search.db";
        var connectionString = $"Data Source={dbPath}";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM text_chunks";
        var total = command.ExecuteScalar();
        
        command.CommandText = "SELECT COUNT(*) FROM text_chunks WHERE Text LIKE '%Nadira%'";
        var nadiraCount = command.ExecuteScalar();

        Console.WriteLine($"Total: {total}");
        Console.WriteLine($"Nadira Count: {nadiraCount}");
    }
}
