using SqlTools.Core.Generation;
using SqlTools.Core.Requests;

namespace SqlTools.Core.Tests;

/// <summary>O formato do script de view/trigger: DROP, GO, CREATE (num lote só dele), GO, com a transação protegendo a troca.</summary>
public class ModuleScriptFormatTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly SqlServerScriptGenerator _generator = new();

    private static string[] Lines(string script) => script.Replace("\r\n", "\n").Split('\n');

    private string Generate(ScriptRequest request, ScriptOptions? options = null)
    {
        var result = _generator.Generate(request, options ?? new ScriptOptions { AddPrintCommands = true, SafeGuard = true });
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return result.Script;
    }

    [Fact]
    public void CreateView_IsDropThenGoThenCreateInItsOwnBatch()
    {
        var script = Generate(new CreateViewRequest { ViewName = "vw_X", SelectStatement = "SELECT Id FROM dbo.T WHERE Tipo = 'a''b'" });

        var lines = Lines(script);
        var firstGo = Array.IndexOf(lines, "GO");
        var createLine = Array.FindIndex(lines, l => l.StartsWith("CREATE VIEW"));

        Assert.True(createLine > firstGo, "o CREATE VIEW vem depois do primeiro GO");
        Assert.Equal(["GO", "", "CREATE VIEW [dbo].[vw_X]", "AS", "SELECT Id FROM dbo.T WHERE Tipo = 'a''b'", "GO"], lines[firstGo..(firstGo + 6)]);

        // O DROP fica no lote anterior, só se a view existir.
        Assert.Contains("IF OBJECT_ID(N'[dbo].[vw_X]', N'V') IS NOT NULL", script);
        Assert.Contains("DROP VIEW [dbo].[vw_X];", script);

        // O código do usuário vai como está: sem SQL dinâmico e sem aspas duplicadas.
        Assert.DoesNotContain("sp_executesql", script);
        Assert.DoesNotContain("''a''''b''", script);
    }

    [Fact]
    public void CreateView_ProtectsTheSwapWithATransactionThatSpansTheBatches()
    {
        var lines = Lines(Generate(new CreateViewRequest { ViewName = "vw_X", SelectStatement = "SELECT 1 AS a" }));
        var script = string.Join("\n", lines);

        var begin = script.IndexOf("BEGIN TRANSACTION;", StringComparison.Ordinal);
        var drop = script.IndexOf("DROP VIEW", StringComparison.Ordinal);
        var create = script.IndexOf("CREATE VIEW", StringComparison.Ordinal);
        var commit = script.IndexOf("COMMIT TRANSACTION", StringComparison.Ordinal);
        var rollback = script.IndexOf("ROLLBACK TRANSACTION", StringComparison.Ordinal);

        Assert.True(begin >= 0 && begin < drop && drop < create && create < commit && commit < rollback);
        Assert.Equal(3, lines.Count(l => l == "GO"));
    }

    [Fact]
    public void ViewAndTriggerScripts_IgnoreTheGlobalTransactionOption()
    {
        ScriptRequest[] requests =
        [
            new CreateViewRequest { ViewName = "vw_X", SelectStatement = "SELECT 1 AS a" },
            new CreateTriggerRequest { Table = "T", TriggerName = "trg_T", OnInsert = true, Body = "SELECT 1;" },
        ];

        foreach (var request in requests)
        {
            Assert.True(request.HasOwnTransaction);
            Assert.Equal(Generate(request, new ScriptOptions { UseTransaction = false }), Generate(request, new ScriptOptions { UseTransaction = true }));
            Assert.DoesNotContain("BEGIN TRY", Generate(request, new ScriptOptions { UseTransaction = true }));
        }
    }

    [Fact]
    public void OtherOperations_DoNotHaveTheirOwnTransaction()
    {
        Assert.False(new CreateTableRequest().HasOwnTransaction);
        Assert.False(new DropViewRequest().HasOwnTransaction);
    }

    [Fact]
    public void CreateTrigger_OnlyDropsATriggerOfTheSameTable()
    {
        var script = Generate(new CreateTriggerRequest { Table = "Cliente", TriggerName = "trg_C", OnInsert = true, Body = "SELECT 1;" });

        Assert.Contains("parent_id = OBJECT_ID(N'[dbo].[Cliente]')", script);
        Assert.Contains("RAISERROR(N'Já existe uma trigger dbo.trg_C em outra tabela", script);
        Assert.Contains("CREATE TRIGGER [dbo].[trg_C]\nON [dbo].[Cliente]\nAFTER INSERT\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    SELECT 1;\nEND", string.Join("\n", Lines(script)));
    }

    [Fact]
    public void MixedBatch_KeepsEachOperationInItsOwnFormat()
    {
        var result = _generator.GenerateBatch(
        [
            new CreateTableRequest { Table = "T", Columns = [new() { Name = "Id", IsNullable = false }] },
            new CreateViewRequest { ViewName = "vw_T", SelectStatement = "SELECT Id FROM dbo.T" },
            new DropTriggerRequest { TriggerName = "trg_T" },
        ], new ScriptOptions { UseTransaction = true });

        Assert.True(result.IsValid);
        var lines = Lines(result.Script);
        Assert.Contains("BEGIN TRY", result.Script);          // a tabela usa o modo transação
        Assert.Equal(1, lines.Count(l => l.StartsWith("CREATE VIEW")));
        // Dentro do TRY só as operações comuns (criar tabela, excluir trigger); a da view tem transação própria, fora do TRY.
        Assert.Equal(2, lines.Count(l => l == "    BEGIN TRANSACTION;"));
    }

    // ------------------------------------------------------------------ execução imitando o SSMS

    private ScriptRequest RequestToBreak(string view) =>
        new CreateViewRequest { ViewName = view, SelectStatement = "SELECT ColunaQueNaoExiste FROM dbo.TSsms" };

    [LocalDbFact]
    public void Ssms_WhenTheCreateFails_TheLastBatchRollsBackTheDrop()
    {
        db.Run("CREATE TABLE dbo.TSsms (Id INT, Nome NVARCHAR(20));");
        db.Run(Generate(new CreateViewRequest { ViewName = "vw_Ssms", SelectStatement = "SELECT Id FROM dbo.TSsms" }));
        var before = db.Scalar<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.vw_Ssms'))");

        // No SSMS o erro de um lote não impede os próximos: o último lote precisa desfazer o DROP.
        var run = db.RunAll(Generate(RequestToBreak("vw_Ssms")));

        Assert.Contains(run.Errors, e => e.Contains("ColunaQueNaoExiste"));
        Assert.Contains(run.Errors, e => e.Contains("nada foi alterado"));
        Assert.Equal(0, run.FinalTranCount);
        Assert.Equal(before, db.Scalar<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.vw_Ssms'))"));
    }

    [LocalDbFact]
    public void Ssms_WhenTheCreateFails_ForANewView_NothingIsLeftBehind()
    {
        db.Run("CREATE TABLE dbo.TSsms2 (Id INT);");

        var run = db.RunAll(Generate(new CreateViewRequest { ViewName = "vw_Ssms2", SelectStatement = "SELECT Nada FROM dbo.TSsms2" }));

        Assert.NotEmpty(run.Errors);
        Assert.Equal(0, run.FinalTranCount);
        Assert.Equal(0, db.Scalar<int>("SELECT COUNT(*) FROM sys.views WHERE name = 'vw_Ssms2'"));
    }

    [LocalDbFact]
    public void Ssms_SuccessfulRun_CommitsAndReportsEachStep()
    {
        db.Run("CREATE TABLE dbo.TSsms3 (Id INT, Nome NVARCHAR(20));");
        var request = new CreateViewRequest { ViewName = "vw_Ssms3", SelectStatement = "SELECT Id FROM dbo.TSsms3" };
        db.RunAll(Generate(request));

        request.SelectStatement = "SELECT Id, Nome FROM dbo.TSsms3";
        var run = db.RunAll(Generate(request));

        Assert.Empty(run.Errors);
        Assert.Equal(0, run.FinalTranCount);
        Assert.Contains(run.Messages, m => m.Contains("já existia: removida para ser recriada"));
        Assert.Contains(run.Messages, m => m.Contains("View dbo.vw_Ssms3 criada."));
        Assert.Equal(2, db.Scalar<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.vw_Ssms3')"));
    }

    [LocalDbFact]
    public void Ssms_TriggerWithTheNameOfAnotherTable_IsNotDropped_AndNothingChanges()
    {
        db.Run("CREATE TABLE dbo.TSsmsA (Id INT); CREATE TABLE dbo.TSsmsB (Id INT);");
        db.Run(Generate(new CreateTriggerRequest { Table = "TSsmsA", TriggerName = "trg_Ssms", OnInsert = true, Body = "SELECT 1;" }));
        var before = db.Scalar<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.trg_Ssms'))");

        var run = db.RunAll(Generate(new CreateTriggerRequest { Table = "TSsmsB", TriggerName = "trg_Ssms", OnDelete = true, Body = "SELECT 2;" }));

        Assert.Contains(run.Errors, e => e.Contains("em outra tabela"));
        Assert.Equal(0, run.FinalTranCount);
        Assert.Equal(before, db.Scalar<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.trg_Ssms'))"));
        Assert.Equal(1, db.Scalar<int>("SELECT COUNT(*) FROM sys.triggers WHERE name = 'trg_Ssms' AND parent_id = OBJECT_ID('dbo.TSsmsA')"));
    }
}
