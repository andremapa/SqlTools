using System.Text.Json.Serialization;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Requests;

public enum TriggerTiming
{
    After,
    InsteadOf,
}

/// <summary>
/// Criar uma trigger DML numa tabela (ou view). Se já existir uma trigger com esse nome na mesma tabela,
/// ela é removida e recriada (as permissões não se aplicam a triggers; o corpo antigo é substituído).
/// </summary>
public sealed class CreateTriggerRequest : ScriptRequest
{
    /// <summary>Nome da trigger (fica no mesmo schema da tabela).</summary>
    public string TriggerName { get; set; } = "";

    public TriggerTiming Timing { get; set; } = TriggerTiming.After;
    public bool OnInsert { get; set; } = true;
    public bool OnUpdate { get; set; }
    public bool OnDelete { get; set; }

    /// <summary>Comandos executados pela trigger (sem o cabeçalho CREATE TRIGGER nem o BEGIN/END externos).</summary>
    public string Body { get; set; } = "";

    public override string OperationName => "Criar/recriar trigger";
    public override bool HasOwnTransaction => true;

    public override string Describe() => $"{OperationName} — {SqlText.Display(Schema, TriggerName)} em {TableDisplay}";

    public string QualifiedTrigger => QualifiedInSchema(TriggerName.Trim());

    public IEnumerable<string> Events
    {
        get
        {
            if (OnInsert) yield return "INSERT";
            if (OnUpdate) yield return "UPDATE";
            if (OnDelete) yield return "DELETE";
        }
    }

    /// <summary>O comando CREATE TRIGGER completo.</summary>
    public string BuildCreateStatement() =>
        $"CREATE TRIGGER {QualifiedTrigger}\nON {QualifiedTable}\n{(Timing == TriggerTiming.After ? "AFTER" : "INSTEAD OF")} {string.Join(", ", Events)}\n" +
        $"AS\nBEGIN\n    SET NOCOUNT ON;\n\n{Indent(Body.Trim())}\nEND";

    protected override IEnumerable<string> ValidateCore()
    {
        if (SqlText.ValidateIdentifier(TriggerName, "Nome da trigger") is { } nameError)
            yield return nameError;
        if (!Events.Any())
            yield return "Marque ao menos um evento (INSERT, UPDATE ou DELETE).";
        if (string.IsNullOrWhiteSpace(Body))
            yield return "Informe o corpo da trigger (os comandos que ela executa).";
        else if (TsqlModules.ContainsGoSeparator(Body))
            yield return "Remova a linha com GO: ela separa lotes e não pode fazer parte da trigger.";
    }

    private static string Indent(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => line.Length == 0 ? line : "    " + line));
}

/// <summary>
/// Alterar uma trigger existente: o script remove a trigger antiga e a cria de novo com a definição informada.
/// Para "adicionar colunas", edite a definição carregada (por exemplo, a lista de colunas de um INSERT de auditoria).
/// </summary>
public sealed class AlterTriggerRequest : ScriptRequest
{
    [JsonIgnore]
    public string TriggerName
    {
        get => Table;
        set => Table = value;
    }

    /// <summary>A definição completa (CREATE TRIGGER ...), lida do banco ou colada pelo usuário e já editada.</summary>
    public string Definition { get; set; } = "";

    public override string OperationName => "Alterar trigger";
    public override bool HasOwnTransaction => true;
    protected override string ObjectLabel => "Trigger";

    /// <summary>A tabela/view em que a trigger será criada, lida do "ON ..." da definição.</summary>
    public ModuleResult<ModuleName> ReadTarget() => TsqlModules.ReadTriggerTarget(Definition);

    protected override IEnumerable<string> ValidateCore()
    {
        if (string.IsNullOrWhiteSpace(Definition))
        {
            yield return "Informe a definição da trigger (CREATE TRIGGER ...). Com um banco conectado, ela é carregada ao escolher a trigger.";
            yield break;
        }

        var name = TsqlModules.ReadName(Definition, "TRIGGER");
        if (!name.Ok)
        {
            yield return name.Error!;
            yield break;
        }

        if (!name.Value!.Matches(Schema, Table))
            yield return $"A definição é da trigger {name.Value}, mas a operação é para {TableDisplay}. Confira o schema e o nome.";

        if (ReadTarget() is { Ok: false } target)
            yield return target.Error!;

        if (TsqlModules.ContainsGoSeparator(Definition))
            yield return "Remova as linhas com GO: elas separam lotes e não podem fazer parte da trigger.";
    }
}

public sealed class DropTriggerRequest : ScriptRequest
{
    [JsonIgnore]
    public string TriggerName
    {
        get => Table;
        set => Table = value;
    }

    public override string OperationName => "Excluir trigger";
    protected override string ObjectLabel => "Trigger";

    protected override IEnumerable<string> ValidateCore() => [];
}
