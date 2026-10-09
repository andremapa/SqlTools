using Microsoft.Data.SqlClient;
using SqlTools.Core.Generation;
using SqlTools.Core.Model;
using SqlTools.Core.Requests;

namespace SqlTools.Core.Tests;

/// <summary>
/// Executa os scripts gerados em um banco real e verifica:
/// (1) a alteração é aplicada; (2) reexecutar não falha (idempotência);
/// (3) pré-condições não atendidas geram erro e não alteram o banco.
/// </summary>
public class ExecutionTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly SqlServerScriptGenerator _generator = new();

    private List<string> Run(ScriptRequest request, bool useTransaction = true)
    {
        var result = _generator.Generate(request, new ScriptOptions { UseTransaction = useTransaction });
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return db.Run(result.Script);
    }

    private void RunExpectingError(ScriptRequest request, string expectedMessage)
    {
        var ex = Assert.Throws<SqlException>(() => Run(request));
        Assert.Contains(expectedMessage, ex.Message);
    }

    private void CreateTable(string table, params ColumnDefinition[] extraColumns)
    {
        Run(new CreateTableRequest
        {
            Table = table,
            Columns = [new() { Name = "Id", IsNullable = false, IsIdentity = true }, .. extraColumns],
            PrimaryKeyColumns = ["Id"],
        });
    }

    private int Count(string sql) => db.Scalar<int>(sql);

    [LocalDbFact]
    public void CreateTable_InNewSchema_IsIdempotent()
    {
        var request = new CreateTableRequest
        {
            Schema = "vendas",
            Table = "Produto",
            Columns =
            [
                new() { Name = "Id", DataType = "BIGINT", IsNullable = false, IsIdentity = true },
                new() { Name = "Nome", DataType = "NVARCHAR", Length = 200, IsNullable = false },
                new() { Name = "Preco", DataType = "DECIMAL", Precision = 10, Scale = 2, IsNullable = false, DefaultExpression = "0" },
                new() { Name = "Descricao", DataType = "NVARCHAR", IsMax = true },
            ],
            PrimaryKeyColumns = ["Id"],
        };

        var first = Run(request);
        var second = Run(request);

        Assert.Contains("Tabela vendas.Produto criada.", first);
        Assert.Contains("Tabela vendas.Produto já existe. Nenhuma alteração realizada.", second);
        Assert.Equal(4, Count("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('vendas.Produto')"));
    }

    [LocalDbFact]
    public void AddColumn_NotNullWithoutDefault_FailsWhenTableHasRows()
    {
        CreateTable("AddColTest", new ColumnDefinition { Name = "Nome", DataType = "VARCHAR", Length = 10 });
        db.Run("INSERT INTO dbo.AddColTest (Nome) VALUES ('a');");

        RunExpectingError(
            new AddColumnRequest { Table = "AddColTest", Column = new() { Name = "Codigo", IsNullable = false } },
            "possui registros");

        var withDefault = new AddColumnRequest
        {
            Table = "AddColTest",
            Column = new() { Name = "Codigo", IsNullable = false, DefaultExpression = "42" },
        };
        Run(withDefault);
        var again = Run(withDefault);

        Assert.Contains("Coluna dbo.AddColTest.Codigo já existe. Nenhuma alteração realizada.", again);
        Assert.Equal(42, Count("SELECT Codigo FROM dbo.AddColTest"));
    }

    [LocalDbFact]
    public void AddColumn_NullableWithDefault_FillsExistingRows()
    {
        CreateTable("WithValuesTest");
        db.Run("INSERT INTO dbo.WithValuesTest DEFAULT VALUES;");

        Run(new AddColumnRequest { Table = "WithValuesTest", Column = new() { Name = "Status", DefaultExpression = "1" } });

        Assert.Equal(1, Count("SELECT Status FROM dbo.WithValuesTest"));
    }

    [LocalDbFact]
    public void AlterColumn_ToNotNull_FailsWhileNullsExist()
    {
        CreateTable("AlterTest", new ColumnDefinition { Name = "Nome", DataType = "VARCHAR", Length = 10 });
        db.Run("INSERT INTO dbo.AlterTest (Nome) VALUES (NULL);");
        var request = new AlterColumnRequest
        {
            Table = "AlterTest",
            Column = new() { Name = "Nome", DataType = "NVARCHAR", Length = 50, IsNullable = false },
        };

        RunExpectingError(request, "possui valores NULL");

        db.Run("UPDATE dbo.AlterTest SET Nome = 'x';");
        Run(request);
        Run(request);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AlterTest') AND name = 'Nome' AND is_nullable = 1"));
    }

    [LocalDbFact]
    public void AlterColumn_MissingColumn_FailsWithMessage()
    {
        CreateTable("AlterMissing");

        RunExpectingError(
            new AlterColumnRequest { Table = "AlterMissing", Column = new() { Name = "Nada", IsNullable = false } },
            "Coluna dbo.AlterMissing.Nada não existe");
    }

    [LocalDbFact]
    public void RenameColumn_IsIdempotent()
    {
        CreateTable("RenameTest", new ColumnDefinition { Name = "Nme" });
        var request = new RenameColumnRequest { Table = "RenameTest", ColumnName = "Nme", NewName = "Nome" };

        Run(request);
        var again = Run(request);

        Assert.Contains("Coluna dbo.RenameTest.Nme já foi renomeada para Nome. Nenhuma alteração realizada.", again);
    }

    [LocalDbFact]
    public void DropColumn_RemovesDefaultConstraintFirst()
    {
        CreateTable("DropColTest", new ColumnDefinition { Name = "Ativo", DataType = "BIT", IsNullable = false, DefaultExpression = "1" });
        var request = new DropColumnRequest { Table = "DropColTest", ColumnName = "Ativo" };

        var first = Run(request);
        var again = Run(request);

        Assert.Contains("Constraint default DF_DropColTest_Ativo removida.", first);
        Assert.Contains("Coluna dbo.DropColTest.Ativo não existe. Nenhuma alteração realizada.", again);
    }

    [LocalDbFact]
    public void DropColumn_UsedInIndex_Fails()
    {
        CreateTable("DropIdxCol", new ColumnDefinition { Name = "Email", DataType = "VARCHAR", Length = 100 });
        Run(new CreateIndexRequest { Table = "DropIdxCol", Columns = [new("Email")] });

        RunExpectingError(new DropColumnRequest { Table = "DropIdxCol", ColumnName = "Email" }, "usada em índice");
    }

    [LocalDbFact]
    public void PrimaryKey_RequiresNotNullColumns_AndIsIdempotent()
    {
        Run(new CreateTableRequest
        {
            Table = "PkTest",
            Columns = [new() { Name = "Codigo", IsNullable = true }],
        });
        var request = new AddPrimaryKeyRequest { Table = "PkTest", Columns = ["Codigo"] };

        RunExpectingError(request, "aceita NULL");

        Run(new AlterColumnRequest { Table = "PkTest", Column = new() { Name = "Codigo", IsNullable = false } });
        Run(request);
        var again = Run(request);

        Assert.Contains("Chave primária PK_PkTest já existe. Nenhuma alteração realizada.", again);
    }

    [LocalDbFact]
    public void ForeignKey_FullLifecycle()
    {
        CreateTable("FkPai", new ColumnDefinition { Name = "Codigo", IsNullable = false });
        CreateTable("FkFilho", new ColumnDefinition { Name = "PaiId", IsNullable = false });

        RunExpectingError(
            new AddForeignKeyRequest { Table = "FkFilho", Columns = ["PaiId"], ReferencedTable = "FkPai", ReferencedColumns = ["Codigo"] },
            "não formam PK/UNIQUE");

        var fk = new AddForeignKeyRequest
        {
            Table = "FkFilho", Columns = ["PaiId"], ReferencedTable = "FkPai", ReferencedColumns = ["Id"],
            OnDelete = ReferentialAction.Cascade,
        };
        Run(fk);
        Assert.Contains("Chave estrangeira FK_FkFilho_FkPai já existe. Nenhuma alteração realizada.", Run(fk));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_FkFilho_FkPai' AND delete_referential_action = 1"));

        RunExpectingError(new DropTableRequest { Table = "FkPai" }, "referenciada por chaves estrangeiras");
        RunExpectingError(new DropConstraintRequest { Table = "FkPai", ConstraintName = "PK_FkPai" }, "referenciada por chaves estrangeiras");

        var dropFk = new DropConstraintRequest { Table = "FkFilho", ConstraintName = "FK_FkFilho_FkPai" };
        Run(dropFk);
        Assert.Contains("Constraint FK_FkFilho_FkPai não existe em dbo.FkFilho. Nenhuma alteração realizada.", Run(dropFk));

        Run(new DropTableRequest { Table = "FkPai" });
        Assert.Contains("Tabela dbo.FkPai não existe. Nenhuma alteração realizada.", Run(new DropTableRequest { Table = "FkPai" }));
    }

    [LocalDbFact]
    public void UniqueCheckAndDefault_AreIdempotent()
    {
        CreateTable("CkTest",
            new ColumnDefinition { Name = "Email", DataType = "VARCHAR", Length = 100, IsNullable = false },
            new ColumnDefinition { Name = "Valor", DataType = "DECIMAL", Precision = 10, Scale = 2 });

        ScriptRequest[] requests =
        [
            new AddUniqueRequest { Table = "CkTest", Columns = ["Email"] },
            new AddCheckRequest { Table = "CkTest", ConstraintName = "CK_CkTest_Valor", Expression = "[Valor] >= 0" },
            new AddDefaultRequest { Table = "CkTest", ColumnName = "Valor", Expression = "0" },
        ];

        foreach (var request in requests)
        {
            Run(request);
            Assert.Contains(Run(request), m => m.Contains("já existe. Nenhuma alteração realizada."));
        }

        RunExpectingError(
            new AddDefaultRequest { Table = "CkTest", ColumnName = "Valor", ConstraintName = "DF_Outro", Expression = "1" },
            "já possui outra constraint DEFAULT");
        Assert.Throws<SqlException>(() => db.Run("INSERT INTO dbo.CkTest (Email, Valor) VALUES ('a', -1);"));
    }

    [LocalDbFact]
    public void Index_CreateAndDrop_AreIdempotent()
    {
        CreateTable("IdxTest",
            new ColumnDefinition { Name = "Nome", DataType = "NVARCHAR", Length = 100 },
            new ColumnDefinition { Name = "Ativo", DataType = "BIT" },
            new ColumnDefinition { Name = "Email", DataType = "VARCHAR", Length = 100 });

        var create = new CreateIndexRequest
        {
            Table = "IdxTest",
            Columns = [new("Nome", SortDirection.Desc)],
            IncludedColumns = ["Email"],
            Filter = "[Ativo] = 1",
        };
        Run(create);
        Assert.Contains("Índice IX_IdxTest_Nome já existe em dbo.IdxTest. Nenhuma alteração realizada.", Run(create));

        RunExpectingError(new CreateIndexRequest { Table = "IdxTest", Columns = [new("Id")], IsClustered = true }, "já possui índice CLUSTERED");
        RunExpectingError(new DropIndexRequest { Table = "IdxTest", IndexName = "PK_IdxTest" }, "pertence a uma PK/UNIQUE");

        var drop = new DropIndexRequest { Table = "IdxTest", IndexName = "IX_IdxTest_Nome" };
        Run(drop);
        Assert.Contains("Índice IX_IdxTest_Nome não existe em dbo.IdxTest. Nenhuma alteração realizada.", Run(drop));
    }

    [LocalDbFact]
    public void FailedValidation_InTransaction_ChangesNothing()
    {
        // Lote: cria tabela e depois tenta FK para tabela inexistente. Cada operação é atômica;
        // a primeira é aplicada e a segunda falha sem efeitos colaterais.
        var result = _generator.GenerateBatch(
        [
            new CreateTableRequest { Table = "BatchTest", Columns = [new() { Name = "RefId", IsNullable = false }] },
            new AddForeignKeyRequest { Table = "BatchTest", Columns = ["RefId"], ReferencedTable = "NaoExiste", ReferencedColumns = ["Id"] },
        ], new ScriptOptions());

        var ex = Assert.Throws<SqlException>(() => db.Run(result.Script));

        Assert.Contains("Tabela referenciada dbo.NaoExiste não existe", ex.Message);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.tables WHERE name = 'BatchTest'"));
        Assert.Equal(0, Count("SELECT @@TRANCOUNT"));
    }

    [LocalDbFact]
    public void ScriptsWork_WithoutTransaction()
    {
        CreateTable("NoTranTest");
        var request = new AddColumnRequest { Table = "NoTranTest", Column = new() { Name = "Obs", DataType = "NVARCHAR", Length = 20 } };

        Run(request, useTransaction: false);
        Assert.Contains("Coluna dbo.NoTranTest.Obs já existe. Nenhuma alteração realizada.", Run(request, useTransaction: false));
    }
}
