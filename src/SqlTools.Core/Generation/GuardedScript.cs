using SqlTools.Core.Sql;

namespace SqlTools.Core.Generation;

internal enum GuardKind
{
    /// <summary>
    /// Validação extra (Safe guard): pré-condição não atendida, o script levanta erro (e faz rollback, se em transação).
    /// Só é escrita com a opção "Safe guard" ligada.
    /// </summary>
    Error,

    /// <summary>
    /// O banco já está no estado desejado (a tabela já existe, a coluna já foi removida...): o script não faz nada.
    /// É a verificação básica que torna o script idempotente, e é sempre escrita.
    /// </summary>
    Skip,
}

/// <param name="RunCondition">Para <see cref="GuardKind.Skip"/>: o inverso da condição, usado quando o script não tem PRINT.</param>
internal sealed record Guard(string Condition, string Message, GuardKind Kind, string? RunCondition = null);

/// <summary>
/// Estrutura comum dos scripts gerados. O básico (padrão) é simples e direto:
/// <code>
/// IF a tabela não existe
/// BEGIN
///     ação
/// END
/// </code>
/// Com "Adicionar comandos PRINT", o ramo "já existe" ganha uma mensagem:
/// <code>
/// IF a tabela existe
///     PRINT 'já existe'
/// ELSE
/// BEGIN
///     ação
///     PRINT sucesso
/// END
/// </code>
/// Com "Safe guard", as validações extras (<see cref="GuardKind.Error"/>) entram antes, em cadeia, na ordem em que foram
/// declaradas (a ordem importa: "a tabela já existe" deve vir antes de "já existe um objeto com esse nome").
/// </summary>
internal sealed class GuardedScript
{
    /// <summary>Linhas escritas antes das verificações, sempre.</summary>
    public List<string> Prelude { get; } = [];

    /// <summary>Linhas escritas antes das verificações, só com Safe guard (preparam dados para as validações extras).</summary>
    public List<string> ExtraPrelude { get; } = [];

    public List<Guard> Guards { get; } = [];
    public required Action<SqlWriter> Action { get; init; }
    public required string SuccessMessage { get; init; }

    /// <summary>Validação extra (só com Safe guard).</summary>
    public GuardedScript Error(string condition, string message)
    {
        Guards.Add(new Guard(condition, message, GuardKind.Error));
        return this;
    }

    /// <summary>Verificação básica de "já está pronto". <paramref name="runCondition"/> é o inverso, para o script sem PRINT ficar legível.</summary>
    public GuardedScript Skip(string condition, string message, string? runCondition = null)
    {
        Guards.Add(new Guard(condition, message, GuardKind.Skip, runCondition));
        return this;
    }

    public void WriteTo(SqlWriter w)
    {
        var prelude = w.SafeGuard ? Prelude.Concat(ExtraPrelude).ToList() : Prelude;
        foreach (var line in prelude)
            w.Line(line);
        if (prelude.Count > 0)
            w.Line();

        var guards = w.SafeGuard ? Guards : Guards.Where(g => g.Kind == GuardKind.Skip).ToList();
        WriteGuards(w, guards, 0);
    }

    /// <summary>Escreve as verificações a partir de <paramref name="start"/> e, por fim, a ação.</summary>
    private void WriteGuards(SqlWriter w, List<Guard> guards, int start)
    {
        var chained = false; // já escrevi um IF neste nível: o próximo é ELSE IF

        for (var i = start; i < guards.Count; i++)
        {
            var guard = guards[i];
            var keyword = chained ? "ELSE IF" : "IF";

            if (guard.Kind == GuardKind.Skip && !w.Prints)
            {
                // Sem PRINT, o ramo "já está pronto" não teria nenhum comando (um IF vazio não existe em T-SQL).
                // Em vez disso, o restante do script vai dentro do inverso da condição.
                w.Line($"{keyword} {guard.RunCondition ?? $"NOT ({guard.Condition})"}");
                w.Begin();
                WriteGuards(w, guards, i + 1);
                w.End();
                return;
            }

            w.Line($"{keyword} {guard.Condition}");
            w.Indented(() => w.Line(guard.Kind == GuardKind.Error
                ? $"RAISERROR({SqlText.RaiserrorLiteral(guard.Message)}, 16, 1);"
                : $"PRINT {SqlText.Literal(guard.Message)};"));
            chained = true;
        }

        if (chained)
        {
            w.Line("ELSE");
            w.Begin();
            Action(w);
            w.Print(SuccessMessage);
            w.End();
        }
        else
        {
            // Sem nenhuma verificação neste nível: o comando vai direto, sem BEGIN/END.
            Action(w);
            w.Print(SuccessMessage);
        }
    }
}
