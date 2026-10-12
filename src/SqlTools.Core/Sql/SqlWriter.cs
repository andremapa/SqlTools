using System.Text;

namespace SqlTools.Core.Sql;

/// <summary>
/// StringBuilder com controle de indentação para montar scripts legíveis.
/// </summary>
internal sealed class SqlWriter
{
    private const string IndentUnit = "    ";
    private readonly StringBuilder _sb = new();
    private int _indent;

    /// <summary>Opção "Adicionar comandos PRINT": se desligada, nenhum PRINT é escrito no script.</summary>
    public bool Prints { get; init; }

    /// <summary>Opção "Safe guard": se desligada, só a verificação de "já existe" entra no script (sem as validações extras).</summary>
    public bool SafeGuard { get; init; }

    /// <summary>Escreve <c>PRINT N'mensagem';</c>, só se a opção de PRINT estiver ligada.</summary>
    public void Print(string message)
    {
        if (Prints)
            Line($"PRINT {SqlText.Literal(message)};");
    }

    /// <summary>Escreve <c>PRINT expressão;</c> (ex.: concatenação com variável), só se a opção de PRINT estiver ligada.</summary>
    public void PrintExpression(string expression)
    {
        if (Prints)
            Line($"PRINT {expression};");
    }

    public SqlWriter Line(string text = "")
    {
        if (text.Length == 0)
        {
            _sb.AppendLine();
            return this;
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            for (var i = 0; i < _indent; i++)
                _sb.Append(IndentUnit);
            _sb.AppendLine(line);
        }
        return this;
    }

    public void Begin()
    {
        Line("BEGIN");
        _indent++;
    }

    public void End()
    {
        _indent--;
        Line("END");
    }

    public void Indented(Action body)
    {
        _indent++;
        body();
        _indent--;
    }

    public override string ToString() => _sb.ToString();
}
