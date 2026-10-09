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

public sealed class TableInfo
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public List<ColumnInfo> Columns { get; } = [];

    /// <summary>PK, UNIQUE, FOREIGN KEY, CHECK e DEFAULT da tabela.</summary>
    public List<NamedObject> Constraints { get; } = [];

    public List<NamedObject> Indexes { get; } = [];

    public string Display => $"{Schema}.{Name}";
}

/// <summary>Retrato da estrutura do banco no momento da conexão.</summary>
public sealed class DatabaseSchema
{
    public required string Server { get; init; }
    public required string Database { get; init; }
    public List<TableInfo> Tables { get; } = [];

    public IEnumerable<string> Schemas =>
        Tables.Select(t => t.Schema).Append("dbo").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    public TableInfo? Find(string? schema, string? table) =>
        Tables.FirstOrDefault(t =>
            t.Schema.Equals(schema?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            t.Name.Equals(table?.Trim(), StringComparison.OrdinalIgnoreCase));
}
