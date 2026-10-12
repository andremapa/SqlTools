using Microsoft.Data.SqlClient;
using SqlTools.Core.Generation;
using SqlTools.Core.Requests;

namespace SqlTools.Core.Tests;

/// <summary>
/// Executa os scripts de view e trigger num banco real. O ponto mais importante: o script remove a versão antiga e cria a
/// nova, e se a criação falhar a versão antiga TEM que continuar no banco (mesmo sem a opção "transação").
/// </summary>
public class ModuleExecutionTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly SqlServerScriptGenerator _generator = new();

    private List<string> Run(ScriptRequest request, bool useTransaction = false)
    {
        var result = _generator.Generate(request, new ScriptOptions { UseTransaction = useTransaction, AddPrintCommands = true, SafeGuard = true });
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return db.Run(result.Script);
    }

    private void RunExpectingError(ScriptRequest request, string expectedMessage, bool useTransaction = false)
    {
        var ex = Assert.Throws<SqlException>(() => Run(request, useTransaction));
        Assert.Contains(expectedMessage, ex.Message);
    }

    private int Count(string sql) => db.Scalar<int>(sql);

    private string Definition(string view) => db.Scalar<string>($"SELECT OBJECT_DEFINITION(OBJECT_ID('{view}'))");

    private void Table(string name, string columns) => db.Run($"CREATE TABLE dbo.{name} ({columns});");

    // ------------------------------------------------------------------ views

    [LocalDbFact]
    public void CreateView_CreatesAndThenRecreatesTheExistingOne()
    {
        Table("VCliente", "Id INT PRIMARY KEY, Nome NVARCHAR(50), Saldo INT");

        var first = Run(new CreateViewRequest { ViewName = "vw_VCliente", SelectStatement = "SELECT Id, Nome FROM dbo.VCliente" });
        Assert.Contains("View dbo.vw_VCliente criada.", first);
        Assert.DoesNotContain(first, m => m.Contains("removida"));
        Assert.Equal(2, Count("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.vw_VCliente')"));

        // De novo, com mais uma coluna: a antiga é removida e a nova criada.
        var second = Run(new CreateViewRequest { ViewName = "vw_VCliente", SelectStatement = "SELECT Id, Nome, Saldo FROM dbo.VCliente;" });
        Assert.Contains(second, m => m.Contains("já existia: removida para ser recriada"));
        Assert.Contains("View dbo.vw_VCliente criada.", second);
        Assert.Equal(3, Count("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.vw_VCliente')"));
    }

    [LocalDbFact]
    public void CreateView_QuotesInsideTheSelectSurviveTheDynamicSql()
    {
        Table("VAspas", "Id INT, Tipo VARCHAR(10)");
        db.Run("INSERT INTO dbo.VAspas VALUES (1, 'it''s'), (2, 'x');");

        Run(new CreateViewRequest { ViewName = "vw_VAspas", SelectStatement = "SELECT Id FROM dbo.VAspas WHERE Tipo = 'it''s'" });

        Assert.Equal(1, Count("SELECT COUNT(*) FROM dbo.vw_VAspas"));
    }

    [LocalDbFact]
    public void CreateView_WhenCreationFails_TheOldViewIsKept()
    {
        Table("VFalha", "Id INT, Nome NVARCHAR(50)");
        Run(new CreateViewRequest { ViewName = "vw_VFalha", SelectStatement = "SELECT Id FROM dbo.VFalha" });
        var before = Definition("dbo.vw_VFalha");

        // Sem transação ligada: a troca em si precisa ser atômica.
        RunExpectingError(new CreateViewRequest { ViewName = "vw_VFalha", SelectStatement = "SELECT ColunaQueNaoExiste FROM dbo.VFalha" },
            "ColunaQueNaoExiste", useTransaction: false);

        Assert.Equal(before, Definition("dbo.vw_VFalha"));
        Assert.Equal(0, Count("SELECT @@TRANCOUNT"));
    }

    [LocalDbFact]
    public void CreateView_WhenCreationFails_InsideATransactionAlsoKeepsTheOldView()
    {
        Table("VFalha2", "Id INT");
        Run(new CreateViewRequest { ViewName = "vw_VFalha2", SelectStatement = "SELECT Id FROM dbo.VFalha2" });
        var before = Definition("dbo.vw_VFalha2");

        RunExpectingError(new CreateViewRequest { ViewName = "vw_VFalha2", SelectStatement = "SELECT Nada FROM dbo.VFalha2" },
            "Nada", useTransaction: true);

        Assert.Equal(before, Definition("dbo.vw_VFalha2"));
        Assert.Equal(0, Count("SELECT @@TRANCOUNT"));
    }

    [LocalDbFact]
    public void CreateView_FailsWhenANonViewHasThatName_AndWhenTheSchemaIsMissing()
    {
        Table("VTabela", "Id INT");

        RunExpectingError(new CreateViewRequest { ViewName = "VTabela", SelectStatement = "SELECT 1 AS a" }, "VTabela");
        RunExpectingError(new CreateViewRequest { Schema = "naoexiste", ViewName = "v", SelectStatement = "SELECT 1 AS a" }, "naoexiste");
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.tables WHERE name = 'VTabela'"));
    }

    [LocalDbFact]
    public void CreateView_WithSchemaBinding()
    {
        Table("VBind", "Id INT, Nome NVARCHAR(20)");

        Run(new CreateViewRequest { ViewName = "vw_VBind", SelectStatement = "SELECT Id, Nome FROM dbo.VBind", SchemaBinding = true });

        Assert.Equal(1, Count("SELECT OBJECTPROPERTY(OBJECT_ID('dbo.vw_VBind'), 'IsSchemaBound')"));
    }

    [LocalDbFact]
    public void AddViewColumns_RecreatesTheViewWithTheNewColumns_FromTheRealDefinition()
    {
        Table("VAdd", "Id INT PRIMARY KEY, Nome NVARCHAR(50), Email VARCHAR(80), Saldo DECIMAL(10,2)");
        db.Run("INSERT INTO dbo.VAdd VALUES (1, 'Ana', 'ana@x.com', 10.50);");
        db.Run("""
            CREATE VIEW dbo.vw_VAdd
            AS
            SELECT a.Id, a.Nome -- só o básico
            FROM dbo.VAdd a
            WHERE a.Saldo >= 0
            """);

        var request = new AddViewColumnsRequest
        {
            ViewName = "vw_VAdd",
            Definition = Definition("dbo.vw_VAdd"), // exatamente o que o banco guarda
            NewColumns = ["a.Email", "a.Saldo * 2 AS Dobro"],
            ExistingColumns = ["Id", "Nome"],
        };

        Run(request);
        Run(request); // pode ser executado de novo

        var columns = db.Run("SELECT 1;"); // só para garantir a conexão; as colunas vêm abaixo
        Assert.Equal(["Id", "Nome", "Email", "Dobro"],
            Enumerable.Range(1, 4).Select(i => db.Scalar<string>($"SELECT COL_NAME(OBJECT_ID('dbo.vw_VAdd'), {i})")));
        Assert.Equal(21.00m, db.Scalar<decimal>("SELECT Dobro FROM dbo.vw_VAdd WHERE Id = 1"));
        Assert.Contains("WHERE a.Saldo >= 0", Definition("dbo.vw_VAdd")); // o resto da consulta foi preservado
    }

    [LocalDbFact]
    public void AddViewColumns_KeepsSchemaBinding()
    {
        Table("VAddBind", "Id INT, Nome NVARCHAR(20), Extra INT");
        db.Run("CREATE VIEW dbo.vw_VAddBind WITH SCHEMABINDING AS SELECT Id, Nome FROM dbo.VAddBind");

        Run(new AddViewColumnsRequest { ViewName = "vw_VAddBind", Definition = Definition("dbo.vw_VAddBind"), NewColumns = ["Extra"] });

        Assert.Equal(1, Count("SELECT OBJECTPROPERTY(OBJECT_ID('dbo.vw_VAddBind'), 'IsSchemaBound')"));
        Assert.Equal(3, Count("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.vw_VAddBind')"));
    }

    [LocalDbFact]
    public void AddViewColumns_WithABadColumn_KeepsTheOldViewUntouched()
    {
        Table("VAddFalha", "Id INT, Nome NVARCHAR(20)");
        db.Run("CREATE VIEW dbo.vw_VAddFalha AS SELECT Id FROM dbo.VAddFalha");
        var before = Definition("dbo.vw_VAddFalha");

        RunExpectingError(
            new AddViewColumnsRequest { ViewName = "vw_VAddFalha", Definition = before, NewColumns = ["ColunaInexistente"] },
            "ColunaInexistente");

        Assert.Equal(before, Definition("dbo.vw_VAddFalha"));
    }

    [LocalDbFact]
    public void AddViewColumns_WhenTheViewDoesNotExist_WarnsAndCreatesIt()
    {
        var messages = Run(new AddViewColumnsRequest
        {
            ViewName = "vw_NaoExiste", Definition = "CREATE VIEW dbo.vw_NaoExiste AS SELECT 1 AS a", NewColumns = ["2 AS b"],
        });

        Assert.Contains(messages, m => m.Contains("não existe neste banco e será criada"));
        Assert.Equal(2, Count("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.vw_NaoExiste')"));
    }

    [LocalDbFact]
    public void DropView_IsIdempotent()
    {
        Table("VDrop", "Id INT");
        Run(new CreateViewRequest { ViewName = "vw_VDrop", SelectStatement = "SELECT Id FROM dbo.VDrop" });

        Run(new DropViewRequest { ViewName = "vw_VDrop" });
        var again = Run(new DropViewRequest { ViewName = "vw_VDrop" });

        Assert.Contains("View dbo.vw_VDrop não existe. Nenhuma alteração realizada.", again);
        RunExpectingError(new DropViewRequest { ViewName = "VDrop" }, "não é uma view");
    }

    // ------------------------------------------------------------------ triggers

    private void AuditTables(string suffix)
    {
        Table($"TCliente{suffix}", "Id INT PRIMARY KEY, Nome NVARCHAR(50), Email VARCHAR(80) NULL");
        Table($"TLog{suffix}", "Id INT IDENTITY, Origem VARCHAR(30), Detalhe NVARCHAR(200) NULL");
    }

    [LocalDbFact]
    public void CreateTrigger_CreatesItAndItFires_ThenRecreatesWithNewBody()
    {
        AuditTables("A");
        var request = new CreateTriggerRequest
        {
            Table = "TClienteA",
            TriggerName = "trg_TClienteA",
            OnInsert = true,
            Body = "INSERT INTO dbo.TLogA (Origem, Detalhe) SELECT 'v1', Nome FROM inserted;",
        };

        var first = Run(request);
        Assert.Contains("Trigger dbo.trg_TClienteA criada em dbo.TClienteA.", first);
        db.Run("INSERT INTO dbo.TClienteA (Id, Nome) VALUES (1, 'Ana');");
        Assert.Equal("v1", db.Scalar<string>("SELECT Origem FROM dbo.TLogA WHERE Id = 1"));

        request.Body = "INSERT INTO dbo.TLogA (Origem, Detalhe) SELECT 'v2', Nome FROM inserted;";
        var second = Run(request);
        Assert.Contains(second, m => m.Contains("já existia: removida para ser recriada"));
        db.Run("INSERT INTO dbo.TClienteA (Id, Nome) VALUES (2, 'Bia');");

        Assert.Equal("v2", db.Scalar<string>("SELECT Origem FROM dbo.TLogA WHERE Detalhe = N'Bia'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.triggers WHERE name = 'trg_TClienteA'"));
    }

    [LocalDbFact]
    public void CreateTrigger_SupportsSeveralEventsAndInsteadOf()
    {
        AuditTables("B");

        Run(new CreateTriggerRequest
        {
            Table = "TClienteB", TriggerName = "trg_TClienteB", OnInsert = true, OnUpdate = true, OnDelete = true,
            Body = "INSERT INTO dbo.TLogB (Origem) VALUES ('evento');",
        });

        db.Run("INSERT INTO dbo.TClienteB (Id, Nome) VALUES (1, 'x'); UPDATE dbo.TClienteB SET Nome = 'y'; DELETE dbo.TClienteB;");
        Assert.Equal(3, Count("SELECT COUNT(*) FROM dbo.TLogB"));

        // INSTEAD OF numa view: o INSERT passa pela trigger.
        db.Run("CREATE VIEW dbo.vw_TClienteB AS SELECT Id, Nome FROM dbo.TClienteB");
        Run(new CreateTriggerRequest
        {
            Table = "vw_TClienteB", TriggerName = "trg_vw_TClienteB", Timing = TriggerTiming.InsteadOf, OnInsert = true,
            Body = "INSERT INTO dbo.TClienteB (Id, Nome) SELECT Id, UPPER(Nome) FROM inserted;",
        });
        db.Run("INSERT INTO dbo.vw_TClienteB (Id, Nome) VALUES (9, 'minusculo');");
        Assert.Equal("MINUSCULO", db.Scalar<string>("SELECT Nome FROM dbo.TClienteB WHERE Id = 9"));
    }

    [LocalDbFact]
    public void CreateTrigger_WhenCreationFails_TheOldTriggerKeepsWorking()
    {
        AuditTables("C");
        Run(new CreateTriggerRequest
        {
            Table = "TClienteC", TriggerName = "trg_TClienteC", OnInsert = true,
            Body = "INSERT INTO dbo.TLogC (Origem) VALUES ('original');",
        });
        var before = Definition("dbo.trg_TClienteC");

        // Sem transação ligada, e o corpo novo usa uma coluna que não existe (tabelas inexistentes são aceitas pelo SQL Server).
        RunExpectingError(new CreateTriggerRequest
        {
            Table = "TClienteC", TriggerName = "trg_TClienteC", OnInsert = true,
            Body = "INSERT INTO dbo.TLogC (ColunaFantasma) VALUES (1);",
        }, "ColunaFantasma", useTransaction: false);

        Assert.Equal(before, Definition("dbo.trg_TClienteC"));
        db.Run("INSERT INTO dbo.TClienteC (Id, Nome) VALUES (1, 'ok');");
        Assert.Equal("original", db.Scalar<string>("SELECT Origem FROM dbo.TLogC"));
    }

    [LocalDbFact]
    public void CreateTrigger_Guards()
    {
        AuditTables("D");
        Run(new CreateTriggerRequest { Table = "TClienteD", TriggerName = "trg_D", OnInsert = true, Body = "SELECT 1;" });

        // alvo inexistente
        RunExpectingError(new CreateTriggerRequest { Table = "NaoExiste", TriggerName = "trg_X", OnInsert = true, Body = "SELECT 1;" },
            "NaoExiste");
        // mesmo nome em OUTRA tabela: não pode derrubar a trigger alheia
        RunExpectingError(new CreateTriggerRequest { Table = "TLogD", TriggerName = "trg_D", OnInsert = true, Body = "SELECT 1;" },
            "em outra tabela");
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.triggers WHERE name = 'trg_D' AND parent_id = OBJECT_ID('dbo.TClienteD')"));
        // nome de um objeto que não é trigger
        RunExpectingError(new CreateTriggerRequest { Table = "TClienteD", TriggerName = "TLogD", OnInsert = true, Body = "SELECT 1;" },
            "TLogD");
    }

    [LocalDbFact]
    public void AlterTrigger_AddsAColumnByRecreatingFromTheRealDefinition()
    {
        AuditTables("E");
        Run(new CreateTriggerRequest
        {
            Table = "TClienteE", TriggerName = "trg_E", OnInsert = true,
            Body = "INSERT INTO dbo.TLogE (Origem, Detalhe) SELECT 'auditoria', i.Nome FROM inserted i;",
        });

        // Usuário carrega a definição do banco e acrescenta o Email ao detalhe.
        var edited = Definition("dbo.trg_E").Replace("i.Nome FROM inserted", "i.Nome + ' <' + ISNULL(i.Email, '-') + '>' FROM inserted");
        var request = new AlterTriggerRequest { TriggerName = "trg_E", Definition = edited };

        Run(request);
        Run(request); // idempotente

        db.Run("INSERT INTO dbo.TClienteE (Id, Nome, Email) VALUES (1, 'Ana', 'ana@x.com');");
        Assert.Equal("Ana <ana@x.com>", db.Scalar<string>("SELECT Detalhe FROM dbo.TLogE"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.triggers WHERE name = 'trg_E'"));
    }

    [LocalDbFact]
    public void AlterTrigger_WithABrokenDefinition_KeepsTheOldTrigger()
    {
        AuditTables("F");
        Run(new CreateTriggerRequest { Table = "TClienteF", TriggerName = "trg_F", OnInsert = true, Body = "INSERT INTO dbo.TLogF (Origem) VALUES ('ok');" });
        var before = Definition("dbo.trg_F");

        RunExpectingError(
            new AlterTriggerRequest { TriggerName = "trg_F", Definition = before.Replace("(Origem)", "(ColunaFantasma)") },
            "ColunaFantasma");

        Assert.Equal(before, Definition("dbo.trg_F"));
    }

    [LocalDbFact]
    public void AlterTrigger_FailsWhenItsTableDoesNotExist()
    {
        RunExpectingError(
            new AlterTriggerRequest { TriggerName = "trg_Z", Definition = "CREATE TRIGGER dbo.trg_Z ON dbo.TabelaFantasma AFTER INSERT AS BEGIN SELECT 1 END" },
            "TabelaFantasma");
    }

    [LocalDbFact]
    public void DropTrigger_IsIdempotent()
    {
        AuditTables("G");
        Run(new CreateTriggerRequest { Table = "TClienteG", TriggerName = "trg_G", OnInsert = true, Body = "SELECT 1;" });

        Run(new DropTriggerRequest { TriggerName = "trg_G" });
        var again = Run(new DropTriggerRequest { TriggerName = "trg_G" });

        Assert.Contains("Trigger dbo.trg_G não existe. Nenhuma alteração realizada.", again);
    }

    [LocalDbFact]
    public void Scripts_WorkInsideTheOptionalTransactionToo()
    {
        AuditTables("H");
        var request = new CreateTriggerRequest { Table = "TClienteH", TriggerName = "trg_H", OnInsert = true, Body = "SELECT 1;" };

        Run(request, useTransaction: true);
        Run(request, useTransaction: true);

        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.triggers WHERE name = 'trg_H'"));
        Assert.Equal(0, Count("SELECT @@TRANCOUNT"));
    }
}
