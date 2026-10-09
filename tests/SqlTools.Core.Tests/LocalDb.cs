using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlTools.Core.Tests;

/// <summary>
/// Executa os scripts gerados em um banco temporário no SQL Server LocalDB.
/// Os testes são ignorados quando o LocalDB não está disponível.
/// Use a variável SQLTOOLS_TEST_SERVER para apontar para outra instância.
/// </summary>
public sealed partial class LocalDbFixture : IDisposable
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("SQLTOOLS_TEST_SERVER") ?? @"(localdb)\MSSQLLocalDB";

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var connection = new SqlConnection(Master);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    });

    private static string Master => $"Server={Server};Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=15";

    public static bool IsAvailable => Available.Value;

    public string DatabaseName { get; } = "SqlToolsTest_" + Guid.NewGuid().ToString("N")[..12];

    private string ConnectionString =>
        $"Server={Server};Database={DatabaseName};Integrated Security=true;TrustServerCertificate=true";

    public LocalDbFixture()
    {
        if (!IsAvailable)
            return;
        ExecuteOn(Master, $"CREATE DATABASE [{DatabaseName}];");
    }

    /// <summary>Executa um script com separadores GO. Retorna as mensagens PRINT. Lança SqlException em erro.</summary>
    public List<string> Run(string script)
    {
        var messages = new List<string>();
        using var connection = new SqlConnection(ConnectionString);
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SqlError>().Select(x => x.Message));
        connection.Open();

        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            using var command = new SqlCommand(batch, connection);
            command.ExecuteNonQuery();
        }
        return messages;
    }

    public T Scalar<T>(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        return (T)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        if (!IsAvailable)
            return;
        SqlConnection.ClearAllPools();
        ExecuteOn(Master, $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
    }

    private static void ExecuteOn(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

public sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute()
    {
        if (!LocalDbFixture.IsAvailable)
            Skip = "SQL Server LocalDB indisponível.";
    }
}
