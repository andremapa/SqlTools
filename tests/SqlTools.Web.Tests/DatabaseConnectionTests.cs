using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.JSInterop;
using SqlTools.Web.Services;

namespace SqlTools.Web.Tests;

public class DatabaseConnectionTests
{
    private sealed class FakeReader : IDatabaseSchemaReader
    {
        public int TablesCalls, DetailsCalls;
        public TimeSpan DetailsDelay = TimeSpan.FromMilliseconds(150);
        public bool FailDetails;

        public Task<IReadOnlyList<DatabaseEntry>> ListDatabasesAsync(string connectionString, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DatabaseEntry>>([new DatabaseEntry("erp", false), new DatabaseEntry("master", true)]);

        public Task<DatabaseSchema> ReadTablesAsync(string connectionString, CancellationToken ct)
        {
            Interlocked.Increment(ref TablesCalls);
            var schema = new DatabaseSchema { Server = "srv", Database = "erp" };
            schema.Add(new TableInfo { ObjectId = 1, Schema = "dbo", Name = "Cliente" });
            return Task.FromResult(schema);
        }

        public Task LoadModuleDetailsAsync(string connectionString, ModuleInfo module, CancellationToken ct)
        {
            module.SetDetails("CREATE VIEW dbo.v AS SELECT 1 AS a", ["a"]);
            return Task.CompletedTask;
        }

        public async Task LoadTableDetailsAsync(string connectionString, TableInfo table, CancellationToken ct)
        {
            Interlocked.Increment(ref DetailsCalls);
            await Task.Delay(DetailsDelay, ct);
            if (FailDetails)
                throw new TimeoutException();
            table.SetDetails([new ColumnInfo("Id", "INT", 4, 10, 0, false, true)], [], []);
        }
    }

    private sealed class FakeJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(T)!);
    }

    private sealed class AnonymousAuth : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal()));
    }

    private static async Task<(DatabaseConnection Connection, FakeReader Reader)> ConnectedAsync(Action<FakeReader>? configure = null)
    {
        var reader = new FakeReader();
        configure?.Invoke(reader);
        var storage = new ProtectedLocalStorage(new FakeJs(), new EphemeralDataProtectionProvider());
        var connection = new DatabaseConnection(reader, storage, new AnonymousAuth());

        await connection.ConnectAsync(new ConnectionSettings("srv", true, null, null, null), remember: false);
        return (connection, reader);
    }

    [Fact]
    public async Task Connect_ReadsOnlyTheTableList()
    {
        var (connection, reader) = await ConnectedAsync();

        Assert.True(connection.IsConnected);
        Assert.Equal(1, reader.TablesCalls);
        Assert.Equal(0, reader.DetailsCalls);
        Assert.Empty(connection.ColumnsOf("dbo", "Cliente"));
    }

    [Fact]
    public async Task EnsureTable_LoadsDetailsOnce_EvenWithConcurrentCalls()
    {
        var (connection, reader) = await ConnectedAsync();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => connection.EnsureTableAsync("dbo", "Cliente")));
        await connection.EnsureTableAsync("DBO", "cliente"); // já em cache

        Assert.Equal(1, reader.DetailsCalls);
        Assert.Equal(["Id"], connection.ColumnsOf("dbo", "Cliente"));
    }

    [Fact]
    public async Task EnsureTable_FailureIsSilent_AndRetriesNextTime()
    {
        var (connection, reader) = await ConnectedAsync(r => r.FailDetails = true);

        await connection.EnsureTableAsync("dbo", "Cliente"); // não lança
        Assert.Empty(connection.ColumnsOf("dbo", "Cliente"));
        Assert.True(connection.IsConnected);

        reader.FailDetails = false;
        await connection.EnsureTableAsync("dbo", "Cliente");

        Assert.Equal(2, reader.DetailsCalls);
        Assert.Equal(["Id"], connection.ColumnsOf("dbo", "Cliente"));
    }

    [Fact]
    public async Task EnsureTable_UnknownTable_DoesNotQueryTheDatabase()
    {
        var (connection, reader) = await ConnectedAsync();

        await connection.EnsureTableAsync("dbo", "Digitando"); // texto parcial digitado pelo usuário
        await connection.EnsureTableAsync(null, null);

        Assert.Equal(0, reader.DetailsCalls);
    }

    [Fact]
    public async Task Refresh_DiscardsCachedDetails()
    {
        var (connection, reader) = await ConnectedAsync();
        await connection.EnsureTableAsync("dbo", "Cliente");

        await connection.RefreshAsync();
        Assert.Empty(connection.ColumnsOf("dbo", "Cliente"));

        await connection.EnsureTableAsync("dbo", "Cliente");
        Assert.Equal(2, reader.DetailsCalls);
    }
}
