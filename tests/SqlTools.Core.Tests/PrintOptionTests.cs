using Microsoft.Data.SqlClient;
using SqlTools.Core.Generation;
using SqlTools.Core.Model;
using SqlTools.Core.Requests;

namespace SqlTools.Core.Tests;

/// <summary>A opção "Adicionar comandos PRINT": desligada (padrão) o script não tem nenhum PRINT; ligada, informa o que fez.</summary>
public class PrintOptionTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly SqlServerScriptGenerator _generator = new();

    private static readonly ColumnDefinition Id = new() { Name = "Id", IsNullable = false };

    /// <summary>Uma instância válida de CADA tipo de operação.</summary>
    public static IEnumerable<object[]> EveryOperation() =>
    [
        [new CreateTableRequest { Schema = "vendas", Table = "T", Columns = [Id, new() { Name = "Ativo", DataType = "BIT", IsNullable = false, DefaultExpression = "1" }], PrimaryKeyColumns = ["Id"] }],
        [new DropTableRequest { Table = "T" }],
        [new AddColumnRequest { Table = "T", Column = new() { Name = "Nome", DataType = "NVARCHAR", Length = 20 } }],
        [new AlterColumnRequest { Table = "T", Column = new() { Name = "Nome", DataType = "NVARCHAR", Length = 50, IsNullable = false } }],
        [new RenameColumnRequest { Table = "T", ColumnName = "Nome", NewName = "Titulo" }],
        [new DropColumnRequest { Table = "T", ColumnName = "Nome" }],
        [new DropColumnRequest { Table = "T", ColumnName = "Nome", DropDefaultConstraint = false }],
        [new AddPrimaryKeyRequest { Table = "T", Columns = ["Id"] }],
        [new AddForeignKeyRequest { Table = "T", Columns = ["PaiId"], ReferencedTable = "Pai", ReferencedColumns = ["Id"] }],
        [new AddUniqueRequest { Table = "T", Columns = ["Email"] }],
        [new AddCheckRequest { Table = "T", ConstraintName = "CK_T", Expression = "[Id] > 0" }],
        [new AddDefaultRequest { Table = "T", ColumnName = "Ativo", Expression = "1" }],
        [new DropConstraintRequest { Table = "T", ConstraintName = "CK_T" }],
        [new CreateIndexRequest { Table = "T", Columns = [new("Nome")] }],
        [new DropIndexRequest { Table = "T", IndexName = "IX_T" }],
        [new CreateViewRequest { ViewName = "vw_T", SelectStatement = "SELECT Id FROM dbo.T" }],
        [new AddViewColumnsRequest { ViewName = "vw_T", Definition = "CREATE VIEW dbo.vw_T AS SELECT Id FROM dbo.T", NewColumns = ["Nome"] }],
        [new DropViewRequest { ViewName = "vw_T" }],
        [new CreateTriggerRequest { Table = "T", TriggerName = "trg_T", OnInsert = true, Body = "SELECT 1;" }],
        [new AlterTriggerRequest { TriggerName = "trg_T", Definition = "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS SELECT 1" }],
        [new DropTriggerRequest { TriggerName = "trg_T" }],
    ];

    private string Script(ScriptRequest request, bool prints, bool transaction = false, bool safeGuard = false)
    {
        var result = _generator.Generate(request, new ScriptOptions { AddPrintCommands = prints, UseTransaction = transaction, SafeGuard = safeGuard });
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return result.Script;
    }

    [Fact]
    public void Default_IsOff()
    {
        Assert.False(new ScriptOptions().AddPrintCommands);
    }

    [Theory]
    [MemberData(nameof(EveryOperation))]
    public void WithoutThePrintOption_NoOperationEmitsAnyPrint(ScriptRequest request)
    {
        foreach (var transaction in new[] { false, true })
            Assert.DoesNotContain("PRINT", Script(request, prints: false, transaction), StringComparison.OrdinalIgnoreCase);

        // O padrão das opções (sem configurar nada) também não tem PRINT.
        Assert.DoesNotContain("PRINT", _generator.Generate(request, new ScriptOptions()).Script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(EveryOperation))]
    public void WithThePrintOption_EveryOperationReportsWhatItDid(ScriptRequest request)
    {
        Assert.Contains("PRINT N'", Script(request, prints: true));
    }

    [Theory]
    [MemberData(nameof(EveryOperation))]
    public void ThePrintOptionOnlyAddsPrintLines(ScriptRequest request)
    {
        // Ligar a opção só pode acrescentar PRINT: sem eles, o script é o mesmo de antes, linha a linha, exceto pela
        // forma das verificações "já existe" (que ganham a mensagem em vez de IF NOT). Os comandos de alteração são idênticos.
        var withPrint = Script(request, prints: true).Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("PRINT")).ToList();
        var without = Script(request, prints: false).Replace("\r\n", "\n").Split('\n').ToList();

        foreach (var statement in new[] { "CREATE TABLE", "DROP TABLE", "ALTER TABLE", "CREATE VIEW", "DROP VIEW", "CREATE TRIGGER", "DROP TRIGGER", "EXEC sp_rename", "INDEX" })
            Assert.Equal(withPrint.Count(l => l.Contains(statement)), without.Count(l => l.Contains(statement)));
    }

    [Fact]
    public void ErrorsOfTheSafeGuard_DoNotDependOnPrints()
    {
        var script = Script(new AddColumnRequest { Table = "T", Column = new() { Name = "Nome", DataType = "NVARCHAR", Length = 20 } }, prints: false, safeGuard: true);

        Assert.Contains("RAISERROR(N'Tabela dbo.T não existe.', 16, 1);", script);
    }

    [Fact]
    public void SchemaCreation_AndDefaultConstraintRemoval_AreSilentWithoutPrints()
    {
        var create = Script(new CreateTableRequest { Schema = "vendas", Table = "T", Columns = [Id] }, prints: false);
        var drop = Script(new DropColumnRequest { Table = "T", ColumnName = "Nome" }, prints: false);

        Assert.Contains("EXEC(N'CREATE SCHEMA [vendas]');", create);
        Assert.DoesNotContain("PRINT", create);
        Assert.Contains("EXEC sp_executesql @Sql;", drop);
        Assert.DoesNotContain("PRINT", drop);
    }

    [Fact]
    public void SkipWithoutPrint_KeepsTheOrderOfTheChecks()
    {
        // CreateTable: erro de schema, "tabela existe" (skip), "objeto com esse nome" (erro), nomes de constraint (erro).
        // O skip tem que ser avaliado ANTES dos erros seguintes, senão "a tabela existe" viraria erro.
        var script = Script(new CreateTableRequest { Table = "T", Columns = [Id], PrimaryKeyColumns = ["Id"] }, prints: false, safeGuard: true).Replace("\r\n", "\n");

        var schema = script.IndexOf("SCHEMA_ID", StringComparison.Ordinal);
        var skip = script.IndexOf("ELSE IF OBJECT_ID(N'[dbo].[T]', N'U') IS NULL", StringComparison.Ordinal);
        var otherObject = script.IndexOf("IF OBJECT_ID(N'[dbo].[T]') IS NOT NULL", StringComparison.Ordinal);

        Assert.True(schema >= 0 && skip > schema && otherObject > skip, "ordem: schema, tabela existente, outro objeto");
    }

    // ------------------------------------------------------------------ execução real

    private List<string> Run(ScriptRequest request, bool prints) => db.Run(Script(request, prints));

    /// <summary>Mensagens geradas pelo script (descarta o aviso que o próprio sp_rename do SQL Server imprime).</summary>
    private List<string> OurMessages(ScriptRequest request, bool prints) =>
        Run(request, prints).Where(m => !m.StartsWith("Caution: Changing any part of an object name")).ToList();

    [LocalDbFact]
    public void WithoutPrints_ScriptsRunSilentlyAndStayIdempotent()
    {
        ScriptRequest[] requests =
        [
            new CreateTableRequest { Schema = "pr", Table = "Silencio", Columns = [Id, new() { Name = "Nome", DataType = "NVARCHAR", Length = 30 }], PrimaryKeyColumns = ["Id"] },
            new AddColumnRequest { Schema = "pr", Table = "Silencio", Column = new() { Name = "Email", DataType = "VARCHAR", Length = 80 } },
            new RenameColumnRequest { Schema = "pr", Table = "Silencio", ColumnName = "Nome", NewName = "Titulo" },
            new AddUniqueRequest { Schema = "pr", Table = "Silencio", Columns = ["Email"] },
            new AddCheckRequest { Schema = "pr", Table = "Silencio", ConstraintName = "CK_Silencio", Expression = "[Id] > 0" },
            new AddDefaultRequest { Schema = "pr", Table = "Silencio", ColumnName = "Titulo", Expression = "N'x'" },
            new CreateIndexRequest { Schema = "pr", Table = "Silencio", Columns = [new("Titulo")] },
        ];

        db.Run("CREATE SCHEMA pr;");

        foreach (var request in requests)
        {
            // 1ª execução faz a alteração; a 2ª encontra tudo pronto e não faz nada. Nas duas: nenhuma mensagem e nenhum erro.
            Assert.Empty(OurMessages(request, prints: false));
            Assert.Empty(OurMessages(request, prints: false));
        }

        Assert.Equal(3, db.Scalar<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('pr.Silencio')")); // Id, Titulo, Email
        Assert.Equal(1, db.Scalar<int>("SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Silencio_Titulo'"));

        // Idempotência também no modo de remoção.
        ScriptRequest[] drops =
        [
            new DropConstraintRequest { Schema = "pr", Table = "Silencio", ConstraintName = "CK_Silencio" },
            new DropIndexRequest { Schema = "pr", Table = "Silencio", IndexName = "IX_Silencio_Titulo" },
            new DropColumnRequest { Schema = "pr", Table = "Silencio", ColumnName = "Titulo" },
            new DropTableRequest { Schema = "pr", Table = "Silencio" },
        ];
        foreach (var request in drops)
        {
            Assert.Empty(Run(request, prints: false));
            Assert.Empty(Run(request, prints: false));
        }

        Assert.Equal(0, db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Silencio'"));
    }

    [LocalDbFact]
    public void WithoutPrints_CreatingTheSchemaAndRemovingTheDefaultAreSilentToo()
    {
        Assert.Empty(Run(new CreateTableRequest
        {
            Schema = "novoschema", Table = "T1",
            Columns = [Id, new() { Name = "Ativo", DataType = "BIT", IsNullable = false, DefaultExpression = "1" }],
        }, prints: false));
        Assert.Equal(1, db.Scalar<int>("SELECT COUNT(*) FROM sys.schemas WHERE name = 'novoschema'"));

        Assert.Empty(Run(new DropColumnRequest { Schema = "novoschema", Table = "T1", ColumnName = "Ativo" }, prints: false));
        Assert.Equal(0, db.Scalar<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('novoschema.T1') AND name = 'Ativo'"));
    }

    [LocalDbFact]
    public void WithoutPrints_AMissingTableStillMakesTheScriptFail()
    {
        var ex = Assert.Throws<SqlException>(() =>
            Run(new AddColumnRequest { Table = "TabelaQueNaoExiste", Column = new() { Name = "X", DataType = "INT" } }, prints: false));

        Assert.Contains("TabelaQueNaoExiste", ex.Message);
    }

    [LocalDbFact]
    public void WithoutPrints_SkipOrderIsRespectedAtRuntime()
    {
        // Se o skip "a tabela já existe" não vier antes do erro "já existe um objeto com esse nome", a 2ª execução falharia.
        var request = new CreateTableRequest { Table = "Ordem", Columns = [Id], PrimaryKeyColumns = ["Id"] };

        Assert.Empty(Run(request, prints: false));
        var second = Record.Exception(() => Run(request, prints: false));

        Assert.Null(second);
    }

    [LocalDbFact]
    public void WithoutPrints_ViewAndTriggerScriptsAreSilent_AndTheSafeGuardStillProtectsTheOldVersion()
    {
        db.Run("CREATE TABLE dbo.TPrint (Id INT, Nome NVARCHAR(20));");

        var view = new CreateViewRequest { ViewName = "vw_TPrint", SelectStatement = "SELECT Id FROM dbo.TPrint" };
        Assert.Empty(Run(view, prints: false));
        view.SelectStatement = "SELECT Id, Nome FROM dbo.TPrint";
        Assert.Empty(Run(view, prints: false));
        Assert.Equal(2, db.Scalar<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.vw_TPrint')"));

        var trigger = new CreateTriggerRequest { Table = "TPrint", TriggerName = "trg_TPrint", OnInsert = true, Body = "SELECT 1;" };
        Assert.Empty(Run(trigger, prints: false));
        Assert.Empty(Run(trigger, prints: false));
        Assert.Equal(1, db.Scalar<int>("SELECT COUNT(*) FROM sys.triggers WHERE name = 'trg_TPrint'"));

        // Uma falha continua desfazendo o DROP (estilo SSMS: os lotes seguintes ainda rodam).
        var before = db.Scalar<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.vw_TPrint'))");
        var broken = db.RunAll(Script(new CreateViewRequest { ViewName = "vw_TPrint", SelectStatement = "SELECT Fantasma FROM dbo.TPrint" }, prints: false, safeGuard: true));

        Assert.NotEmpty(broken.Errors);
        Assert.Equal(0, broken.FinalTranCount);
        Assert.Equal(before, db.Scalar<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.vw_TPrint'))"));
    }

    [LocalDbFact]
    public void WithPrints_TheSameScriptsReportEachStep()
    {
        var request = new CreateTableRequest { Table = "ComPrint", Columns = [Id], PrimaryKeyColumns = ["Id"] };

        var first = Run(request, prints: true);
        var second = Run(request, prints: true);

        Assert.Contains("Tabela dbo.ComPrint criada.", first);
        Assert.Contains("Tabela dbo.ComPrint já existe. Nenhuma alteração realizada.", second);
    }
}
