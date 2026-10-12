namespace SqlTools.Web.Services;

public sealed record ColumnInfo(
    string Name,
    string DataType,
    int MaxLength,
    int Precision,
    int Scale,
    bool IsNullable,
    bool IsIdentity);

public sealed record NamedObject(string Name, string Kind);

/// <summary>
/// Uma tabela do banco. Ao conectar só nome e schema são lidos (barato mesmo em ERPs com milhares de tabelas);
/// colunas, constraints e índices são carregados sob demanda, quando a tabela é realmente usada.
/// </summary>
public sealed class TableInfo
{
    private List<ColumnInfo> _columns = [];
    private List<NamedObject> _constraints = [];
    private List<NamedObject> _indexes = [];

    public required int ObjectId { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }

    public bool DetailsLoaded { get; private set; }

    public IReadOnlyList<ColumnInfo> Columns => _columns;

    /// <summary>PK, UNIQUE, FOREIGN KEY, CHECK e DEFAULT da tabela.</summary>
    public IReadOnlyList<NamedObject> Constraints => _constraints;

    public IReadOnlyList<NamedObject> Indexes => _indexes;

    public string Display => $"{Schema}.{Name}";

    public void SetDetails(List<ColumnInfo> columns, List<NamedObject> constraints, List<NamedObject> indexes)
    {
        _columns = columns;
        _constraints = constraints;
        _indexes = indexes;
        DetailsLoaded = true;
    }
}

public enum ModuleKind
{
    View,
    Trigger,
}

/// <summary>
/// Uma view ou trigger do banco. Na conexão só nome (e tabela, para triggers) são lidos;
/// a definição (CREATE ...) e as colunas da view são carregadas quando o usuário escolhe o objeto.
/// </summary>
public sealed class ModuleInfo
{
    public required int ObjectId { get; init; }
    public required ModuleKind Kind { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }

    /// <summary>Triggers: tabela ou view em que está.</summary>
    public string? ParentSchema { get; init; }
    public string? ParentName { get; init; }

    public bool DetailsLoaded { get; private set; }

    /// <summary>Texto do CREATE. Nulo se estiver criptografado (WITH ENCRYPTION) ou sem permissão de leitura.</summary>
    public string? Definition { get; private set; }

    /// <summary>Colunas (somente views).</summary>
    public IReadOnlyList<string> Columns { get; private set; } = [];

    public string Display => $"{Schema}.{Name}";
    public string? ParentDisplay => ParentName is null ? null : $"{ParentSchema}.{ParentName}";

    public void SetDetails(string? definition, List<string> columns)
    {
        Definition = definition;
        Columns = columns;
        DetailsLoaded = true;
    }
}

/// <summary>Retrato da lista de tabelas, views e triggers do banco no momento da conexão.</summary>
public sealed class DatabaseSchema
{
    private readonly Dictionary<string, TableInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleInfo> _viewsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleInfo> _triggersByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TableInfo> _tables = [];
    private readonly List<ModuleInfo> _views = [];
    private readonly List<ModuleInfo> _triggers = [];

    public required string Server { get; init; }
    public required string Database { get; init; }

    /// <summary>Ordenadas por schema e nome.</summary>
    public IReadOnlyList<TableInfo> Tables => _tables;

    public IEnumerable<string> Schemas =>
        _tables.Select(t => t.Schema).Append("dbo").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ModuleInfo> Views => _views;
    public IReadOnlyList<ModuleInfo> Triggers => _triggers;

    public void Add(TableInfo table)
    {
        _tables.Add(table);
        _byName[Key(table.Schema, table.Name)] = table;
    }

    public void Add(ModuleInfo module)
    {
        var (list, index) = module.Kind == ModuleKind.View ? (_views, _viewsByName) : (_triggers, _triggersByName);
        list.Add(module);
        index[Key(module.Schema, module.Name)] = module;
    }

    public ModuleInfo? FindModule(ModuleKind kind, string? schema, string? name) =>
        string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name)
            ? null
            : (kind == ModuleKind.View ? _viewsByName : _triggersByName).GetValueOrDefault(Key(schema.Trim(), name.Trim()));

    /// <summary>A tabela ou view existe? (alvo de uma trigger)</summary>
    public bool TableOrViewExists(string? schema, string? name) =>
        Find(schema, name) is not null || FindModule(ModuleKind.View, schema, name) is not null;

    /// <summary>Views ou triggers cujo nome contém o texto, limitados a <paramref name="limit"/>.</summary>
    public IReadOnlyList<ModuleInfo> SearchModules(ModuleKind kind, string? schema, string? text, int limit)
    {
        var term = text?.Trim() ?? "";
        var schemaFilter = schema?.Trim();

        return (kind == ModuleKind.View ? _views : _triggers)
            .Where(m => string.IsNullOrEmpty(schemaFilter) || m.Schema.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase))
            .Where(m => term.Length == 0 || m.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }

    /// <summary>Busca por schema e nome em tempo constante (as telas consultam isso a cada renderização).</summary>
    public TableInfo? Find(string? schema, string? table) =>
        string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table)
            ? null
            : _byName.GetValueOrDefault(Key(schema.Trim(), table.Trim()));

    /// <summary>
    /// Nomes de tabela que contêm o texto, limitados a <paramref name="limit"/>. Alimenta as listas de sugestão:
    /// enviar milhares de opções ao navegador a cada tecla seria lento, então só vão as mais relevantes.
    /// </summary>
    public IReadOnlyList<string> SearchTables(string? schema, string? text, int limit, bool includeViews = false)
    {
        var term = text?.Trim() ?? "";
        var schemaFilter = schema?.Trim();

        bool Matches(string itemSchema, string name) =>
            (string.IsNullOrEmpty(schemaFilter) || itemSchema.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)) &&
            (term.Length == 0 || name.Contains(term, StringComparison.OrdinalIgnoreCase));

        var names = _tables.Where(t => Matches(t.Schema, t.Name)).Select(t => t.Name);
        if (includeViews)
            names = names.Concat(_views.Where(v => Matches(v.Schema, v.Name)).Select(v => v.Name));

        return names.Take(limit).ToList();
    }

    private static string Key(string schema, string table) => schema + "\u0001" + table;
}
