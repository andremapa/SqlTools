using System.Text.Json;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Requests;

/// <summary>
/// Base de toda operação que gera script. Toda operação atua sobre uma tabela (schema + nome).
/// </summary>
public abstract class ScriptRequest
{
    public string Schema { get; set; } = "dbo";
    public string Table { get; set; } = "";

    /// <summary>Nome da operação, ex.: "Criar tabela".</summary>
    public abstract string OperationName { get; }

    /// <summary>Descrição curta da instância, ex.: "Criar tabela dbo.Cliente".</summary>
    public virtual string Describe() => $"{OperationName} — {TableDisplay}";

    public string TableDisplay => SqlText.Display(Schema, Table);
    public string QualifiedTable => $"{SqlText.Quote(Schema)}.{SqlText.Quote(Table)}";

    /// <summary>Nome qualificado de um objeto do mesmo schema da tabela (ex.: constraints).</summary>
    public string QualifiedInSchema(string name) => $"{SqlText.Quote(Schema)}.{SqlText.Quote(name)}";

    /// <summary>Valida os dados informados (não o banco). Retorna as mensagens de erro.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SqlText.ValidateIdentifier(Schema, "Schema") is { } schemaError)
            errors.Add(schemaError);
        if (SqlText.ValidateIdentifier(Table, "Tabela") is { } tableError)
            errors.Add(tableError);
        errors.AddRange(ValidateCore());
        return errors;
    }

    protected abstract IEnumerable<string> ValidateCore();

    /// <summary>Cópia profunda (o formulário continua editável após adicionar a operação a um lote).</summary>
    public ScriptRequest Clone() =>
        (ScriptRequest)JsonSerializer.Deserialize(JsonSerializer.Serialize(this, GetType()), GetType())!;

    protected static IEnumerable<string> ValidateColumnList(IReadOnlyCollection<string> columns, string label)
    {
        if (columns.Count == 0)
        {
            yield return $"Informe ao menos uma coluna em '{label}'.";
            yield break;
        }

        foreach (var column in columns)
            if (SqlText.ValidateIdentifier(column, $"Coluna em '{label}'") is { } error)
                yield return error;

        foreach (var duplicate in columns.GroupBy(c => c.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"Coluna '{duplicate.Key}' repetida em '{label}'.";
    }

    protected static IEnumerable<string> ValidateOptionalName(string? name, string label) =>
        string.IsNullOrWhiteSpace(name) || SqlText.ValidateIdentifier(name, label) is not { } error
            ? []
            : [error];

    protected string NameOrDefault(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

    protected string JoinNames(IEnumerable<string> names) => string.Join("_", names.Select(n => n.Trim()));
}
