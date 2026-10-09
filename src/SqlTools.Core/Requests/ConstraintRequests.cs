using SqlTools.Core.Model;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Requests;

public sealed class AddPrimaryKeyRequest : ScriptRequest
{
    public string? ConstraintName { get; set; }
    public List<string> Columns { get; set; } = [];
    public bool Clustered { get; set; } = true;

    public override string OperationName => "Adicionar chave primária";
    public override string Describe() => $"{OperationName} — {GetConstraintName()} em {TableDisplay}";

    public string GetConstraintName() => NameOrDefault(ConstraintName, $"PK_{Table.Trim()}");

    protected override IEnumerable<string> ValidateCore() =>
        ValidateOptionalName(ConstraintName, "Nome da constraint").Concat(ValidateColumnList(Columns, "Colunas"));
}

public sealed class AddForeignKeyRequest : ScriptRequest
{
    public string? ConstraintName { get; set; }
    public List<string> Columns { get; set; } = [];

    public string ReferencedSchema { get; set; } = "dbo";
    public string ReferencedTable { get; set; } = "";
    public List<string> ReferencedColumns { get; set; } = [];

    public ReferentialAction OnDelete { get; set; } = ReferentialAction.NoAction;
    public ReferentialAction OnUpdate { get; set; } = ReferentialAction.NoAction;

    /// <summary>WITH CHECK: valida os dados existentes. WITH NOCHECK: só valida novos dados (FK fica "not trusted").</summary>
    public bool CheckExistingData { get; set; } = true;

    public override string OperationName => "Adicionar chave estrangeira";
    public override string Describe() => $"{OperationName} — {GetConstraintName()} em {TableDisplay}";

    public string ReferencedDisplay => SqlText.Display(ReferencedSchema, ReferencedTable);
    public string QualifiedReferencedTable => $"{SqlText.Quote(ReferencedSchema)}.{SqlText.Quote(ReferencedTable)}";

    public string GetConstraintName() => NameOrDefault(ConstraintName, $"FK_{Table.Trim()}_{ReferencedTable.Trim()}");

    protected override IEnumerable<string> ValidateCore()
    {
        foreach (var e in ValidateOptionalName(ConstraintName, "Nome da constraint")) yield return e;
        if (SqlText.ValidateIdentifier(ReferencedSchema, "Schema referenciado") is { } e1) yield return e1;
        if (SqlText.ValidateIdentifier(ReferencedTable, "Tabela referenciada") is { } e2) yield return e2;
        foreach (var e in ValidateColumnList(Columns, "Colunas")) yield return e;
        foreach (var e in ValidateColumnList(ReferencedColumns, "Colunas referenciadas")) yield return e;

        if (Columns.Count > 0 && Columns.Count != ReferencedColumns.Count)
            yield return "A quantidade de colunas deve ser igual à de colunas referenciadas.";
    }
}

public sealed class AddUniqueRequest : ScriptRequest
{
    public string? ConstraintName { get; set; }
    public List<string> Columns { get; set; } = [];
    public bool Clustered { get; set; }

    public override string OperationName => "Adicionar constraint UNIQUE";
    public override string Describe() => $"{OperationName} — {GetConstraintName()} em {TableDisplay}";

    public string GetConstraintName() => NameOrDefault(ConstraintName, $"UQ_{Table.Trim()}_{JoinNames(Columns)}");

    protected override IEnumerable<string> ValidateCore() =>
        ValidateOptionalName(ConstraintName, "Nome da constraint").Concat(ValidateColumnList(Columns, "Colunas"));
}

public sealed class AddCheckRequest : ScriptRequest
{
    public string ConstraintName { get; set; } = "";

    /// <summary>Expressão booleana, ex.: [Valor] &gt;= 0.</summary>
    public string Expression { get; set; } = "";

    public bool CheckExistingData { get; set; } = true;

    public override string OperationName => "Adicionar constraint CHECK";
    public override string Describe() => $"{OperationName} — {ConstraintName} em {TableDisplay}";

    protected override IEnumerable<string> ValidateCore()
    {
        if (SqlText.ValidateIdentifier(ConstraintName, "Nome da constraint") is { } e) yield return e;
        if (string.IsNullOrWhiteSpace(Expression)) yield return "Expressão do CHECK é obrigatória.";
    }
}

public sealed class AddDefaultRequest : ScriptRequest
{
    public string? ConstraintName { get; set; }
    public string ColumnName { get; set; } = "";

    /// <summary>Expressão do valor padrão, ex.: GETDATE(), 0, N'Ativo'.</summary>
    public string Expression { get; set; } = "";

    public override string OperationName => "Adicionar constraint DEFAULT";
    public override string Describe() => $"{OperationName} — {GetConstraintName()} em {TableDisplay}";

    public string GetConstraintName() => NameOrDefault(ConstraintName, $"DF_{Table.Trim()}_{ColumnName.Trim()}");

    protected override IEnumerable<string> ValidateCore()
    {
        foreach (var e in ValidateOptionalName(ConstraintName, "Nome da constraint")) yield return e;
        if (SqlText.ValidateIdentifier(ColumnName, "Coluna") is { } e1) yield return e1;
        if (string.IsNullOrWhiteSpace(Expression)) yield return "Expressão do valor padrão é obrigatória.";
    }
}

public sealed class DropConstraintRequest : ScriptRequest
{
    public string ConstraintName { get; set; } = "";

    public override string OperationName => "Excluir constraint";
    public override string Describe() => $"{OperationName} — {ConstraintName} em {TableDisplay}";

    protected override IEnumerable<string> ValidateCore()
    {
        if (SqlText.ValidateIdentifier(ConstraintName, "Nome da constraint") is { } e) yield return e;
    }
}
