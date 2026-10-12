namespace SqlTools.Core.Sql;

internal enum TokenKind
{
    Word,
    /// <summary>Identificador delimitado: [nome] ou "nome".</summary>
    Quoted,
    String,
    Number,
    Symbol,
}

/// <param name="Depth">Nível de parênteses em que o token está (0 = nível principal). Os próprios "(" e ")" ficam no nível externo.</param>
internal readonly record struct Token(TokenKind Kind, string Text, int Start, int End, int Depth)
{
    public bool IsWord(string word) => Kind == TokenKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    public bool IsSymbol(char symbol) => Kind == TokenKind.Symbol && Text[0] == symbol;

    /// <summary>Texto do identificador sem colchetes/aspas (e com o escape desfeito).</summary>
    public string Unquoted => Kind == TokenKind.Quoted
        ? Text[0] == '['
            ? Text[1..^1].Replace("]]", "]")
            : Text[1..^1].Replace("\"\"", "\"")
        : Text;
}

/// <summary>
/// Separador de tokens de T-SQL, suficiente para localizar palavras-chave no nível principal de um comando
/// sem confundi-las com texto dentro de strings, comentários, identificadores ou subconsultas entre parênteses.
/// Comentários e espaços são descartados (as posições dos tokens apontam para o texto original).
/// </summary>
internal static class TsqlLexer
{
    public static List<Token> Tokenize(string sql, out bool balanced)
    {
        var tokens = new List<Token>();
        var depth = 0;
        var negative = false;
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && Peek(sql, i + 1) == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
            }
            else if (c == '/' && Peek(sql, i + 1) == '*')
            {
                i = SkipBlockComment(sql, i);
            }
            else if (c == '\'')
            {
                var end = ScanDelimited(sql, i, '\'', '\'');
                tokens.Add(new Token(TokenKind.String, sql[i..end], i, end, depth));
                i = end;
            }
            else if (c == '[')
            {
                var end = ScanDelimited(sql, i, ']', ']');
                tokens.Add(new Token(TokenKind.Quoted, sql[i..end], i, end, depth));
                i = end;
            }
            else if (c == '"')
            {
                var end = ScanDelimited(sql, i, '"', '"');
                tokens.Add(new Token(TokenKind.Quoted, sql[i..end], i, end, depth));
                i = end;
            }
            else if (IsWordStart(c))
            {
                var start = i;
                while (i < sql.Length && IsWordPart(sql[i]))
                    i++;
                tokens.Add(new Token(TokenKind.Word, sql[start..i], start, i, depth));
            }
            else if (char.IsDigit(c))
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.'))
                    i++;
                tokens.Add(new Token(TokenKind.Number, sql[start..i], start, i, depth));
            }
            else
            {
                if (c == '(')
                {
                    tokens.Add(new Token(TokenKind.Symbol, "(", i, i + 1, depth));
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    negative |= depth < 0;
                    tokens.Add(new Token(TokenKind.Symbol, ")", i, i + 1, Math.Max(depth, 0)));
                }
                else
                {
                    tokens.Add(new Token(TokenKind.Symbol, c.ToString(), i, i + 1, depth));
                }
                i++;
            }
        }

        balanced = depth == 0 && !negative;
        return tokens;
    }

    private static char Peek(string s, int index) => index < s.Length ? s[index] : '\0';

    private static bool IsWordStart(char c) => char.IsLetter(c) || c is '_' or '@' or '#';

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    /// <summary>Lê um trecho delimitado (string ou identificador) em que o delimitador final duplicado é escape.</summary>
    private static int ScanDelimited(string s, int start, char close, char escape)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == close)
            {
                if (Peek(s, i + 1) == escape)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return s.Length; // não terminado: vai até o fim
    }

    /// <summary>Comentários de bloco podem ser aninhados em T-SQL.</summary>
    private static int SkipBlockComment(string s, int start)
    {
        var level = 0;
        var i = start;
        while (i < s.Length)
        {
            if (s[i] == '/' && Peek(s, i + 1) == '*')
            {
                level++;
                i += 2;
            }
            else if (s[i] == '*' && Peek(s, i + 1) == '/')
            {
                level--;
                i += 2;
                if (level == 0)
                    return i;
            }
            else
            {
                i++;
            }
        }
        return s.Length;
    }
}
