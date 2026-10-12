using Microsoft.Data.SqlClient;

namespace SqlTools.Web.Services;

public interface IDatabaseSchemaReader
{
    Task<IReadOnlyList<DatabaseEntry>> ListDatabasesAsync(string connectionString, CancellationToken cancellationToken);

    /// <summary>Lê apenas a lista de tabelas (schema e nome). Detalhes vêm de <see cref="LoadTableDetailsAsync"/>.</summary>
    Task<DatabaseSchema> ReadTablesAsync(string connectionString, CancellationToken cancellationToken);

    /// <summary>Lê colunas, constraints e índices de uma tabela e os guarda nela.</summary>
    Task LoadTableDetailsAsync(string connectionString, TableInfo table, CancellationToken cancellationToken);

    /// <summary>Lê a definição (CREATE ...) e, para views, as colunas, e os guarda no objeto.</summary>
    Task LoadModuleDetailsAsync(string connectionString, ModuleInfo module, CancellationToken cancellationToken);
}

/// <summary>
/// Lê metadados pelas views de catálogo (sys.*). Somente leitura.
/// Todas as consultas usam READ UNCOMMITTED: ler o catálogo não deve esperar por transações longas ou DDL em andamento
/// no ERP (a causa mais comum de "timeout" ao ler metadados em banco movimentado), e um retrato aproximado basta aqui.
/// </summary>
public sealed class SqlServerSchemaReader : IDatabaseSchemaReader
{
    private const string NoBlocking = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n";

    // Sem HAS_DBACCESS: ele precisa abrir cada banco e, com muitos bancos (ou AUTO_CLOSE), é lento.
    // Um banco sem acesso só dá erro ao ser escolhido, com a mensagem do próprio SQL Server.
    private const string DatabasesSql = NoBlocking + """
        SELECT name, CAST(CASE WHEN database_id <= 4 THEN 1 ELSE 0 END AS BIT)
        FROM sys.databases
        WHERE state = 0
        ORDER BY CASE WHEN database_id <= 4 THEN 1 ELSE 0 END, name
        """;

    // Tabelas, views e triggers numa única ida ao servidor (três resultados). Só nomes: definições são lidas sob demanda.
    private const string TablesSql = NoBlocking + """
        SELECT t.object_id, s.name, t.name
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name;

        SELECT v.object_id, s.name, v.name
        FROM sys.views v
        JOIN sys.schemas s ON s.schema_id = v.schema_id
        WHERE v.is_ms_shipped = 0
        ORDER BY s.name, v.name;

        SELECT t.object_id, os.name, t.name, ps.name, p.name
        FROM sys.triggers t
        JOIN sys.objects o ON o.object_id = t.object_id
        JOIN sys.schemas os ON os.schema_id = o.schema_id
        JOIN sys.objects p ON p.object_id = t.parent_id
        JOIN sys.schemas ps ON ps.schema_id = p.schema_id
        WHERE t.parent_class = 1 AND t.is_ms_shipped = 0
        ORDER BY os.name, t.name;
        """;

    private const string ModuleSql = NoBlocking + """
        SELECT m.definition FROM sys.sql_modules m WHERE m.object_id = @id;

        SELECT c.name FROM sys.columns c WHERE c.object_id = @id ORDER BY c.column_id;
        """;

    // Um único lote com três resultados, filtrado pelo object_id (usa o índice do catálogo, não varre o banco).
    private const string DetailsSql = NoBlocking + """
        SELECT c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity
        FROM sys.columns c
        WHERE c.object_id = @id
        ORDER BY c.column_id;

        SELECT o.name, o.type
        FROM sys.objects o
        WHERE o.parent_object_id = @id AND o.type IN ('PK', 'UQ', 'F', 'C', 'D')
        ORDER BY o.name;

        SELECT i.name, i.is_primary_key, i.is_unique_constraint
        FROM sys.indexes i
        WHERE i.object_id = @id AND i.index_id > 0 AND i.name IS NOT NULL AND i.is_hypothetical = 0
        ORDER BY i.name;
        """;

    public async Task<IReadOnlyList<DatabaseEntry>> ListDatabasesAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(DatabasesSql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var databases = new List<DatabaseEntry>();
        while (await reader.ReadAsync(cancellationToken))
            databases.Add(new DatabaseEntry(reader.GetString(0), reader.GetBoolean(1)));
        return databases;
    }

    public async Task<DatabaseSchema> ReadTablesAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var schema = new DatabaseSchema { Server = connection.DataSource, Database = connection.Database };

        await using var command = new SqlCommand(TablesSql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            schema.Add(new TableInfo { ObjectId = reader.GetInt32(0), Schema = reader.GetString(1), Name = reader.GetString(2) });

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            schema.Add(new ModuleInfo
            {
                ObjectId = reader.GetInt32(0), Kind = ModuleKind.View, Schema = reader.GetString(1), Name = reader.GetString(2),
            });

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            schema.Add(new ModuleInfo
            {
                ObjectId = reader.GetInt32(0), Kind = ModuleKind.Trigger, Schema = reader.GetString(1), Name = reader.GetString(2),
                ParentSchema = reader.GetString(3), ParentName = reader.GetString(4),
            });

        return schema;
    }

    public async Task LoadModuleDetailsAsync(string connectionString, ModuleInfo module, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(ModuleSql, connection) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("@id", module.ObjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        string? definition = null;
        if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
            definition = reader.GetString(0);

        var columns = new List<string>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));

        module.SetDetails(definition, columns);
    }

    public async Task LoadTableDetailsAsync(string connectionString, TableInfo table, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(DetailsSql, connection) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("@id", table.ObjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new List<ColumnInfo>();
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(new ColumnInfo(
                reader.GetString(0), reader.GetString(1).ToUpperInvariant(), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4),
                reader.GetBoolean(5), reader.GetBoolean(6)));

        var constraints = new List<NamedObject>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            constraints.Add(new NamedObject(reader.GetString(0), ConstraintKind(reader.GetString(1).Trim())));

        var indexes = new List<NamedObject>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            indexes.Add(new NamedObject(reader.GetString(0),
                reader.GetBoolean(1) ? "PK" : reader.GetBoolean(2) ? "UNIQUE" : "Índice"));

        table.SetDetails(columns, constraints, indexes);
    }

    private static string ConstraintKind(string type) => type switch
    {
        "PK" => "PK",
        "UQ" => "UNIQUE",
        "F" => "FK",
        "C" => "CHECK",
        _ => "DEFAULT",
    };
}
