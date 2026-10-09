using SqlTools.Core.Model;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Requests;

public sealed record IndexColumn(string Name, SortDirection Direction = SortDirection.Asc)
{
    /// <summary>Converte "Nome DESC, Id" em colunas com direção.</summary>
    public static List<IndexColumn> ParseList(string? text) =>
        ColumnList.Parse(text)
            .Select(item =>
            {
                var parts = item.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 1 && parts[^1].ToUpperInvariant() is "ASC" or "DESC")
                {
                    var direction = parts[^1].Equals("DESC", StringComparison.OrdinalIgnoreCase) ? SortDirection.Desc : SortDirection.Asc;
                    return new IndexColumn(string.Join(' ', parts[..^1]), direction);
                }
                return new IndexColumn(item);
            })
            .ToList();

    public static string Format(IEnumerable<IndexColumn> columns) =>
        string.Join(", ", columns.Select(c => c.Direction == SortDirection.Desc ? $"{c.Name} DESC" : c.Name));
}

public sealed class CreateIndexRequest : ScriptRequest
{
    public string? IndexName { get; set; }
    public List<IndexColumn> Columns { get; set; } = [];
    public List<string> IncludedColumns { get; set; } = [];
    public bool IsUnique { get; set; }
    public bool IsClustered { get; set; }

    /// <summary>Filtro (índice filtrado), ex.: [Ativo] = 1.</summary>
    public string? Filter { get; set; }

    public override string OperationName => "Criar índice";
    public override string Describe() => $"{OperationName} — {GetIndexName()} em {TableDisplay}";

    public string GetIndexName() =>
        NameOrDefault(IndexName, $"{(IsUnique ? "UX" : "IX")}_{Table.Trim()}_{JoinNames(Columns.Select(c => c.Name))}");

    protected override IEnumerable<string> ValidateCore()
    {
        foreach (var e in ValidateOptionalName(IndexName, "Nome do índice")) yield return e;
        foreach (var e in ValidateColumnList(Columns.Select(c => c.Name).ToList(), "Colunas")) yield return e;

        if (IncludedColumns.Count > 0)
        {
            foreach (var e in ValidateColumnList(IncludedColumns, "Colunas incluídas (INCLUDE)")) yield return e;
            if (IsClustered)
                yield return "Índice CLUSTERED não aceita colunas INCLUDE.";
            foreach (var overlap in IncludedColumns.Intersect(Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase))
                yield return $"Coluna '{overlap}' não pode estar na chave e no INCLUDE ao mesmo tempo.";
        }

        if (IsClustered && !string.IsNullOrWhiteSpace(Filter))
            yield return "Índice filtrado (WHERE) não pode ser CLUSTERED.";
    }
}

public sealed class DropIndexRequest : ScriptRequest
{
    public string IndexName { get; set; } = "";

    public override string OperationName => "Excluir índice";
    public override string Describe() => $"{OperationName} — {IndexName} em {TableDisplay}";

    protected override IEnumerable<string> ValidateCore()
    {
        if (SqlText.ValidateIdentifier(IndexName, "Nome do índice") is { } e) yield return e;
    }
}

/// <summary>Conversão entre texto separado por vírgula e lista de colunas.</summary>
public static class ColumnList
{
    public static List<string> Parse(string? text) =>
        (text ?? "")
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unbracket)
            .Where(s => s.Length > 0)
            .ToList();

    public static string Format(IEnumerable<string> columns) => string.Join(", ", columns);

    private static string Unbracket(string value)
    {
        value = value.Trim();
        // Mantém a direção (ASC/DESC) fora dos colchetes: "[Nome] DESC" -> "Nome DESC".
        if (value.StartsWith('[') && value.IndexOf(']') is var end and > 0)
            return (value[1..end].Replace("]]", "]") + value[(end + 1)..]).Trim();
        return value;
    }
}
