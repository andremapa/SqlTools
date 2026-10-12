using Microsoft.Data.SqlClient;
using SqlTools.Web.Services;

namespace SqlTools.Web.Tests;

/// <summary>Cria um banco temporário no LocalDB (ou em SQLTOOLS_TEST_SERVER). Os testes são ignorados sem SQL Server.</summary>
public sealed class ErpLikeDatabase : IDisposable
{
    private static readonly string Server = Environment.GetEnvironmentVariable("SQLTOOLS_TEST_SERVER") ?? @"(localdb)\MSSQLLocalDB";

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

    public string Name { get; } = "SqlToolsWeb_" + Guid.NewGuid().ToString("N")[..12];

    public string ConnectionString => $"Server={Server};Database={Name};Integrated Security=true;TrustServerCertificate=true";

    public ErpLikeDatabase()
    {
        if (!IsAvailable)
            return;

        Execute(Master, $"CREATE DATABASE [{Name}];");
        Execute(ConnectionString, """
            CREATE SCHEMA vendas;
            """);
        Execute(ConnectionString, """
            CREATE TABLE dbo.Cliente (
                Id INT IDENTITY NOT NULL CONSTRAINT PK_Cliente PRIMARY KEY,
                Nome NVARCHAR(100) NOT NULL,
                Saldo DECIMAL(12,3) NOT NULL CONSTRAINT DF_Cliente_Saldo DEFAULT 0);
            CREATE INDEX IX_Cliente_Nome ON dbo.Cliente (Nome);
            CREATE TABLE vendas.Pedido (
                Id INT NOT NULL PRIMARY KEY,
                ClienteId INT NOT NULL CONSTRAINT FK_Pedido_Cliente REFERENCES dbo.Cliente (Id));
            """);
    }

    public static void Execute(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (!IsAvailable)
            return;
        SqlConnection.ClearAllPools();
        Execute(Master, $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}];");
    }
}

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!ErpLikeDatabase.IsAvailable)
            Skip = "SQL Server LocalDB indisponível.";
    }
}

public class SchemaReaderTests(ErpLikeDatabase db) : IClassFixture<ErpLikeDatabase>
{
    private readonly SqlServerSchemaReader _reader = new();

    [SqlServerFact]
    public async Task ReadTables_ListsTablesWithoutLoadingDetails()
    {
        var schema = await _reader.ReadTablesAsync(db.ConnectionString, CancellationToken.None);

        Assert.Equal(db.Name, schema.Database);
        Assert.Equal(["dbo.Cliente", "vendas.Pedido"], schema.Tables.Select(t => t.Display));
        Assert.All(schema.Tables, t => Assert.False(t.DetailsLoaded));
        Assert.Contains("vendas", schema.Schemas);
    }

    [SqlServerFact]
    public async Task LoadTableDetails_ReadsColumnsConstraintsAndIndexes()
    {
        var schema = await _reader.ReadTablesAsync(db.ConnectionString, CancellationToken.None);
        var cliente = schema.Find("dbo", "Cliente")!;
        var pedido = schema.Find("vendas", "Pedido")!;

        await _reader.LoadTableDetailsAsync(db.ConnectionString, cliente, CancellationToken.None);
        await _reader.LoadTableDetailsAsync(db.ConnectionString, pedido, CancellationToken.None);

        Assert.True(cliente.DetailsLoaded);
        Assert.Equal(["Id", "Nome", "Saldo"], cliente.Columns.Select(c => c.Name));
        var id = cliente.Columns[0];
        Assert.True(id.IsIdentity);
        Assert.False(id.IsNullable);
        var saldo = cliente.Columns[2];
        Assert.Equal(("DECIMAL", 12, 3), (saldo.DataType, saldo.Precision, saldo.Scale));
        Assert.Contains(cliente.Constraints, c => c is { Name: "PK_Cliente", Kind: "PK" });
        Assert.Contains(cliente.Constraints, c => c is { Name: "DF_Cliente_Saldo", Kind: "DEFAULT" });
        Assert.Contains(cliente.Indexes, i => i is { Name: "IX_Cliente_Nome", Kind: "Índice" });
        Assert.Contains(cliente.Indexes, i => i.Kind == "PK");

        // Os detalhes de uma tabela não vazam para a outra.
        Assert.Contains(pedido.Constraints, c => c is { Name: "FK_Pedido_Cliente", Kind: "FK" });
        Assert.DoesNotContain(pedido.Columns, c => c.Name == "Nome");
    }

    [SqlServerFact]
    public async Task ListDatabases_PutsUserDatabasesBeforeSystemOnes()
    {
        var databases = await _reader.ListDatabasesAsync(db.ConnectionString, CancellationToken.None);

        var user = databases.Where(d => !d.IsSystem).Select(d => d.Name).ToList();
        Assert.Contains(db.Name, user);
        Assert.Contains(databases, d => d is { Name: "master", IsSystem: true });
        Assert.True(databases.ToList().FindLastIndex(d => !d.IsSystem) < databases.ToList().FindIndex(d => d.IsSystem));
    }

    /// <summary>
    /// Regressão do "timeout ao conectar no ERP": um DDL aberto (ex.: migração em andamento) fazia a leitura do catálogo
    /// esperar até estourar o tempo. Com READ UNCOMMITTED a leitura não espera.
    /// </summary>
    [SqlServerFact]
    public async Task ReadTables_IsNotBlockedByAnOpenDdlTransaction()
    {
        await using var ddl = new SqlConnection(db.ConnectionString);
        await ddl.OpenAsync();
        await using (var begin = new SqlCommand("BEGIN TRAN; CREATE TABLE dbo.EmMigracao (Id INT); ALTER TABLE dbo.Cliente ADD Extra INT NULL;", ddl))
            await begin.ExecuteNonQueryAsync();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var schema = await _reader.ReadTablesAsync(db.ConnectionString, timeout.Token);

            Assert.NotNull(schema.Find("dbo", "Cliente"));
            await _reader.LoadTableDetailsAsync(db.ConnectionString, schema.Find("dbo", "Cliente")!, timeout.Token);
        }
        finally
        {
            await using var rollback = new SqlCommand("ROLLBACK", ddl);
            await rollback.ExecuteNonQueryAsync();
        }
    }
}
