namespace SqlTools.Core.Model;

/// <summary>Tipo de argumento que o tipo de dado aceita.</summary>
public enum TypeArgumentKind
{
    None,
    /// <summary>CHAR(n), NCHAR(n), BINARY(n).</summary>
    Length,
    /// <summary>VARCHAR(n|MAX), NVARCHAR(n|MAX), VARBINARY(n|MAX).</summary>
    LengthOrMax,
    /// <summary>DECIMAL(p,s), NUMERIC(p,s).</summary>
    PrecisionScale,
    /// <summary>DATETIME2(n), TIME(n), DATETIMEOFFSET(n).</summary>
    FractionalSeconds,
}

public sealed record SqlTypeInfo(
    string Name,
    TypeArgumentKind ArgumentKind,
    int MaxLength = 0,
    int DefaultLength = 0,
    bool SupportsIdentity = false);

/// <summary>Tipos de dados do SQL Server suportados pelo gerador.</summary>
public static class SqlTypeCatalog
{
    public static readonly IReadOnlyList<SqlTypeInfo> All =
    [
        new("INT", TypeArgumentKind.None, SupportsIdentity: true),
        new("BIGINT", TypeArgumentKind.None, SupportsIdentity: true),
        new("SMALLINT", TypeArgumentKind.None, SupportsIdentity: true),
        new("TINYINT", TypeArgumentKind.None, SupportsIdentity: true),
        new("BIT", TypeArgumentKind.None),
        new("DECIMAL", TypeArgumentKind.PrecisionScale, SupportsIdentity: true),
        new("NUMERIC", TypeArgumentKind.PrecisionScale, SupportsIdentity: true),
        new("MONEY", TypeArgumentKind.None),
        new("SMALLMONEY", TypeArgumentKind.None),
        new("FLOAT", TypeArgumentKind.None),
        new("REAL", TypeArgumentKind.None),
        new("VARCHAR", TypeArgumentKind.LengthOrMax, MaxLength: 8000, DefaultLength: 50),
        new("NVARCHAR", TypeArgumentKind.LengthOrMax, MaxLength: 4000, DefaultLength: 50),
        new("CHAR", TypeArgumentKind.Length, MaxLength: 8000, DefaultLength: 1),
        new("NCHAR", TypeArgumentKind.Length, MaxLength: 4000, DefaultLength: 1),
        new("DATE", TypeArgumentKind.None),
        new("DATETIME", TypeArgumentKind.None),
        new("DATETIME2", TypeArgumentKind.FractionalSeconds),
        new("SMALLDATETIME", TypeArgumentKind.None),
        new("DATETIMEOFFSET", TypeArgumentKind.FractionalSeconds),
        new("TIME", TypeArgumentKind.FractionalSeconds),
        new("UNIQUEIDENTIFIER", TypeArgumentKind.None),
        new("VARBINARY", TypeArgumentKind.LengthOrMax, MaxLength: 8000, DefaultLength: 50),
        new("BINARY", TypeArgumentKind.Length, MaxLength: 8000, DefaultLength: 1),
        new("XML", TypeArgumentKind.None),
        new("ROWVERSION", TypeArgumentKind.None),
        new("SQL_VARIANT", TypeArgumentKind.None),
    ];

    public static SqlTypeInfo? Find(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
}
