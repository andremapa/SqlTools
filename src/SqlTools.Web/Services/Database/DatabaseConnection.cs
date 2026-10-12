using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace SqlTools.Web.Services;

/// <summary>
/// Conexão do usuário com o SQL Server usada apenas para LEITURA da estrutura (nenhum script é executado).
/// Funciona em duas etapas, como no SSMS: conecta ao servidor (lista os bancos) e depois carrega a estrutura do banco escolhido.
/// Os dados ficam na memória do circuito; com "lembrar", também são guardados no navegador,
/// criptografados pelo Data Protection do servidor e separados por usuário.
/// </summary>
public sealed class DatabaseConnection(
    IDatabaseSchemaReader reader,
    ProtectedLocalStorage storage,
    AuthenticationStateProvider authentication)
{
    // v2: formato com campos separados (a v1 guardava a string de conexão inteira).
    private const string KeyPrefix = "sqltools.connection.v2.";
    private const string LegacyKeyPrefix = "sqltools.connection.";

    private readonly object _gate = new();
    private readonly Dictionary<object, Task> _loadingDetails = new(ReferenceEqualityComparer.Instance);

    private bool _restored;
    private bool _serverConnected;

    public ConnectionSettings? Settings { get; private set; }
    public IReadOnlyList<DatabaseEntry> Databases { get; private set; } = [];
    public DatabaseSchema? Schema { get; private set; }
    public string? Error { get; private set; }
    public bool IsLoading { get; private set; }
    public bool Remember { get; private set; }

    /// <summary>Conectado ao servidor (a lista de bancos está disponível), mesmo sem banco escolhido.</summary>
    public bool IsServerConnected => _serverConnected;

    /// <summary>Estrutura de um banco carregada: é isso que alimenta as listas das telas.</summary>
    public bool IsConnected => Schema is not null;

    public event Action? Changed;

    /// <summary>Conecta ao servidor. Se <see cref="ConnectionSettings.Database"/> estiver preenchido (ou houver um único banco de usuário), já carrega a estrutura.</summary>
    public async Task ConnectAsync(ConnectionSettings settings, bool remember)
    {
        if (settings.Validate() is { } validation)
        {
            Error = validation;
            Changed?.Invoke();
            return;
        }

        await RunAsync(async ct =>
        {
            var databases = await reader.ListDatabasesAsync(settings.BuildConnectionString(), ct);

            Settings = settings with { Server = settings.Server.Trim(), UserName = settings.UserName?.Trim() };
            Databases = databases;
            Remember = remember;
            Schema = null;
            _serverConnected = true;

            var wanted = databases.FirstOrDefault(d => d.Name.Equals(settings.Database, StringComparison.OrdinalIgnoreCase))
                         ?? OnlyUserDatabase(databases);
            if (wanted is not null)
            {
                try
                {
                    await LoadSchemaAsync(wanted.Name, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // O servidor respondeu, só esse banco falhou (ex.: sem permissão): mantém a conexão para escolher outro.
                    Error = $"Não foi possível ler o banco {wanted.Name}: {ex.Message}";
                }
            }

            await SaveAsync();
        }, resetOnError: true);
    }

    public Task SelectDatabaseAsync(string database) =>
        Settings is null || string.IsNullOrWhiteSpace(database)
            ? Task.CompletedTask
            : RunAsync(async ct =>
            {
                await LoadSchemaAsync(database, ct);
                await SaveAsync();
            }, resetOnError: false);

    /// <summary>Relê a lista de bancos e a estrutura (útil depois de executar scripts no banco).</summary>
    public Task RefreshAsync() =>
        Settings is null ? Task.CompletedTask : ConnectAsync(Settings, Remember);

    public async Task DisconnectAsync()
    {
        Settings = null;
        Databases = [];
        Schema = null;
        Error = null;
        Remember = false;
        _serverConnected = false;
        await DeleteStoredAsync();
        Changed?.Invoke();
    }

    /// <summary>Reconecta com os dados lembrados no navegador. Chamar após a primeira renderização (usa JS).</summary>
    public async Task RestoreAsync()
    {
        if (_restored)
            return;
        _restored = true;

        try
        {
            var id = await UserIdAsync();
            if (id is null)
                return;

            await storage.DeleteAsync(LegacyKeyPrefix + id); // formato antigo, não é mais usado

            var stored = await storage.GetAsync<ConnectionSettings>(KeyPrefix + id);
            if (stored.Success && stored.Value is { } settings)
                await ConnectAsync(settings, remember: true);
        }
        catch
        {
            // Dado ilegível (ex.: chaves de criptografia trocadas): ignora e segue sem conexão.
            await DeleteStoredAsync();
        }
    }

    public IReadOnlyList<string> Schemas => Schema?.Schemas.ToList() ?? [];

    /// <summary>Sugestões de tabela para o texto digitado (limitadas: bancos de ERP têm milhares de tabelas).</summary>
    public IReadOnlyList<string> SearchTables(string? schema, string? text, int limit = 50, bool includeViews = false) =>
        Schema?.SearchTables(schema, text, limit, includeViews) ?? [];

    public TableInfo? Find(string? schema, string? table) => Schema?.Find(schema, table);

    public ModuleInfo? FindModule(ModuleKind kind, string? schema, string? name) => Schema?.FindModule(kind, schema, name);

    public bool TableOrViewExists(string? schema, string? name) => Schema?.TableOrViewExists(schema, name) ?? false;

    /// <summary>Sugestões de view ou trigger para o texto digitado (limitadas, como as de tabela).</summary>
    public IReadOnlyList<ModuleInfo> SearchModules(ModuleKind kind, string? schema, string? text, int limit = 50) =>
        Schema?.SearchModules(kind, schema, text, limit) ?? [];

    /// <summary>Colunas já carregadas da tabela (vazio até <see cref="EnsureTableAsync"/> terminar).</summary>
    public IReadOnlyList<string> ColumnsOf(string? schema, string? table) =>
        Find(schema, table)?.Columns.Select(c => c.Name).ToList() ?? [];

    /// <summary>Tabela com detalhes sendo lidos agora (para mostrar "carregando" na interface).</summary>
    public bool IsLoadingDetails(string? schema, string? table)
    {
        var info = Find(schema, table);
        if (info is null)
            return false;
        lock (_gate)
            return _loadingDetails.ContainsKey(info);
    }

    /// <summary>
    /// Garante que colunas, constraints e índices da tabela estejam carregados. A leitura é feita uma única vez por tabela
    /// (chamadas simultâneas compartilham a mesma tarefa) e fica em cache até reconectar ou atualizar a estrutura.
    /// Falhas são silenciosas: a próxima chamada tenta de novo, e a tela segue funcionando com digitação manual.
    /// </summary>
    public Task EnsureTableAsync(string? schema, string? table)
    {
        var info = Find(schema, table);
        return info is null
            ? Task.CompletedTask
            : EnsureLoadedAsync(info, info.DetailsLoaded, (cs, ct) => reader.LoadTableDetailsAsync(cs, info, ct));
    }

    /// <summary>Igual a <see cref="EnsureTableAsync"/>, para a definição (e colunas) de uma view ou trigger.</summary>
    public Task EnsureModuleAsync(ModuleInfo? module) =>
        module is null
            ? Task.CompletedTask
            : EnsureLoadedAsync(module, module.DetailsLoaded, (cs, ct) => reader.LoadModuleDetailsAsync(cs, module, ct));

    public bool IsLoadingModule(ModuleInfo module)
    {
        lock (_gate)
            return _loadingDetails.ContainsKey(module);
    }

    private Task EnsureLoadedAsync(object key, bool alreadyLoaded, Func<string, CancellationToken, Task> load)
    {
        var owner = Schema;
        var settings = Settings;
        if (alreadyLoaded || owner is null || settings is null)
            return Task.CompletedTask;

        lock (_gate)
        {
            if (_loadingDetails.TryGetValue(key, out var running))
                return running;

            var task = LoadDetailsAsync(key, owner, settings.BuildConnectionString(owner.Database), load);
            _loadingDetails[key] = task;
            return task;
        }
    }

    private async Task LoadDetailsAsync(object key, DatabaseSchema owner, string connectionString, Func<string, CancellationToken, Task> load)
    {
        // Cede o controle antes de qualquer trabalho: o chamador está dentro de um lock ao registrar esta tarefa.
        await Task.Yield();
        Changed?.Invoke(); // mostra "carregando"

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await load(connectionString, timeout.Token);
        }
        catch
        {
            // Segue sem os detalhes; será tentado novamente na próxima vez.
        }
        finally
        {
            lock (_gate)
                _loadingDetails.Remove(key);
        }

        if (ReferenceEquals(Schema, owner))
            Changed?.Invoke();
    }

    private async Task LoadSchemaAsync(string database, CancellationToken ct)
    {
        lock (_gate)
            _loadingDetails.Clear();

        Schema = await reader.ReadTablesAsync(Settings!.BuildConnectionString(database), ct);
        Settings = Settings with { Database = database };
    }

    private static DatabaseEntry? OnlyUserDatabase(IReadOnlyList<DatabaseEntry> databases)
    {
        var user = databases.Where(d => !d.IsSystem).Take(2).ToList();
        return user.Count == 1 ? user[0] : null;
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, bool resetOnError)
    {
        IsLoading = true;
        Error = null;
        Changed?.Invoke();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await operation(timeout.Token);
        }
        catch (Exception ex)
        {
            Error = ex is OperationCanceledException ? "Tempo esgotado ao se comunicar com o servidor." : ex.Message;
            Schema = null;
            if (resetOnError)
            {
                Settings = null;
                Databases = [];
                _serverConnected = false;
            }
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    private async Task SaveAsync()
    {
        if (!Remember || Settings is null)
        {
            await DeleteStoredAsync();
            return;
        }

        try
        {
            if (await UserIdAsync() is { } id)
                await storage.SetAsync(KeyPrefix + id, Settings);
        }
        catch (InvalidOperationException)
        {
            // JS interop indisponível (pré-renderização): a conexão segue só em memória.
        }
    }

    private async Task DeleteStoredAsync()
    {
        try
        {
            if (await UserIdAsync() is { } id)
                await storage.DeleteAsync(KeyPrefix + id);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task<string?> UserIdAsync() =>
        (await authentication.GetAuthenticationStateAsync()).User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
}
