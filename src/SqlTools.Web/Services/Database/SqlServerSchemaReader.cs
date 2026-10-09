using Microsoft.Data.SqlClient;

namespace SqlTools.Web.Services;

public interface IDatabaseSchemaReader
{
    Task<IReadOnlyList<DatabaseEntry>> ListDatabasesAsync(string connectionString, CancellationToken cancellationToken);

    Task<DatabaseSchema> ReadAsync(string connectionString, CancellationToken cancellationToken);
}

/// <summary>Lê tabelas, colunas, constraints e índices pelas views de catálogo (sys.*). Somente leitura.</summary>
public sealed class SqlServerSchemaReader : IDatabaseSchemaReader
{
    private const string TablesSql = """
        SELECT t.object_id, s.name, t.name
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name
        """;

    private const string ColumnsSql = """
        SELECT c.object_id, c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id AND t.is_ms_shipped = 0
        ORDER BY c.object_id, c.column_id
        """;

    private const string ConstraintsSql = """
        SELECT o.parent_object_id, o.name, o.type
        FROM sys.objects o
        JOIN sys.tables t ON t.object_id = o.parent_object_id AND t.is_ms_shipped = 0
        WHERE o.type IN ('PK', 'UQ', 'F', 'C', 'D')
        ORDER BY o.name
        """;

    private const string IndexesSql = """
        SELECT i.object_id, i.name, i.is_primary_key, i.is_unique_constraint
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id AND t.is_ms_shipped = 0
        WHERE i.index_id > 0 AND i.name IS NOT NULL AND i.is_hypothetical = 0
        ORDER BY i.name
        """;

    // Bancos online aos quais o login tem acesso; os de usuário vêm antes dos do sistema (master, tempdb, model, msdb).
    private const string DatabasesSql = """
        SELECT name, CAST(CASE WHEN database_id <= 4 THEN 1 ELSE 0 END AS BIT)
        FROM sys.databases
        WHERE state = 0 AND HAS_DBACCESS(name) = 1
        ORDER BY CASE WHEN database_id <= 4 THEN 1 ELSE 0 END, name
        """;

    public async Task<IReadOnlyList<DatabaseEntry>> ListDatabasesAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var databases = new List<DatabaseEntry>();
        await foreach (var row in Query(connection, DatabasesSql, cancellationToken))
            databases.Add(new DatabaseEntry(row.GetString(0), row.GetBoolean(1)));
        return databases;
    }

    public async Task<DatabaseSchema> ReadAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (builder.ConnectTimeout is 0 or > 15)
            builder.ConnectTimeout = 15;
        builder.ApplicationName = "SqlTools";

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var schema = new DatabaseSchema
        {
            Server = connection.DataSource,
            Database = connection.Database,
        };
        var byId = new Dictionary<int, TableInfo>();

        await foreach (var row in Query(connection, TablesSql, cancellationToken))
        {
            var table = new TableInfo { Schema = row.GetString(1), Name = row.GetString(2) };
            byId[row.GetInt32(0)] = table;
            schema.Tables.Add(table);
        }

        await foreach (var row in Query(connection, ColumnsSql, cancellationToken))
            if (byId.TryGetValue(row.GetInt32(0), out var table))
                table.Columns.Add(new ColumnInfo(
                    row.GetString(1), row.GetString(2).ToUpperInvariant(), row.GetInt16(3), row.GetByte(4), row.GetByte(5),
                    row.GetBoolean(6), row.GetBoolean(7)));

        await foreach (var row in Query(connection, ConstraintsSql, cancellationToken))
            if (byId.TryGetValue(row.GetInt32(0), out var table))
                table.Constraints.Add(new NamedObject(row.GetString(1), ConstraintKind(row.GetString(2).Trim())));

        await foreach (var row in Query(connection, IndexesSql, cancellationToken))
            if (byId.TryGetValue(row.GetInt32(0), out var table))
                table.Indexes.Add(new NamedObject(row.GetString(1),
                    row.GetBoolean(2) ? "PK" : row.GetBoolean(3) ? "UNIQUE" : "Índice"));

        return schema;
    }

    private static string ConstraintKind(string type) => type switch
    {
        "PK" => "PK",
        "UQ" => "UNIQUE",
        "F" => "FK",
        "C" => "CHECK",
        _ => "DEFAULT",
    };

    private static async IAsyncEnumerable<SqlDataReader> Query(
        SqlConnection connection, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return reader;
    }
}
