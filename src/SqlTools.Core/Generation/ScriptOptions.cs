namespace SqlTools.Core.Generation;

public sealed class ScriptOptions
{
    /// <summary>Se informado, o script começa com USE [banco].</summary>
    public string? DatabaseName { get; set; }

    /// <summary>Envolve cada operação em BEGIN TRY / TRANSACTION com rollback em caso de erro.</summary>
    public bool UseTransaction { get; set; }

    /// <summary>Inclui cabeçalho de comentário com descrição e data de geração.</summary>
    public bool IncludeHeader { get; set; }

    /// <summary>
    /// Adiciona comandos PRINT que informam o que o script fez ("Tabela criada.", "já existe..."). Desligado por padrão:
    /// nenhum PRINT é escrito. Erros (RAISERROR) continuam sempre presentes, pois sinalizam falhas.
    /// </summary>
    public bool AddPrintCommands { get; set; }

    /// <summary>
    /// "Safe guard": acrescenta as validações extras ao script (tabela/coluna/schema inexistente, nome em uso, dados que
    /// impedem a alteração, dependências...). Desligado por padrão: o script só verifica se o objeto já existe (ou já foi
    /// removido), o que o torna idempotente, e deixa o SQL Server acusar qualquer outro problema. Nas views e triggers,
    /// liga também a transação que desfaz o DROP se o CREATE falhar.
    /// </summary>
    public bool SafeGuard { get; set; }

    public ScriptOptions Clone() => (ScriptOptions)MemberwiseClone();
}

public sealed record ScriptResult(string Script, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static ScriptResult Invalid(IReadOnlyList<string> errors) => new("", errors);
}
