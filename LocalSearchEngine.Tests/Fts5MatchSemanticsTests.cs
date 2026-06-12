using Microsoft.Data.Sqlite;
using Xunit;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Documents the FTS5 MATCH semantics the keyword search relies on: whitespace-separated
/// (quoted) terms are an implicit AND, not a phrase. This is why the "all terms" tier can quote
/// each term and space-join them and still match documents where the terms aren't adjacent.
/// </summary>
public class Fts5MatchSemanticsTests
{
    [Fact]
    public void Space_separated_quoted_terms_are_implicit_AND_not_a_phrase()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        Exec(conn, "CREATE VIRTUAL TABLE t USING fts5(body, tokenize='porter unicode61');");
        Exec(conn, "INSERT INTO t(rowid, body) VALUES " +
                   "(1, 'foo middle bar')," +   // both terms, NOT adjacent
                   "(2, 'foo bar')," +           // both terms, adjacent
                   "(3, 'only foo here');");      // one term

        // Space-separated quoted terms match wherever both terms occur (rows 1 and 2)...
        Assert.Equal(2, Match(conn, "\"foo\" \"bar\""));
        // ...exactly like an explicit AND...
        Assert.Equal(2, Match(conn, "\"foo\" AND \"bar\""));
        // ...whereas a real phrase only matches the adjacent occurrence (row 2).
        Assert.Equal(1, Match(conn, "\"foo bar\""));
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Match(SqliteConnection c, string expr)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t WHERE t MATCH @q";
        cmd.Parameters.AddWithValue("@q", expr);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }
}
