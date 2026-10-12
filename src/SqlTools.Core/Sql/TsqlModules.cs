using System.Text;

namespace SqlTools.Core.Sql;

/// <summary>Nome (schema opcional + nome) encontrado no cabeçalho de uma definição CREATE VIEW/TRIGGER.</summary>
public sealed record ModuleName(string? Schema, string Name)
{
    public bool Matches(string schema, string name) =>
        string.Equals(Name, name.Trim(), StringComparison.OrdinalIgnoreCase) &&
        (Schema is null || string.Equals(Schema, schema.Trim(), StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Schema is null ? Name : $"{Schema}.{Name}";
}

public sealed record ModuleResult<T>(T? Value, string? Error)
{
    public bool Ok => Error is null;

    public static ModuleResult<T> Success(T value) => new(value, null);
    public static ModuleResult<T> Fail(string error) => new(default, error);
}

/// <summary>Resultado de adicionar colunas ao SELECT de uma view.</summary>
/// <param name="Options">Opções entre o nome e o AS (ex.: WITH SCHEMABINDING). Vazio se não houver.</param>
/// <param name="Body">Tudo depois do AS, já com as novas colunas.</param>
public sealed record ViewWithColumns(ModuleName Name, string Options, string Body);

/// <summary>Uma coluna a adicionar: a expressão como o usuário digitou e o nome que ela terá na view.</summary>
public sealed record NewViewColumn(string Expression, string Name);

/// <summary>
/// Leitura e edição de definições CREATE VIEW / CREATE TRIGGER. Usa um separador de tokens (não regex)
/// para ignorar strings, comentários, identificadores entre colchetes e subconsultas.
/// </summary>
public static class TsqlModules
{
    private static readonly HashSet<string> ForbiddenInColumn = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "INTERSECT", "EXCEPT", "SELECT", "GO",
    };

    /// <summary>
    /// Confere se o texto é um SELECT (ou WITH ... SELECT) utilizável como corpo de view.
    /// Retorna a mensagem de erro, ou null se estiver correto.
    /// </summary>
    public static string? ValidateSelectStatement(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Informe o SELECT da view.";

        var tokens = TsqlLexer.Tokenize(text, out var balanced);
        if (tokens.Count == 0)
            return "Informe o SELECT da view.";
        if (tokens[0].IsWord("CREATE") || tokens[0].IsWord("ALTER"))
            return "Informe apenas o SELECT: o CREATE VIEW é gerado pela aplicação.";
        if (!tokens[0].IsWord("SELECT") && !tokens[0].IsWord("WITH"))
            return "O corpo da view deve começar com SELECT (ou WITH, para CTE).";
        if (!balanced)
            return "Parênteses desbalanceados no SELECT.";
        if (ContainsGoSeparator(text))
            return "Remova a linha com GO: ela separa lotes e não pode fazer parte da view.";
        return null;
    }

    /// <summary>Verdadeiro se alguma linha do texto é apenas "GO" (separador de lotes do SSMS/sqlcmd).</summary>
    public static bool ContainsGoSeparator(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Any(line => line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Lê "CREATE [OR ALTER] VIEW|TRIGGER nome" no começo da definição.
    /// </summary>
    public static ModuleResult<ModuleName> ReadName(string definition, string keyword)
    {
        var parsed = ParseHeader(definition, keyword);
        return parsed.Ok
            ? ModuleResult<ModuleName>.Success(parsed.Value!.Name)
            : ModuleResult<ModuleName>.Fail(parsed.Error!);
    }

    /// <summary>
    /// Lê, em "CREATE TRIGGER nome ON objeto ...", o objeto (tabela ou view) em que a trigger é criada.
    /// </summary>
    public static ModuleResult<ModuleName> ReadTriggerTarget(string definition)
    {
        var parsed = ParseHeader(definition, "TRIGGER");
        if (!parsed.Ok)
            return ModuleResult<ModuleName>.Fail(parsed.Error!);

        var header = parsed.Value!;
        var next = header.NameEndIndex + 1;
        if (next >= header.Tokens.Count || !header.Tokens[next].IsWord("ON"))
            return ModuleResult<ModuleName>.Fail("Depois do nome da trigger deve vir ON <tabela ou view>.");

        var target = ReadQualifiedName(header.Tokens, next + 1);
        return target is null
            ? ModuleResult<ModuleName>.Fail("Não foi possível ler a tabela da trigger (ON ...).")
            : ModuleResult<ModuleName>.Success(target.Value.Name);
    }

    /// <summary>
    /// Insere novas colunas no fim da lista do SELECT principal da view (antes do FROM).
    /// Não suporta views com UNION/INTERSECT/EXCEPT nem com lista de colunas no cabeçalho
    /// (para essas, o SELECT inteiro deve ser editado em "Criar/recriar view").
    /// </summary>
    public static ModuleResult<ViewWithColumns> AddColumnsToView(string definition, IReadOnlyList<NewViewColumn> columns)
    {
        var parsed = ParseHeader(definition, "VIEW");
        if (!parsed.Ok)
            return ModuleResult<ViewWithColumns>.Fail(parsed.Error!);

        var header = parsed.Value!;
        var tokens = header.Tokens;
        var afterName = header.NameEndIndex + 1;

        if (afterName < tokens.Count && tokens[afterName].IsSymbol('('))
            return ModuleResult<ViewWithColumns>.Fail(
                "Esta view declara uma lista de colunas no cabeçalho (CREATE VIEW v (a, b) ...). Edite o SELECT completo em \"Criar/recriar view\".");

        var asIndex = -1;
        for (var i = afterName; i < tokens.Count; i++)
        {
            if (tokens[i].Depth == 0 && tokens[i].IsWord("AS"))
            {
                asIndex = i;
                break;
            }
        }
        if (asIndex < 0)
            return ModuleResult<ViewWithColumns>.Fail("Não encontrei o AS que separa o cabeçalho do SELECT da view.");

        var selectIndex = -1;
        for (var i = asIndex + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Depth == 0 && tokens[i].IsWord("SELECT"))
            {
                selectIndex = i;
                break;
            }
        }
        if (selectIndex < 0)
            return ModuleResult<ViewWithColumns>.Fail("Não encontrei o SELECT principal da view.");

        var fromIndex = -1;
        for (var i = selectIndex + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Depth != 0)
                continue;

            if (token.IsWord("UNION") || token.IsWord("INTERSECT") || token.IsWord("EXCEPT"))
                return ModuleResult<ViewWithColumns>.Fail(
                    "Esta view combina vários SELECT (UNION/INTERSECT/EXCEPT), e não dá para saber em qual deles inserir as colunas. Edite o SELECT completo em \"Criar/recriar view\".");

            if (fromIndex < 0 && token.IsWord("FROM"))
                fromIndex = i;
        }

        // Sem FROM (ex.: SELECT 1 AS a): as colunas entram no fim do comando, antes de ";" finais.
        int lastSelectToken;
        if (fromIndex >= 0)
        {
            lastSelectToken = fromIndex - 1;
        }
        else
        {
            lastSelectToken = tokens.Count - 1;
            while (lastSelectToken > selectIndex && tokens[lastSelectToken].IsSymbol(';'))
                lastSelectToken--;
        }

        if (lastSelectToken <= selectIndex)
            return ModuleResult<ViewWithColumns>.Fail("O SELECT da view não tem nenhuma coluna.");

        var insertAt = tokens[lastSelectToken].End;
        var bodyStart = tokens[asIndex].End;

        // A vírgula entra logo depois da última coluna e as novas vão numa linha própria. Se essa coluna tinha um
        // comentário de linha ("c.Nome -- básico"), ele fica com ela: a vírgula vem antes do comentário.
        var commentEnd = LineCommentEnd(definition, insertAt);
        var additionLines = string.Join(",\n    ", columns.Select(c => c.Expression.Trim()));
        var rebuilt = commentEnd < 0
            ? definition[bodyStart..insertAt] + ",\n    " + additionLines + definition[insertAt..]
            : definition[bodyStart..insertAt] + "," + definition[insertAt..commentEnd] + "\n    " + additionLines + definition[commentEnd..];

        var options = definition[(tokens[header.NameEndIndex].End)..tokens[asIndex].Start].Trim();
        return ModuleResult<ViewWithColumns>.Success(new ViewWithColumns(header.Name, options, rebuilt.Trim()));
    }

    /// <summary>
    /// Valida uma expressão de coluna digitada pelo usuário e descobre o nome que ela terá
    /// (alias com AS, "alias = expr" ou o último trecho de "tabela.coluna").
    /// </summary>
    public static ModuleResult<NewViewColumn> ReadNewColumn(string text)
    {
        var expression = text.Trim().TrimEnd(',', ';').Trim();
        if (expression.Length == 0)
            return ModuleResult<NewViewColumn>.Fail("Há uma coluna vazia na lista.");

        var tokens = TsqlLexer.Tokenize(expression, out var balanced);
        if (!balanced)
            return ModuleResult<NewViewColumn>.Fail($"Parênteses desbalanceados em '{expression}'.");

        foreach (var token in tokens)
        {
            if (token.Depth == 0 && token.Kind == TokenKind.Word && ForbiddenInColumn.Contains(token.Text))
                return ModuleResult<NewViewColumn>.Fail(
                    $"Em '{expression}': informe só a expressão da coluna (sem {token.Text.ToUpperInvariant()}). Subconsultas devem ficar entre parênteses.");
            if (token.Depth == 0 && token.IsSymbol(';'))
                return ModuleResult<NewViewColumn>.Fail($"Em '{expression}': remova o ';'.");
        }

        var name = DeriveName(tokens);
        return name is null
            ? ModuleResult<NewViewColumn>.Fail($"'{expression}' não tem nome de coluna. Acrescente um alias, ex.: {expression} AS NovaColuna.")
            : ModuleResult<NewViewColumn>.Success(new NewViewColumn(expression, name));
    }

    /// <summary>Se logo depois de <paramref name="position"/>, na mesma linha, há um comentário "--", retorna onde ele termina; senão -1.</summary>
    private static int LineCommentEnd(string text, int position)
    {
        var i = position;
        while (i < text.Length && text[i] is ' ' or '\t')
            i++;

        if (i + 1 >= text.Length || text[i] != '-' || text[i + 1] != '-')
            return -1;

        while (i < text.Length && text[i] is not ('\n' or '\r'))
            i++;
        return i;
    }

    private static string? DeriveName(List<Token> tokens)
    {
        if (tokens.Count == 0)
            return null;

        // expr AS alias
        if (tokens.Count >= 3 && tokens[^2].Depth == 0 && tokens[^2].IsWord("AS") &&
            tokens[^1].Kind is TokenKind.Word or TokenKind.Quoted or TokenKind.String)
            return Unquote(tokens[^1]);

        // alias = expr
        if (tokens.Count >= 3 && tokens[0].Kind is TokenKind.Word or TokenKind.Quoted && tokens[1].IsSymbol('=') &&
            !tokens[2].IsSymbol('='))
            return Unquote(tokens[0]);

        // NULL sozinho é um valor, não o nome de uma coluna.
        if (tokens.Count == 1 && tokens[0].IsWord("NULL"))
            return null;

        // tabela.coluna  /  coluna
        var onlyNames = true;
        for (var i = 0; i < tokens.Count; i++)
        {
            var isName = tokens[i].Kind is TokenKind.Word or TokenKind.Quoted;
            var isDot = tokens[i].IsSymbol('.');
            if (i % 2 == 0 ? !isName : !isDot)
            {
                onlyNames = false;
                break;
            }
        }
        if (onlyNames && tokens.Count % 2 == 1)
            return Unquote(tokens[^1]);

        return null;
    }

    private static string Unquote(Token token) =>
        token.Kind == TokenKind.String ? token.Text[1..^1].Replace("''", "'") : token.Unquoted;

    // ---------------------------------------------------------------- cabeçalho

    private sealed record Header(ModuleName Name, int NameEndIndex, List<Token> Tokens);

    private static ModuleResult<Header> ParseHeader(string definition, string keyword)
    {
        if (string.IsNullOrWhiteSpace(definition))
            return ModuleResult<Header>.Fail("Informe a definição (CREATE " + keyword + " ...).");

        var tokens = TsqlLexer.Tokenize(definition, out _);
        var i = 0;
        if (i >= tokens.Count || !tokens[i].IsWord("CREATE"))
            return ModuleResult<Header>.Fail($"A definição precisa começar com CREATE {keyword}.");
        i++;

        if (i + 1 < tokens.Count && tokens[i].IsWord("OR") && tokens[i + 1].IsWord("ALTER"))
            i += 2;

        if (i >= tokens.Count || !tokens[i].IsWord(keyword))
            return ModuleResult<Header>.Fail($"A definição precisa começar com CREATE {keyword}.");
        i++;

        var name = ReadQualifiedName(tokens, i);
        if (name is null)
            return ModuleResult<Header>.Fail($"Não foi possível ler o nome depois de CREATE {keyword}.");

        return ModuleResult<Header>.Success(new Header(name.Value.Name, name.Value.EndIndex, tokens));
    }

    private static (ModuleName Name, int EndIndex)? ReadQualifiedName(List<Token> tokens, int start)
    {
        var parts = new List<string>();
        var i = start;

        while (true)
        {
            if (i >= tokens.Count || tokens[i].Kind is not (TokenKind.Word or TokenKind.Quoted))
                return null;
            parts.Add(tokens[i].Unquoted);

            if (i + 1 < tokens.Count && tokens[i + 1].IsSymbol('.'))
            {
                i += 2;
                continue;
            }
            break;
        }

        return parts.Count switch
        {
            1 => (new ModuleName(null, parts[0]), i),
            2 => (new ModuleName(parts[0], parts[1]), i),
            _ => null, // nome de 3 partes (banco.schema.objeto): não suportado
        };
    }
}
