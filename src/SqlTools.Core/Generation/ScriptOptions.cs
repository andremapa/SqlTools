namespace SqlTools.Core.Generation;

public sealed class ScriptOptions
{
    /// <summary>Se informado, o script começa com USE [banco].</summary>
    public string? DatabaseName { get; set; }

    /// <summary>Envolve cada operação em BEGIN TRY / TRANSACTION com rollback em caso de erro.</summary>
    public bool UseTransaction { get; set; } = true;

    /// <summary>Inclui cabeçalho de comentário com descrição e data de geração.</summary>
    public bool IncludeHeader { get; set; } = true;

    public ScriptOptions Clone() => (ScriptOptions)MemberwiseClone();
}

public sealed record ScriptResult(string Script, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static ScriptResult Invalid(IReadOnlyList<string> errors) => new("", errors);
}
