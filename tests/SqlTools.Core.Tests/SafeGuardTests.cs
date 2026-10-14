using Microsoft.Data.SqlClient;
using SqlTools.Core.Generation;
using SqlTools.Core.Model;
using SqlTools.Core.Requests;

namespace SqlTools.Core.Tests;

/// <summary>
/// "Safe guard": o padrão é o script simples e direto (só a verificação de "já existe", que dá a idempotência);
/// as validações extras (RAISERROR) só entram com a opção ligada.
/// </summary>
public class SafeGuardTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly SqlServerScriptGenerator _generator = new();

    private static readonly ColumnDefinition Id = new() { Name = "Id", IsNullable = false };

    private string Script(ScriptRequest request, bool safeGuard = false, bool prints = false)
    {
        var result = _generator.Generate(request, new ScriptOptions { SafeGuard = safeGuard, AddPrintCommands = prints });
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return result.Script.Replace("\r\n", "\n").TrimEnd('\n');
    }

    // ------------------------------------------------------------------ cobertura: todas as operações

    [Fact]
    public void Default_IsOff() => Assert.False(new ScriptOptions().SafeGuard);

    [Theory]
    [MemberData(nameof(PrintOptionTests.EveryOperation), MemberType = typeof(PrintOptionTests))]
    public void ByDefault_NoOperationHasExtraValidations(ScriptRequest request)
    {
        var script = Script(request);

        Assert.DoesNotContain("RAISERROR", script);
        Assert.DoesNotContain("@HasNulls", script);
        Assert.DoesNotContain("TRANCOUNT", script);
    }

    [Theory]
    [MemberData(nameof(PrintOptionTests.EveryOperation), MemberType = typeof(PrintOptionTests))]
    public void WithSafeGuard_EveryOperationHasItsExtraValidations(ScriptRequest request)
    {
        Assert.Contains("RAISERROR", Script(request, safeGuard: true));
    }

    [Theory]
    [MemberData(nameof(PrintOptionTests.EveryOperation), MemberType = typeof(PrintOptionTests))]
    public void TheSafeGuardOnlyAddsChecks_TheChangingStatementsAreTheSame(ScriptRequest request)
    {
        var plain = Script(request).Split('\n');
        var guarded = Script(request, safeGuard: true).Split('\n');

        foreach (var statement in new[] { "CREATE TABLE", "DROP TABLE", "ALTER TABLE", "CREATE VIEW", "DROP VIEW", "CREATE TRIGGER", "DROP TRIGGER", "EXEC sp_rename", "INDEX" })
            Assert.Equal(guarded.Count(l => l.Contains(statement)), plain.Count(l => l.Contains(statement)));
    }

    [Theory]
    [MemberData(nameof(PrintOptionTests.EveryOperation), MemberType = typeof(PrintOptionTests))]
    public void TheBasicScriptIsNeverLongerThanTheGuardedOne(ScriptRequest request)
    {
        Assert.True(Script(request).Length < Script(request, safeGuard: true).Length);
    }

    // ------------------------------------------------------------------ a forma "simples e direta"

    [Fact]
    public void CreateTable_JustChecksWhetherTheTableAlreadyExists()
    {
        var script = Script(new CreateTableRequest { Table = "Cliente", Columns = [Id] });

        Assert.Equal("""
            IF OBJECT_ID(N'[dbo].[Cliente]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Cliente]
                (
                    [Id] INT NOT NULL
                );
            END
            GO
            """.Replace("\r\n", "\n"), script);
    }

    [Fact]
    public void CreateTable_WithTheSafeGuard_AddsTheExtraChecksInOrder()
    {
        var script = Script(new CreateTableRequest { Table = "Cliente", Columns = [Id], PrimaryKeyColumns = ["Id"] }, safeGuard: true);

        Assert.Contains("RAISERROR(N'Schema dbo não existe.', 16, 1);", script);
        Assert.Contains("RAISERROR(N'Já existe um objeto que não é tabela chamado dbo.Cliente.', 16, 1);", script);
        Assert.Contains("RAISERROR(N'Já existe outro objeto chamado dbo.PK_Cliente no banco.', 16, 1);", script);
    }

    [Fact]
    public void DropTable_JustChecksWhetherTheTableExists()
    {
        Assert.Equal("""
            IF OBJECT_ID(N'[dbo].[T]', N'U') IS NOT NULL
            BEGIN
                DROP TABLE [dbo].[T];
            END
            GO
            """.Replace("\r\n", "\n"), Script(new DropTableRequest { Table = "T" }));
    }

    [Fact]
    public void AddColumn_JustChecksWhetherTheColumnAlreadyExists()
    {
        Assert.Equal("""
            IF NOT EXISTS (SELECT 1 FROM Syscolumns WHERE id = OBJECT_ID(N'[dbo].[T]') AND name = N'Nome')
            BEGIN
                ALTER TABLE [dbo].[T] ADD [Nome] NVARCHAR(20) NULL;
            END
            GO
            """.Replace("\r\n", "\n"), Script(new AddColumnRequest { Table = "T", Column = new() { Name = "Nome", DataType = "NVARCHAR", Length = 20 } }));
    }

    [Fact]
    public void AlterColumn_HasNoChecksAtAll_JustTheStatement()
    {
        var script = Script(new AlterColumnRequest { Table = "T", Column = new() { Name = "Nome", DataType = "NVARCHAR", Length = 50, IsNullable = false } });

        Assert.Equal("ALTER TABLE [dbo].[T] ALTER COLUMN [Nome] NVARCHAR(50) NOT NULL;\nGO", script);
    }

    [Fact]
    public void AlterColumn_WithTheSafeGuard_ChecksTheColumnAndTheNulls()
    {
        var script = Script(new AlterColumnRequest { Table = "T", Column = new() { Name = "Nome", DataType = "NVARCHAR", Length = 50, IsNullable = false } }, safeGuard: true);

        Assert.Contains("DECLARE @HasNulls BIT = 0;", script);
        Assert.Contains("possui valores NULL", script);
    }

    [Fact]
    public void AddPrimaryKey_IsAPlainStatement_AndOnlyUsesDynamicSqlWithTheSafeGuard()
    {
        var request = new AddPrimaryKeyRequest { Table = "T", Columns = ["Id"] };

        Assert.DoesNotContain("sp_executesql", Script(request));
        Assert.Contains("ALTER TABLE [dbo].[T] ADD CONSTRAINT [PK_T] PRIMARY KEY CLUSTERED ([Id]);", Script(request));
        Assert.Contains("sp_executesql", Script(request, safeGuard: true));
    }

    [Fact]
    public void RenameColumn_SkipsOnlyWhenItWasAlreadyRenamed()
    {
        var script = Script(new RenameColumnRequest { Table = "T", ColumnName = "Nome", NewName = "Titulo" });

        Assert.Equal("""
            IF EXISTS (SELECT 1 FROM Syscolumns WHERE id = OBJECT_ID(N'[dbo].[T]') AND name = N'Nome') OR NOT EXISTS (SELECT 1 FROM Syscolumns WHERE id = OBJECT_ID(N'[dbo].[T]') AND name = N'Titulo')
            BEGIN
                EXEC sp_rename N'[dbo].[T].[Nome]', N'Titulo', N'COLUMN';
            END
            GO
            """.Replace("\r\n", "\n"), script);
    }

    [Fact]
    public void WithPrints_TheBasicScriptKeepsTheSimpleChain()
    {
        var script = Script(new CreateTableRequest { Table = "Cliente", Columns = [Id] }, prints: true);

        Assert.StartsWith("IF OBJECT_ID(N'[dbo].[Cliente]', N'U') IS NOT NULL\n    PRINT N'Tabela dbo.Cliente já existe. Nenhuma alteração realizada.';\nELSE\nBEGIN", script);
        Assert.DoesNotContain("RAISERROR", script);
    }

    [Fact]
    public void View_DefaultIsExactlyDropIfExistsGoCreateGo()
    {
        var script = Script(new CreateViewRequest { ViewName = "vw_X", SelectStatement = "SELECT 1 AS a" });

        Assert.Equal("""
            SET QUOTED_IDENTIFIER ON;
            SET ANSI_NULLS ON;

            IF OBJECT_ID(N'[dbo].[vw_X]', N'V') IS NOT NULL
            BEGIN
                DROP VIEW [dbo].[vw_X];
            END
            GO

            CREATE VIEW [dbo].[vw_X]
            AS
            SELECT 1 AS a
            GO
            """.Replace("\r\n", "\n"), script);
    }

    [Fact]
    public void View_WithTheSafeGuard_ProtectsTheSwapWithATransaction()
    {
        var script = Script(new CreateViewRequest { ViewName = "vw_X", SelectStatement = "SELECT 1 AS a" }, safeGuard: true);

        Assert.Contains("BEGIN TRANSACTION;", script);
        Assert.Contains("ROLLBACK TRANSACTION", script);
        Assert.Equal(3, script.Split('\n').Count(l => l == "GO"));
    }

    [Fact]
    public void Trigger_DefaultHasNoTransactionAndNoWarnings()
    {
        var script = Script(new CreateTriggerRequest { Table = "T", TriggerName = "trg_T", OnInsert = true, Body = "SELECT 1;" });

        Assert.DoesNotContain("TRANSACTION", script);
        Assert.DoesNotContain("RAISERROR", script);
        Assert.Equal(2, script.Split('\n').Count(l => l == "GO"));
        Assert.Contains("DROP TRIGGER [dbo].[trg_T];", script);
    }

    [Fact]
    public void WithPrints_AViewScriptStillEndsWithItsMessageInASeparateBatch()
    {
        var script = Script(new CreateViewRequest { ViewName = "vw_X", SelectStatement = "SELECT 1 AS a" }, prints: true);

        Assert.EndsWith("SELECT 1 AS a\nGO\n\nPRINT N'View dbo.vw_X criada.';\nGO", script);
    }

    // ------------------------------------------------------------------ execução real

    private List<string> Run(ScriptRequest request, bool safeGuard = false)
    {
        var result = _generator.Generate(request, new ScriptOptions { SafeGuard = safeGuard });
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return db.Run(result.Script);
    }

    private ScriptRequest Table(string name, string columns = "Id INT NOT NULL, Nome NVARCHAR(30) NULL")
    {
        db.Run($"CREATE TABLE dbo.{name} ({columns});");
        return null!;
    }

    [LocalDbFact]
    public void Basic_AlterColumnRunsAsAPlainStatement()
    {
        Table("SgAlter", "Nome VARCHAR(10) NULL");

        Run(new AlterColumnRequest { Table = "SgAlter", Column = new() { Name = "Nome", DataType = "NVARCHAR", Length = 40, IsNullable = true } });

        Assert.Equal(80, db.Scalar<short>("SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SgAlter') AND name = 'Nome'"));
    }

    [LocalDbFact]
    public void Basic_AMissingTable_IsReportedByTheSqlServerItself()
    {
        var ex = Assert.Throws<SqlException>(() =>
            Run(new AddColumnRequest { Table = "SgNaoExiste", Column = new() { Name = "X", DataType = "INT" } }));

        Assert.Contains("SgNaoExiste", ex.Message);
        Assert.DoesNotContain("não existe", ex.Message); // não é a mensagem do Safe guard
    }

    [LocalDbFact]
    public void Basic_APrimaryKeyOnANullableColumn_FailsWithTheSqlServerMessage()
    {
        db.Run("CREATE TABLE dbo.SgPk (Codigo INT NULL);");

        var ex = Assert.Throws<SqlException>(() => Run(new AddPrimaryKeyRequest { Table = "SgPk", Columns = ["Codigo"] }));

        Assert.Contains("nullable", ex.Message);
        Assert.DoesNotContain("aceita NULL", ex.Message);
    }

    [LocalDbFact]
    public void WithTheSafeGuard_ThatSamePrimaryKeyGivesTheFriendlyMessage()
    {
        db.Run("CREATE TABLE dbo.SgPk2 (Codigo INT NULL);");

        var ex = Assert.Throws<SqlException>(() => Run(new AddPrimaryKeyRequest { Table = "SgPk2", Columns = ["Codigo"] }, safeGuard: true));

        Assert.Contains("aceita NULL", ex.Message);
    }

    [LocalDbFact]
    public void Basic_CreateTableOverAnotherObject_FailsWithTheSqlServerMessage()
    {
        db.Run("CREATE TABLE dbo.SgBase (Id INT);\nGO\nCREATE VIEW dbo.SgNome AS SELECT Id FROM dbo.SgBase;");

        var ex = Assert.Throws<SqlException>(() => Run(new CreateTableRequest { Table = "SgNome", Columns = [Id] }));

        Assert.Contains("SgNome", ex.Message);
    }

    [LocalDbFact]
    public void Basic_EveryScriptIsIdempotent()
    {
        ScriptRequest[] requests =
        [
            new CreateTableRequest { Table = "SgIdem", Columns = [Id, new() { Name = "Nome", DataType = "NVARCHAR", Length = 30 }], PrimaryKeyColumns = ["Id"] },
            new AddColumnRequest { Table = "SgIdem", Column = new() { Name = "Email", DataType = "VARCHAR", Length = 80 } },
            new RenameColumnRequest { Table = "SgIdem", ColumnName = "Nome", NewName = "Titulo" },
            new AddUniqueRequest { Table = "SgIdem", Columns = ["Email"] },
            new AddCheckRequest { Table = "SgIdem", ConstraintName = "CK_SgIdem", Expression = "[Id] > 0" },
            new AddDefaultRequest { Table = "SgIdem", ColumnName = "Titulo", Expression = "N'x'" },
            new CreateIndexRequest { Table = "SgIdem", Columns = [new("Titulo")] },
            new DropIndexRequest { Table = "SgIdem", IndexName = "IX_SgIdem_Titulo" },
            new DropConstraintRequest { Table = "SgIdem", ConstraintName = "CK_SgIdem" },
            new DropColumnRequest { Table = "SgIdem", ColumnName = "Titulo" },
            new DropTableRequest { Table = "SgIdem" },
        ];

        foreach (var request in requests)
        {
            Run(request);
            Run(request); // a 2ª encontra tudo pronto e não faz nada, sem erro
        }

        Assert.Equal(0, db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'SgIdem'"));
    }

    [LocalDbFact]
    public void Basic_ViewAndTriggerRecreateWork_AndAreIdempotent()
    {
        db.Run("CREATE TABLE dbo.SgVt (Id INT, Nome NVARCHAR(20));");

        var view = new CreateViewRequest { ViewName = "vw_SgVt", SelectStatement = "SELECT Id FROM dbo.SgVt" };
        Run(view);
        view.SelectStatement = "SELECT Id, Nome FROM dbo.SgVt";
        Run(view);
        Run(view);
        Assert.Equal(2, db.Scalar<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.vw_SgVt')"));

        var trigger = new CreateTriggerRequest { Table = "SgVt", TriggerName = "trg_SgVt", OnInsert = true, Body = "SELECT 1;" };
        Run(trigger);
        Run(trigger);
        Assert.Equal(1, db.Scalar<int>("SELECT COUNT(*) FROM sys.triggers WHERE name = 'trg_SgVt'"));
    }

    /// <summary>
    /// A contrapartida de usar o script simples: se o CREATE falhar, o DROP já aconteceu e a view antiga se perdeu.
    /// É por isso que a transação que desfaz o DROP faz parte do Safe guard.
    /// </summary>
    [LocalDbFact]
    public void Basic_ViewRecreate_WhenTheCreateFails_TheOldViewIsGone_WhileTheSafeGuardKeepsIt()
    {
        db.Run("CREATE TABLE dbo.SgPerda (Id INT);\nGO\nCREATE VIEW dbo.vw_SgPerda AS SELECT Id FROM dbo.SgPerda;");
        db.Run("CREATE TABLE dbo.SgPerda2 (Id INT);\nGO\nCREATE VIEW dbo.vw_SgPerda2 AS SELECT Id FROM dbo.SgPerda2;");

        var plain = _generator.Generate(new CreateViewRequest { ViewName = "vw_SgPerda", SelectStatement = "SELECT Fantasma FROM dbo.SgPerda" }, new ScriptOptions());
        var guarded = _generator.Generate(new CreateViewRequest { ViewName = "vw_SgPerda2", SelectStatement = "SELECT Fantasma FROM dbo.SgPerda2" }, new ScriptOptions { SafeGuard = true });

        var plainRun = db.RunAll(plain.Script);
        var guardedRun = db.RunAll(guarded.Script);

        Assert.NotEmpty(plainRun.Errors);
        Assert.Equal(0, db.Scalar<int>("SELECT COUNT(*) FROM sys.views WHERE name = 'vw_SgPerda'"));   // perdida

        Assert.NotEmpty(guardedRun.Errors);
        Assert.Equal(1, db.Scalar<int>("SELECT COUNT(*) FROM sys.views WHERE name = 'vw_SgPerda2'"));  // preservada
        Assert.Equal(0, guardedRun.FinalTranCount);
    }

    [LocalDbFact]
    public void Basic_AndSafeGuard_LeaveTheSameFinalState_OnTheHappyPath()
    {
        db.Run("CREATE SCHEMA sg;");
        foreach (var (name, safeGuard) in new[] { ("Igual1", false), ("Igual2", true) })
        {
            Run(new CreateTableRequest { Schema = "sg", Table = name, Columns = [Id, new() { Name = "Nome", DataType = "NVARCHAR", Length = 30 }], PrimaryKeyColumns = ["Id"] }, safeGuard);
            Run(new AddColumnRequest { Schema = "sg", Table = name, Column = new() { Name = "Email", DataType = "VARCHAR", Length = 80 } }, safeGuard);
            Run(new CreateIndexRequest { Schema = "sg", Table = name, Columns = [new("Nome")] }, safeGuard);
        }

        string Shape(string table) => string.Join(",", Enumerable.Range(1, 3).Select(i => db.Scalar<string>($"SELECT COL_NAME(OBJECT_ID('sg.{table}'), {i})")))
            + "|" + db.Scalar<int>($"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('sg.{table}') AND index_id > 0");

        Assert.Equal(Shape("Igual1"), Shape("Igual2"));
        Assert.Equal("Id,Nome,Email|2", Shape("Igual1"));
    }
}
