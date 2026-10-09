using SqlTools.Core.Sql;

namespace SqlTools.Core.Generation;

internal enum GuardKind
{
    /// <summary>Pré-condição não atendida: o script levanta erro (e faz rollback, se em transação).</summary>
    Error,
    /// <summary>O banco já está no estado desejado: o script apenas informa e não faz nada.</summary>
    Skip,
}

internal sealed record Guard(string Condition, string Message, GuardKind Kind);

/// <summary>
/// Estrutura comum de todo script gerado:
/// <code>
/// [prelúdio]
/// IF cond1 RAISERROR/PRINT
/// ELSE IF cond2 RAISERROR/PRINT
/// ELSE
/// BEGIN
///     ação
///     PRINT sucesso
/// END
/// </code>
/// </summary>
internal sealed class GuardedScript
{
    public List<string> Prelude { get; } = [];
    public List<Guard> Guards { get; } = [];
    public required Action<SqlWriter> Action { get; init; }
    public required string SuccessMessage { get; init; }

    public GuardedScript Error(string condition, string message)
    {
        Guards.Add(new Guard(condition, message, GuardKind.Error));
        return this;
    }

    public GuardedScript Skip(string condition, string message)
    {
        Guards.Add(new Guard(condition, message, GuardKind.Skip));
        return this;
    }

    public void WriteTo(SqlWriter w)
    {
        foreach (var line in Prelude)
            w.Line(line);
        if (Prelude.Count > 0)
            w.Line();

        for (var i = 0; i < Guards.Count; i++)
        {
            var guard = Guards[i];
            w.Line($"{(i == 0 ? "IF" : "ELSE IF")} {guard.Condition}");
            w.Indented(() => w.Line(guard.Kind == GuardKind.Error
                ? $"RAISERROR({SqlText.RaiserrorLiteral(guard.Message)}, 16, 1);"
                : $"PRINT {SqlText.Literal(guard.Message)};"));
        }

        if (Guards.Count > 0)
            w.Line("ELSE");
        w.Begin();
        Action(w);
        w.Line($"PRINT {SqlText.Literal(SuccessMessage)};");
        w.End();
    }
}
