namespace SqlTools.Core.Sql;

/// <summary>
/// Helpers para escapar identificadores e literais no padrão T-SQL.
/// </summary>
public static class SqlText
{
    public const int MaxIdentifierLength = 128;

    /// <summary>Equivalente ao QUOTENAME do SQL Server: [nome] com ] escapado.</summary>
    public static string Quote(string identifier) => "[" + identifier.Trim().Replace("]", "]]") + "]";

    /// <summary>Literal Unicode: N'texto' com aspas simples duplicadas.</summary>
    public static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

    /// <summary>Texto seguro para RAISERROR, onde % é caractere de formatação.</summary>
    public static string RaiserrorLiteral(string value) => Literal(value.Replace("%", "%%"));

    public static string QuoteList(IEnumerable<string> identifiers) => string.Join(", ", identifiers.Select(Quote));

    /// <summary>Nome legível (sem colchetes) usado em mensagens: schema.objeto.</summary>
    public static string Display(string schema, string name) => $"{schema.Trim()}.{name.Trim()}";

    /// <summary>
    /// Valida um identificador e retorna a mensagem de erro, ou null se válido.
    /// </summary>
    public static string? ValidateIdentifier(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{label} é obrigatório.";
        if (value.Trim().Length > MaxIdentifierLength)
            return $"{label} excede {MaxIdentifierLength} caracteres.";
        return null;
    }
}
