using System.Text.Json.Serialization;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Requests;

/// <summary>
/// Criar uma view. Se ela já existir, a view antiga é removida e a nova é criada no lugar
/// (as permissões concedidas à view antiga se perdem).
/// </summary>
public sealed class CreateViewRequest : ScriptRequest
{
    /// <summary>Nome da view (usa o mesmo campo da "tabela" das demais operações).</summary>
    [JsonIgnore]
    public string ViewName
    {
        get => Table;
        set => Table = value;
    }

    /// <summary>O SELECT da view (sem o CREATE VIEW).</summary>
    public string SelectStatement { get; set; } = "";

    /// <summary>WITH SCHEMABINDING: impede alterar as tabelas de origem de forma que quebre a view.</summary>
    public bool SchemaBinding { get; set; }

    public override string OperationName => "Criar/recriar view";
    public override bool HasOwnTransaction => true;
    protected override string ObjectLabel => "View";

    /// <summary>O comando CREATE VIEW completo.</summary>
    public string BuildCreateStatement() =>
        $"CREATE VIEW {QualifiedTable}{(SchemaBinding ? " WITH SCHEMABINDING" : "")}\nAS\n{SelectStatement.Trim().TrimEnd(';').TrimEnd()}";

    protected override IEnumerable<string> ValidateCore()
    {
        if (TsqlModules.ValidateSelectStatement(SelectStatement) is { } error)
            yield return error;
    }
}

/// <summary>
/// Adicionar colunas a uma view existente. O script remove a view antiga e a cria de novo com o mesmo SELECT
/// mais as novas colunas (as permissões concedidas à view antiga se perdem).
/// </summary>
public sealed class AddViewColumnsRequest : ScriptRequest
{
    [JsonIgnore]
    public string ViewName
    {
        get => Table;
        set => Table = value;
    }

    /// <summary>Definição atual da view (o CREATE VIEW completo), lida do banco ou colada pelo usuário.</summary>
    public string Definition { get; set; } = "";

    /// <summary>Colunas a acrescentar, uma por item: "c.Email" ou "p.Total * 1.1 AS TotalComImposto".</summary>
    public List<string> NewColumns { get; set; } = [];

    /// <summary>Colunas que a view já tem (se conhecidas), para evitar nomes repetidos.</summary>
    public List<string> ExistingColumns { get; set; } = [];

    public override string OperationName => "Adicionar colunas à view";
    public override bool HasOwnTransaction => true;
    protected override string ObjectLabel => "View";

    public override string Describe() => $"{OperationName} — {TableDisplay} (+{NewColumns.Count(c => !string.IsNullOrWhiteSpace(c))})";

    /// <summary>Monta a nova definição da view; o erro explica o que impede.</summary>
    public ModuleResult<ViewWithColumns> BuildDefinition()
    {
        var columns = new List<NewViewColumn>();
        foreach (var text in NewColumns.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var column = TsqlModules.ReadNewColumn(text);
            if (!column.Ok)
                return ModuleResult<ViewWithColumns>.Fail(column.Error!);
            columns.Add(column.Value!);
        }

        if (columns.Count == 0)
            return ModuleResult<ViewWithColumns>.Fail("Informe ao menos uma coluna a adicionar.");

        foreach (var duplicate in columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            return ModuleResult<ViewWithColumns>.Fail($"A coluna '{duplicate.Key}' foi informada mais de uma vez.");

        foreach (var column in columns)
            if (ExistingColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
                return ModuleResult<ViewWithColumns>.Fail($"A view já tem uma coluna chamada '{column.Name}'.");

        var result = TsqlModules.AddColumnsToView(Definition, columns);
        if (!result.Ok)
            return result;

        if (!result.Value!.Name.Matches(Schema, Table))
            return ModuleResult<ViewWithColumns>.Fail(
                $"A definição é da view {result.Value.Name}, mas a operação é para {TableDisplay}. Confira o schema e o nome.");

        return result;
    }

    protected override IEnumerable<string> ValidateCore()
    {
        if (string.IsNullOrWhiteSpace(Definition))
        {
            yield return "Informe a definição atual da view (CREATE VIEW ...). Com um banco conectado, ela é carregada ao escolher a view.";
            yield break;
        }

        if (BuildDefinition() is { Ok: false } failure)
            yield return failure.Error!;
    }
}

public sealed class DropViewRequest : ScriptRequest
{
    [JsonIgnore]
    public string ViewName
    {
        get => Table;
        set => Table = value;
    }

    public override string OperationName => "Excluir view";
    protected override string ObjectLabel => "View";

    protected override IEnumerable<string> ValidateCore() => [];
}
