using SqlTools.Core.Sql;

namespace SqlTools.Core.Model;

/// <summary>Definição de uma coluna (usada em criação de tabela, adição e alteração de coluna).</summary>
public sealed class ColumnDefinition
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "INT";
    public int? Length { get; set; }
    public bool IsMax { get; set; }
    public int? Precision { get; set; } = 18;
    public int? Scale { get; set; } = 2;
    public int? FractionalSeconds { get; set; } = 7;
    public bool IsNullable { get; set; } = true;

    public bool IsIdentity { get; set; }
    public long IdentitySeed { get; set; } = 1;
    public long IdentityIncrement { get; set; } = 1;

    /// <summary>Expressão SQL do valor padrão, ex.: GETDATE(), 0, N'Ativo'. Vazio = sem default.</summary>
    public string? DefaultExpression { get; set; }

    /// <summary>Nome da constraint de default. Vazio = DF_{Tabela}_{Coluna}.</summary>
    public string? DefaultConstraintName { get; set; }

    public bool HasDefault => !string.IsNullOrWhiteSpace(DefaultExpression);

    public SqlTypeInfo? TypeInfo => SqlTypeCatalog.Find(DataType);

    public string GetDefaultConstraintName(string table) =>
        string.IsNullOrWhiteSpace(DefaultConstraintName)
            ? $"DF_{table.Trim()}_{Name.Trim()}"
            : DefaultConstraintName.Trim();

    /// <summary>Tipo formatado, ex.: NVARCHAR(100), DECIMAL(18,2), DATETIME2(7).</summary>
    public string FormatType()
    {
        var info = TypeInfo ?? throw new InvalidOperationException($"Tipo de dado desconhecido: {DataType}");
        return info.ArgumentKind switch
        {
            TypeArgumentKind.Length => $"{info.Name}({Length ?? info.DefaultLength})",
            TypeArgumentKind.LengthOrMax => IsMax ? $"{info.Name}(MAX)" : $"{info.Name}({Length ?? info.DefaultLength})",
            TypeArgumentKind.PrecisionScale => $"{info.Name}({Precision ?? 18},{Scale ?? 0})",
            TypeArgumentKind.FractionalSeconds => $"{info.Name}({FractionalSeconds ?? 7})",
            _ => info.Name,
        };
    }

    /// <summary>Valida a coluna. Os flags indicam quais recursos são aplicáveis no contexto.</summary>
    public IEnumerable<string> Validate(bool allowIdentity = true, bool allowDefault = true)
    {
        var label = string.IsNullOrWhiteSpace(Name) ? "Coluna" : $"Coluna '{Name.Trim()}'";

        if (SqlText.ValidateIdentifier(Name, "Nome da coluna") is { } nameError)
            yield return nameError;

        var info = TypeInfo;
        if (info is null)
        {
            yield return $"{label}: tipo de dado '{DataType}' não suportado.";
            yield break;
        }

        switch (info.ArgumentKind)
        {
            case TypeArgumentKind.Length:
            case TypeArgumentKind.LengthOrMax when !IsMax:
                var length = Length ?? info.DefaultLength;
                if (length < 1 || length > info.MaxLength)
                    yield return $"{label}: tamanho deve estar entre 1 e {info.MaxLength} (ou MAX, quando permitido).";
                break;
            case TypeArgumentKind.PrecisionScale:
                var precision = Precision ?? 18;
                var scale = Scale ?? 0;
                if (precision is < 1 or > 38)
                    yield return $"{label}: precisão deve estar entre 1 e 38.";
                if (scale < 0 || scale > precision)
                    yield return $"{label}: escala deve estar entre 0 e a precisão ({precision}).";
                break;
            case TypeArgumentKind.FractionalSeconds:
                if (FractionalSeconds is < 0 or > 7)
                    yield return $"{label}: precisão de frações de segundo deve estar entre 0 e 7.";
                break;
        }

        if (IsIdentity)
        {
            if (!allowIdentity)
                yield return $"{label}: IDENTITY não é aplicável nesta operação.";
            else if (!info.SupportsIdentity)
                yield return $"{label}: IDENTITY só é permitido em tipos inteiros ou DECIMAL/NUMERIC com escala 0.";
            else if (info.ArgumentKind == TypeArgumentKind.PrecisionScale && (Scale ?? 0) != 0)
                yield return $"{label}: IDENTITY em {info.Name} exige escala 0.";
            if (IsNullable)
                yield return $"{label}: coluna IDENTITY não pode aceitar NULL.";
            if (IdentityIncrement == 0)
                yield return $"{label}: incremento do IDENTITY não pode ser 0.";
            if (HasDefault)
                yield return $"{label}: coluna IDENTITY não pode ter valor padrão.";
        }

        if (HasDefault && !allowDefault)
            yield return $"{label}: valor padrão não é aplicável nesta operação (use 'Adicionar Default').";

        if (!string.IsNullOrWhiteSpace(DefaultConstraintName) &&
            SqlText.ValidateIdentifier(DefaultConstraintName, "Nome da constraint default") is { } dfError)
            yield return $"{label}: {dfError}";
    }

    /// <summary>Gera a definição da coluna para CREATE TABLE / ALTER TABLE ADD.</summary>
    public string ToDefinitionSql(string table, bool includeIdentityAndDefault = true)
    {
        var sql = $"{SqlText.Quote(Name)} {FormatType()}";
        if (includeIdentityAndDefault && IsIdentity)
            sql += $" IDENTITY({IdentitySeed},{IdentityIncrement})";
        sql += IsNullable ? " NULL" : " NOT NULL";
        if (includeIdentityAndDefault && HasDefault)
            sql += $" CONSTRAINT {SqlText.Quote(GetDefaultConstraintName(table))} DEFAULT ({DefaultExpression!.Trim()})";
        return sql;
    }

    public ColumnDefinition Clone() => (ColumnDefinition)MemberwiseClone();
}
