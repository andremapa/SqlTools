using SqlTools.Core.Model;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Requests;

/// <summary>
/// Solicitação que reúne várias operações iguais (ex.: adicionar 25 colunas). O gerador a expande nas solicitações simples
/// correspondentes, então cada coluna ganha as mesmas verificações, opções (Safe guard, PRINT) e idempotência de uma operação
/// avulsa, tudo num único script.
/// </summary>
public interface IExpandableRequest
{
    IReadOnlyList<ScriptRequest> Expand();
}

/// <summary>Adicionar várias colunas a uma tabela de uma vez.</summary>
public sealed class AddColumnsRequest : ScriptRequest, IExpandableRequest
{
    public List<ColumnDefinition> Columns { get; set; } = [];

    /// <summary>Para coluna NULL com default: preenche os registros existentes com o valor padrão (WITH VALUES).</summary>
    public bool FillExistingRowsWithDefault { get; set; } = true;

    public override string OperationName => "Adicionar colunas";
    public override string Describe() => $"{OperationName} — {TableDisplay} ({Columns.Count})";

    public IReadOnlyList<ScriptRequest> Expand() =>
        Columns.Select(c => (ScriptRequest)new AddColumnRequest
        {
            Schema = Schema, Table = Table, Column = c, FillExistingRowsWithDefault = FillExistingRowsWithDefault,
        }).ToList();

    protected override IEnumerable<string> ValidateCore()
    {
        if (Columns.Count == 0)
        {
            yield return "Adicione ao menos uma coluna.";
            yield break;
        }

        foreach (var error in ColumnErrors(Columns, allowIdentity: true, allowDefault: true))
            yield return error;

        if (Columns.Count(c => c.IsIdentity) > 1)
            yield return "Uma tabela só pode ter uma coluna IDENTITY.";
    }

    internal static IEnumerable<string> ColumnErrors(IReadOnlyList<ColumnDefinition> columns, bool allowIdentity, bool allowDefault)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            foreach (var error in columns[i].Validate(allowIdentity, allowDefault))
                // Linha sem nome: a mensagem não diz de qual coluna se trata, então indica a posição na lista.
                yield return string.IsNullOrWhiteSpace(columns[i].Name) ? $"Linha {i + 1}: {error}" : error;
        }

        foreach (var duplicate in columns.Where(c => !string.IsNullOrWhiteSpace(c.Name))
                     .GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"A coluna '{duplicate.Key}' aparece mais de uma vez.";
    }
}

/// <summary>Alterar o tipo e a nulidade de várias colunas de uma vez.</summary>
public sealed class AlterColumnsRequest : ScriptRequest, IExpandableRequest
{
    /// <summary>Nova definição de cada coluna (tipo e nulidade). IDENTITY e DEFAULT não se aplicam.</summary>
    public List<ColumnDefinition> Columns { get; set; } = [];

    public override string OperationName => "Alterar colunas";
    public override string Describe() => $"{OperationName} — {TableDisplay} ({Columns.Count})";

    public IReadOnlyList<ScriptRequest> Expand() =>
        Columns.Select(c => (ScriptRequest)new AlterColumnRequest { Schema = Schema, Table = Table, Column = c }).ToList();

    protected override IEnumerable<string> ValidateCore()
    {
        if (Columns.Count == 0)
        {
            yield return "Adicione ao menos uma coluna.";
            yield break;
        }

        foreach (var error in AddColumnsRequest.ColumnErrors(Columns, allowIdentity: false, allowDefault: false))
            yield return error;
    }
}

public sealed class ColumnRename
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>Renomear várias colunas de uma vez.</summary>
public sealed class RenameColumnsRequest : ScriptRequest, IExpandableRequest
{
    public List<ColumnRename> Renames { get; set; } = [];

    public override string OperationName => "Renomear colunas";
    public override string Describe() => $"{OperationName} — {TableDisplay} ({Renames.Count})";

    public IReadOnlyList<ScriptRequest> Expand() =>
        Renames.Select(r => (ScriptRequest)new RenameColumnRequest { Schema = Schema, Table = Table, ColumnName = r.From, NewName = r.To }).ToList();

    protected override IEnumerable<string> ValidateCore()
    {
        if (Renames.Count == 0)
        {
            yield return "Adicione ao menos uma coluna a renomear.";
            yield break;
        }

        for (var i = 0; i < Renames.Count; i++)
        {
            var rename = Renames[i];
            var label = string.IsNullOrWhiteSpace(rename.From) ? $"Linha {i + 1}" : $"Coluna '{rename.From.Trim()}'";
            if (SqlText.ValidateIdentifier(rename.From, "Coluna atual") is { } e1) yield return $"{label}: {e1}";
            if (SqlText.ValidateIdentifier(rename.To, "Novo nome") is { } e2) yield return $"{label}: {e2}";
            if (rename.From.Trim().Length > 0 && string.Equals(rename.From.Trim(), rename.To.Trim(), StringComparison.Ordinal))
                yield return $"{label}: o novo nome deve ser diferente do atual.";
        }

        var named = Renames.Where(r => r.From.Trim().Length > 0 && r.To.Trim().Length > 0).ToList();

        foreach (var duplicate in named.GroupBy(r => r.From.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"A coluna '{duplicate.Key}' aparece mais de uma vez como coluna atual.";
        foreach (var duplicate in named.GroupBy(r => r.To.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"O novo nome '{duplicate.Key}' é usado por mais de uma coluna.";

        // As renomeações rodam em sequência: um novo nome igual ao nome atual de outra linha (a→b, b→c, ou trocas) depende
        // da ordem e pode falhar. Melhor recusar do que gerar um script que quebra no meio.
        var current = named.Select(r => r.From.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rename in named.Where(r => current.Contains(r.To.Trim()) && !string.Equals(r.From.Trim(), r.To.Trim(), StringComparison.OrdinalIgnoreCase)))
            yield return $"O novo nome '{rename.To.Trim()}' é o nome atual de outra coluna da lista: renomeie em etapas (gere um script por vez).";
    }
}

/// <summary>Excluir várias colunas de uma vez.</summary>
public sealed class DropColumnsRequest : ScriptRequest, IExpandableRequest
{
    public List<string> ColumnNames { get; set; } = [];

    /// <summary>Remove automaticamente a constraint DEFAULT vinculada a cada coluna antes de excluí-la.</summary>
    public bool DropDefaultConstraint { get; set; } = true;

    public override string OperationName => "Excluir colunas";
    public override string Describe() => $"{OperationName} — {TableDisplay} ({ColumnNames.Count})";

    public IReadOnlyList<ScriptRequest> Expand() =>
        ColumnNames.Select(n => (ScriptRequest)new DropColumnRequest
        {
            Schema = Schema, Table = Table, ColumnName = n, DropDefaultConstraint = DropDefaultConstraint,
        }).ToList();

    protected override IEnumerable<string> ValidateCore() => ValidateColumnList(ColumnNames, "Colunas");
}
