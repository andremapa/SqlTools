using SqlTools.Core.Requests;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Generation;

public interface IScriptGenerator
{
    ScriptResult Generate(ScriptRequest request, ScriptOptions options);
    ScriptResult GenerateBatch(IReadOnlyList<ScriptRequest> requests, ScriptOptions options);
}

/// <summary>
/// Gera scripts T-SQL idempotentes e validados para SQL Server.
/// Cada operação é um lote (terminado em GO) com verificações no catálogo antes da alteração.
/// </summary>
public sealed partial class SqlServerScriptGenerator(TimeProvider? timeProvider = null) : IScriptGenerator
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public ScriptResult Generate(ScriptRequest request, ScriptOptions options) => GenerateBatch([request], options);

    public ScriptResult GenerateBatch(IReadOnlyList<ScriptRequest> requests, ScriptOptions options)
    {
        if (requests.Count == 0)
            return ScriptResult.Invalid(["Nenhuma operação informada."]);

        var errors = new List<string>();
        for (var i = 0; i < requests.Count; i++)
        {
            var prefix = requests.Count > 1 ? $"[{i + 1}] {requests[i].OperationName}: " : "";
            errors.AddRange(requests[i].Validate().Select(e => prefix + e));
        }
        if (!string.IsNullOrWhiteSpace(options.DatabaseName) &&
            SqlText.ValidateIdentifier(options.DatabaseName, "Banco de dados") is { } dbError)
            errors.Add(dbError);
        if (errors.Count > 0)
            return ScriptResult.Invalid(errors);

        var w = new SqlWriter { Prints = options.AddPrintCommands, SafeGuard = options.SafeGuard };

        if (options.IncludeHeader)
            WriteHeader(w, requests);

        if (!string.IsNullOrWhiteSpace(options.DatabaseName))
        {
            w.Line($"USE {SqlText.Quote(options.DatabaseName)};");
            w.Line("GO");
            w.Line();
        }

        // Solicitações compostas (ex.: 25 colunas) viram uma operação por item, cada uma com suas verificações.
        var operations = requests.SelectMany(Expand).ToList();

        for (var i = 0; i < operations.Count; i++)
        {
            if (operations.Count > 1)
                w.Line($"-- [{i + 1}/{operations.Count}] {operations[i].Describe()}");

            // View/trigger: DROP, GO, CREATE, GO, com transação própria atravessando os lotes (a opção "Transação" não se aplica).
            if (BuildRecreate(operations[i]) is { } recreate)
                WriteRecreateScript(w, recreate);
            else
                WriteBatch(w, Build(operations[i]), options);
            w.Line();
        }

        return new ScriptResult(w.ToString().TrimEnd() + Environment.NewLine, []);
    }

    private static IEnumerable<ScriptRequest> Expand(ScriptRequest request) =>
        request is IExpandableRequest expandable ? expandable.Expand().SelectMany(Expand) : [request];

    private void WriteHeader(SqlWriter w, IReadOnlyList<ScriptRequest> requests)
    {
        w.Line("-- =============================================================");
        if (requests.Count == 1)
        {
            w.Line($"-- Operação : {requests[0].Describe()}");
        }
        else
        {
            w.Line($"-- Operações: {requests.Count}");
            for (var i = 0; i < requests.Count; i++)
                w.Line($"--   {i + 1}. {requests[i].Describe()}");
        }
        w.Line($"-- Gerado em: {_time.GetLocalNow():yyyy-MM-dd HH:mm:ss}");
        w.Line("-- Gerado por: SqlTools (script idempotente: pode ser reexecutado)");
        w.Line("-- =============================================================");
        w.Line();
    }

    private static void WriteBatch(SqlWriter w, GuardedScript script, ScriptOptions options)
    {
        if (!options.UseTransaction)
        {
            script.WriteTo(w);
            w.Line("GO");
            return;
        }

        w.Line("SET XACT_ABORT ON;");
        w.Line("BEGIN TRY");
        w.Indented(() =>
        {
            w.Line("BEGIN TRANSACTION;");
            w.Line();
            script.WriteTo(w);
            w.Line();
            w.Line("COMMIT TRANSACTION;");
        });
        w.Line("END TRY");
        w.Line("BEGIN CATCH");
        w.Indented(() =>
        {
            w.Line("IF @@TRANCOUNT > 0");
            w.Indented(() => w.Line("ROLLBACK TRANSACTION;"));
            w.Line("THROW;");
        });
        w.Line("END CATCH");
        w.Line("GO");
    }

    private static GuardedScript Build(ScriptRequest request) => request switch
    {
        CreateTableRequest r => BuildCreateTable(r),
        DropTableRequest r => BuildDropTable(r),
        AddColumnRequest r => BuildAddColumn(r),
        AlterColumnRequest r => BuildAlterColumn(r),
        RenameColumnRequest r => BuildRenameColumn(r),
        DropColumnRequest r => BuildDropColumn(r),
        AddPrimaryKeyRequest r => BuildAddPrimaryKey(r),
        AddForeignKeyRequest r => BuildAddForeignKey(r),
        AddUniqueRequest r => BuildAddUnique(r),
        AddCheckRequest r => BuildAddCheck(r),
        AddDefaultRequest r => BuildAddDefault(r),
        DropConstraintRequest r => BuildDropConstraint(r),
        CreateIndexRequest r => BuildCreateIndex(r),
        DropIndexRequest r => BuildDropIndex(r),
        DropViewRequest r => BuildDropView(r),
        DropTriggerRequest r => BuildDropTrigger(r),
        _ => throw new NotSupportedException($"Operação não suportada: {request.GetType().Name}"),
    };

    private static string TableMissingMessage(ScriptRequest r) => $"Tabela {r.TableDisplay} não existe.";

    private static string NameInUseMessage(ScriptRequest r, string name) =>
        $"Já existe outro objeto chamado {SqlText.Display(r.Schema, name)} no banco.";
}
