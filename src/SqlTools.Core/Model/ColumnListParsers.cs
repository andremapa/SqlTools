using SqlTools.Core.Requests;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Model;

public sealed record ColumnImportResult<T>(List<T> Items, List<string> Errors);

/// <summary>
/// Lê uma lista de colunas escrita como em um CREATE TABLE, uma por linha:
/// <code>
/// Nome        NVARCHAR(100) NOT NULL
/// Email       VARCHAR(150)
/// Saldo       DECIMAL(12,2) NOT NULL DEFAULT 0
/// CriadoEm    DATETIME2(3) NOT NULL CONSTRAINT DF_X_CriadoEm DEFAULT SYSDATETIME()
/// Codigo      INT IDENTITY(1,1)
/// </code>
/// Linhas em branco, comentários (<c>--</c>) e a vírgula no fim da linha são ignorados. Linhas inválidas viram mensagens de erro
/// com o número da linha; as válidas são aproveitadas.
/// </summary>
public static class ColumnDefinitionParser
{
    private static readonly HashSet<string> NotColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "FOREIGN", "CREATE", "ALTER", "INDEX", "GO",
    };

    public static ColumnImportResult<ColumnDefinition> Parse(string? text)
    {
        var columns = new List<ColumnDefinition>();
        var errors = new List<string>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim().TrimEnd(',').Trim();
            if (line.Length == 0 || line.StartsWith("--") || line is "(" or ")" or ");")
                continue;

            var parsed = ParseLine(line);
            if (parsed.Error is { } error)
                errors.Add($"Linha {i + 1}: {error}");
            else
                columns.Add(parsed.Column!);
        }

        return new ColumnImportResult<ColumnDefinition>(columns, errors);
    }

    private sealed record LineResult(ColumnDefinition? Column, string? Error);

    private static LineResult Fail(string error) => new(null, error);

    private static LineResult ParseLine(string line)
    {
        var tokens = TsqlLexer.Tokenize(line, out var balanced);
        if (!balanced)
            return Fail("parênteses desbalanceados.");
        if (tokens.Count < 2)
            return Fail("informe o nome e o tipo, ex.: Nome NVARCHAR(100) NOT NULL.");

        if (tokens[0].Kind is not (TokenKind.Word or TokenKind.Quoted))
            return Fail("a linha deve começar pelo nome da coluna.");
        if (tokens[0].Kind == TokenKind.Word && NotColumns.Contains(tokens[0].Text))
            return Fail($"'{tokens[0].Text}' não é uma coluna (constraints e comandos não são importados aqui).");
        var name = tokens[0].Unquoted;

        if (tokens[1].Kind is not (TokenKind.Word or TokenKind.Quoted))
            return Fail($"informe o tipo de '{name}'.");
        var type = SqlTypeCatalog.Find(tokens[1].Unquoted);
        if (type is null)
            return Fail($"tipo '{tokens[1].Unquoted}' não suportado.");

        var column = new ColumnDefinition { Name = name, DataType = type.Name };
        var i = 2;

        // Argumentos do tipo: (100), (MAX), (18,2), (3)
        var args = new List<string>();
        if (i < tokens.Count && tokens[i].IsSymbol('('))
        {
            i++;
            while (i < tokens.Count && !(tokens[i].IsSymbol(')') && tokens[i].Depth == 0))
            {
                if (!tokens[i].IsSymbol(','))
                    args.Add(tokens[i].Text);
                i++;
            }
            if (i >= tokens.Count)
                return Fail("faltou fechar o parêntese do tipo.");
            i++; // ")"
        }

        if (ApplyTypeArguments(column, type, args) is { } argumentError)
            return Fail(argumentError);

        // Opções, em qualquer ordem: NULL, NOT NULL, IDENTITY[(seed,incremento)], [CONSTRAINT nome] DEFAULT expressão
        var nullSpecified = false;
        while (i < tokens.Count)
        {
            var token = tokens[i];

            if (token.IsWord("NOT") && i + 1 < tokens.Count && tokens[i + 1].IsWord("NULL"))
            {
                column.IsNullable = false;
                nullSpecified = true;
                i += 2;
            }
            else if (token.IsWord("NULL"))
            {
                column.IsNullable = true;
                nullSpecified = true;
                i++;
            }
            else if (token.IsWord("IDENTITY"))
            {
                column.IsIdentity = true;
                i++;
                if (i < tokens.Count && tokens[i].IsSymbol('('))
                {
                    var numbers = new List<long>();
                    i++;
                    while (i < tokens.Count && !tokens[i].IsSymbol(')'))
                    {
                        if (tokens[i].Kind == TokenKind.Number && long.TryParse(tokens[i].Text, out var n))
                            numbers.Add(n);
                        else if (!tokens[i].IsSymbol(',') && !tokens[i].IsSymbol('-'))
                            return Fail($"IDENTITY(seed, incremento) inválido em '{name}'.");
                        i++;
                    }
                    if (numbers.Count != 2)
                        return Fail($"IDENTITY de '{name}' precisa de dois números: IDENTITY(1,1).");
                    column.IdentitySeed = numbers[0];
                    column.IdentityIncrement = numbers[1];
                    i++; // ")"
                }
            }
            else if (token.IsWord("CONSTRAINT") || token.IsWord("DEFAULT"))
            {
                if (token.IsWord("CONSTRAINT"))
                {
                    if (i + 2 >= tokens.Count || !tokens[i + 2].IsWord("DEFAULT"))
                        return Fail($"depois de CONSTRAINT {(i + 1 < tokens.Count ? tokens[i + 1].Unquoted : "")} só é aceito DEFAULT.");
                    column.DefaultConstraintName = tokens[i + 1].Unquoted;
                    i += 2;
                }

                // A expressão vai até o fim da linha (exceto um "NOT NULL" final, comum depois do default).
                var expressionTokens = tokens.Skip(i + 1).ToList();
                if (expressionTokens.Count >= 3 && expressionTokens[^2].IsWord("NOT") && expressionTokens[^1].IsWord("NULL"))
                {
                    column.IsNullable = false;
                    nullSpecified = true;
                    expressionTokens = expressionTokens[..^2];
                }

                if (expressionTokens.Count == 0)
                    return Fail($"falta a expressão do DEFAULT de '{name}'.");

                column.DefaultExpression = UnwrapParentheses(line[expressionTokens[0].Start..expressionTokens[^1].End].Trim(), expressionTokens);
                i = tokens.Count;
            }
            else if (token.IsWord("PRIMARY") || token.IsWord("UNIQUE") || token.IsWord("REFERENCES") || token.IsWord("CHECK"))
            {
                return Fail($"{token.Text.ToUpperInvariant()} em '{name}' não é importado aqui: use o menu Constraints.");
            }
            else
            {
                return Fail($"não entendi '{token.Text}' na coluna '{name}'.");
            }
        }

        // IDENTITY implica NOT NULL, e sem NULL/NOT NULL a coluna aceita NULL (o padrão do SQL Server).
        if (column.IsIdentity)
            column.IsNullable = false;
        else if (!nullSpecified)
            column.IsNullable = true;

        return new LineResult(column, null);
    }

    private static string? ApplyTypeArguments(ColumnDefinition column, SqlTypeInfo type, List<string> args)
    {
        static bool TryNumber(string text, out int value) => int.TryParse(text, out value);

        switch (type.ArgumentKind)
        {
            case TypeArgumentKind.None:
                return args.Count > 0 ? $"o tipo {type.Name} não recebe argumentos." : null;

            case TypeArgumentKind.Length:
                if (args.Count == 0) return null;
                return args.Count == 1 && TryNumber(args[0], out var length) ? Assign(() => column.Length = length) : $"tamanho inválido em {type.Name}.";

            case TypeArgumentKind.LengthOrMax:
                if (args.Count == 0) return null;
                if (args.Count == 1 && args[0].Equals("MAX", StringComparison.OrdinalIgnoreCase)) return Assign(() => column.IsMax = true);
                return args.Count == 1 && TryNumber(args[0], out var size) ? Assign(() => column.Length = size) : $"tamanho inválido em {type.Name}: use um número ou MAX.";

            case TypeArgumentKind.PrecisionScale:
                if (args.Count == 0) return Assign(() => { column.Precision = 18; column.Scale = 0; });
                if (args.Count == 1 && TryNumber(args[0], out var p)) return Assign(() => { column.Precision = p; column.Scale = 0; });
                return args.Count == 2 && TryNumber(args[0], out var precision) && TryNumber(args[1], out var scale)
                    ? Assign(() => { column.Precision = precision; column.Scale = scale; })
                    : $"precisão/escala inválidas em {type.Name}, ex.: {type.Name}(18,2).";

            case TypeArgumentKind.FractionalSeconds:
                if (args.Count == 0) return null;
                return args.Count == 1 && TryNumber(args[0], out var fraction) ? Assign(() => column.FractionalSeconds = fraction) : $"precisão inválida em {type.Name}.";

            default:
                return null;
        }
    }

    private static string? Assign(Action action)
    {
        action();
        return null;
    }

    /// <summary>O gerador já coloca a expressão entre parênteses: "(0)" vira "0".</summary>
    private static string UnwrapParentheses(string expression, List<Token> tokens) =>
        tokens.Count >= 2 && tokens[0].IsSymbol('(') && tokens[^1].IsSymbol(')') &&
        tokens.Skip(1).Take(tokens.Count - 2).All(t => t.Depth >= 1)
            ? expression[1..^1].Trim()
            : expression;
}

/// <summary>
/// Lê uma lista de renomeações, uma por linha: <c>Atual -> Novo</c>, <c>Atual;Novo</c>, <c>Atual,Novo</c>,
/// <c>Atual&lt;tab&gt;Novo</c> (cola do Excel) ou <c>Atual Novo</c>. Nomes com espaço vão entre colchetes.
/// </summary>
public static class ColumnRenameParser
{
    private static readonly HashSet<char> Separators = ['-', '>', '=', ',', ';'];

    public static ColumnImportResult<ColumnRename> Parse(string? text)
    {
        var renames = new List<ColumnRename>();
        var errors = new List<string>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("--"))
                continue;

            var tokens = TsqlLexer.Tokenize(line, out _);
            var names = tokens.Where(t => t.Kind is TokenKind.Word or TokenKind.Quoted).ToList();
            var onlySeparatorsBetween = tokens.Where(t => t.Kind is not (TokenKind.Word or TokenKind.Quoted))
                .All(t => t.Kind == TokenKind.Symbol && Separators.Contains(t.Text[0]));

            if (names.Count != 2 || !onlySeparatorsBetween)
                errors.Add($"Linha {i + 1}: use o formato \"Atual -> Novo\" (dois nomes).");
            else
                renames.Add(new ColumnRename { From = names[0].Unquoted, To = names[1].Unquoted });
        }

        return new ColumnImportResult<ColumnRename>(renames, errors);
    }
}
