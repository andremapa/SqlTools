using SqlTools.Core.Model;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Requests;

public sealed class CreateTableRequest : ScriptRequest
{
    public List<ColumnDefinition> Columns { get; set; } = [];

    /// <summary>Colunas da chave primária. Vazio = tabela sem PK.</summary>
    public List<string> PrimaryKeyColumns { get; set; } = [];
    public string? PrimaryKeyName { get; set; }
    public bool PrimaryKeyClustered { get; set; } = true;

    /// <summary>Cria o schema caso ainda não exista.</summary>
    public bool CreateSchemaIfMissing { get; set; } = true;

    public override string OperationName => "Criar tabela";

    public string GetPrimaryKeyName() => NameOrDefault(PrimaryKeyName, $"PK_{Table.Trim()}");

    protected override IEnumerable<string> ValidateCore()
    {
        if (Columns.Count == 0)
            yield return "Informe ao menos uma coluna.";

        foreach (var error in Columns.SelectMany(c => c.Validate()))
            yield return error;

        foreach (var duplicate in Columns.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Key.Length > 0 && g.Count() > 1))
            yield return $"Coluna '{duplicate.Key}' declarada mais de uma vez.";

        if (Columns.Count(c => c.IsIdentity) > 1)
            yield return "Apenas uma coluna pode ser IDENTITY.";

        if (Columns.Count(c => c.DataType.Equals("ROWVERSION", StringComparison.OrdinalIgnoreCase)) > 1)
            yield return "Apenas uma coluna pode ser ROWVERSION.";

        if (PrimaryKeyColumns.Count > 0)
        {
            foreach (var error in ValidateColumnList(PrimaryKeyColumns, "Chave primária"))
                yield return error;
            foreach (var error in ValidateOptionalName(PrimaryKeyName, "Nome da PK"))
                yield return error;

            foreach (var pkColumn in PrimaryKeyColumns)
            {
                var column = Columns.FirstOrDefault(c => c.Name.Trim().Equals(pkColumn.Trim(), StringComparison.OrdinalIgnoreCase));
                if (column is null)
                    yield return $"Coluna '{pkColumn}' da chave primária não está na lista de colunas.";
                else if (column.IsNullable)
                    yield return $"Coluna '{pkColumn}' faz parte da chave primária e deve ser NOT NULL.";
            }
        }
    }
}

public sealed class DropTableRequest : ScriptRequest
{
    public override string OperationName => "Excluir tabela";

    protected override IEnumerable<string> ValidateCore() => [];
}

public sealed class AddColumnRequest : ScriptRequest
{
    public ColumnDefinition Column { get; set; } = new();

    /// <summary>Para coluna NULL com default: preenche os registros existentes com o valor padrão (WITH VALUES).</summary>
    public bool FillExistingRowsWithDefault { get; set; } = true;

    public override string OperationName => "Adicionar coluna";
    public override string Describe() => $"{OperationName} — {TableDisplay}.{Column.Name}";

    protected override IEnumerable<string> ValidateCore() => Column.Validate();
}

public sealed class AlterColumnRequest : ScriptRequest
{
    /// <summary>Nova definição da coluna (tipo e nulidade). IDENTITY e DEFAULT não se aplicam.</summary>
    public ColumnDefinition Column { get; set; } = new();

    public override string OperationName => "Alterar coluna";
    public override string Describe() => $"{OperationName} — {TableDisplay}.{Column.Name}";

    protected override IEnumerable<string> ValidateCore() => Column.Validate(allowIdentity: false, allowDefault: false);
}

public sealed class RenameColumnRequest : ScriptRequest
{
    public string ColumnName { get; set; } = "";
    public string NewName { get; set; } = "";

    public override string OperationName => "Renomear coluna";
    public override string Describe() => $"{OperationName} — {TableDisplay}.{ColumnName} → {NewName}";

    protected override IEnumerable<string> ValidateCore()
    {
        if (SqlText.ValidateIdentifier(ColumnName, "Coluna atual") is { } e1) yield return e1;
        if (SqlText.ValidateIdentifier(NewName, "Novo nome") is { } e2) yield return e2;
        if (string.Equals(ColumnName.Trim(), NewName.Trim(), StringComparison.Ordinal) && ColumnName.Trim().Length > 0)
            yield return "O novo nome deve ser diferente do atual.";
    }
}

public sealed class DropColumnRequest : ScriptRequest
{
    public string ColumnName { get; set; } = "";

    /// <summary>Remove automaticamente a constraint DEFAULT vinculada à coluna antes de excluí-la.</summary>
    public bool DropDefaultConstraint { get; set; } = true;

    public override string OperationName => "Excluir coluna";
    public override string Describe() => $"{OperationName} — {TableDisplay}.{ColumnName}";

    protected override IEnumerable<string> ValidateCore()
    {
        if (SqlText.ValidateIdentifier(ColumnName, "Coluna") is { } error)
            yield return error;
    }
}
