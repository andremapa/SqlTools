using SqlTools.Core.Generation;
using SqlTools.Core.Model;
using SqlTools.Core.Requests;

namespace SqlTools.Core.Tests;

public class GeneratorTests
{
    private static readonly ScriptOptions NoHeader = new() { IncludeHeader = false };
    private readonly SqlServerScriptGenerator _generator = new();

    private static CreateTableRequest ClienteTable() => new()
    {
        Table = "Cliente",
        Columns =
        [
            new ColumnDefinition { Name = "Id", DataType = "INT", IsNullable = false, IsIdentity = true },
            new ColumnDefinition { Name = "Nome", DataType = "NVARCHAR", Length = 100, IsNullable = false },
            new ColumnDefinition { Name = "CriadoEm", DataType = "DATETIME2", FractionalSeconds = 3, IsNullable = false, DefaultExpression = "SYSDATETIME()" },
        ],
        PrimaryKeyColumns = ["Id"],
    };

    [Fact]
    public void Options_TransactionAndHeaderAreOffByDefault()
    {
        var options = new ScriptOptions();

        Assert.False(options.UseTransaction);
        Assert.False(options.IncludeHeader);

        var script = _generator.Generate(ClienteTable(), options).Script;
        Assert.DoesNotContain("BEGIN TRY", script);
        Assert.DoesNotContain("-- Operação", script);
    }

    [Fact]
    public void CreateTable_ChecksExistenceAndWritesDefinition()
    {
        var result = _generator.Generate(ClienteTable(), new ScriptOptions { UseTransaction = true });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        // Padrão (sem Safe guard e sem PRINT): só a verificação "a tabela não existe".
        Assert.Contains("IF OBJECT_ID(N'[dbo].[Cliente]', N'U') IS NULL", result.Script);
        Assert.Contains("[Id] INT IDENTITY(1,1) NOT NULL,", result.Script);
        Assert.Contains("[Nome] NVARCHAR(100) NOT NULL,", result.Script);
        Assert.Contains("[CriadoEm] DATETIME2(3) NOT NULL CONSTRAINT [DF_Cliente_CriadoEm] DEFAULT (SYSDATETIME()),", result.Script);
        Assert.Contains("CONSTRAINT [PK_Cliente] PRIMARY KEY CLUSTERED ([Id])", result.Script);
        Assert.Contains("BEGIN TRANSACTION;", result.Script);
        Assert.EndsWith("GO" + Environment.NewLine, result.Script);
    }

    [Fact]
    public void Identifiers_AreEscaped()
    {
        var request = new DropTableRequest { Schema = "dbo", Table = "Tab]ela'X" };

        var result = _generator.Generate(request, NoHeader);

        Assert.Contains("DROP TABLE [dbo].[Tab]]ela'X];", result.Script);
        Assert.Contains("OBJECT_ID(N'[dbo].[Tab]]ela''X]', N'U') IS NOT NULL", result.Script);
    }

    [Fact]
    public void RaiserrorMessages_EscapePercent()
    {
        var request = new AddColumnRequest { Table = "Taxa%", Column = new() { Name = "Valor", IsNullable = false } };

        var result = _generator.Generate(request, new ScriptOptions { SafeGuard = true });

        Assert.Contains("RAISERROR(N'Tabela dbo.Taxa%% não existe.', 16, 1);", result.Script);
    }

    [Fact]
    public void WithoutTransaction_HasNoTryCatch()
    {
        var result = _generator.Generate(ClienteTable(), new ScriptOptions { IncludeHeader = false, UseTransaction = false });

        Assert.DoesNotContain("BEGIN TRY", result.Script);
        Assert.DoesNotContain("TRANSACTION", result.Script);
    }

    [Fact]
    public void DatabaseName_AddsUse()
    {
        var result = _generator.Generate(ClienteTable(), new ScriptOptions { IncludeHeader = false, DatabaseName = "Loja" });

        Assert.StartsWith("USE [Loja];", result.Script);
    }

    [Fact]
    public void CreateTable_PrimaryKeyOnNullableColumn_IsInvalid()
    {
        var request = ClienteTable();
        request.PrimaryKeyColumns = ["Nome", "Inexistente"];
        request.Columns[1].IsNullable = true;

        var result = _generator.Generate(request, NoHeader);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'Nome'") && e.Contains("NOT NULL"));
        Assert.Contains(result.Errors, e => e.Contains("'Inexistente'"));
    }

    [Theory]
    [InlineData("VARCHAR", 9000, "tamanho")]
    [InlineData("NVARCHAR", 4001, "tamanho")]
    [InlineData("TIPO_X", null, "não suportado")]
    public void Column_InvalidType_IsReported(string type, int? length, string expected)
    {
        var column = new ColumnDefinition { Name = "C", DataType = type, Length = length };

        Assert.Contains(column.Validate(), e => e.Contains(expected));
    }

    [Fact]
    public void Identity_OnText_IsInvalid()
    {
        var column = new ColumnDefinition { Name = "C", DataType = "VARCHAR", IsIdentity = true, IsNullable = false };

        Assert.Contains(column.Validate(), e => e.Contains("IDENTITY"));
    }

    [Fact]
    public void ForeignKey_ColumnCountMismatch_IsInvalid()
    {
        var request = new AddForeignKeyRequest
        {
            Table = "Pedido", ReferencedTable = "Cliente", Columns = ["ClienteId", "Outra"], ReferencedColumns = ["Id"],
        };

        Assert.Contains(request.Validate(), e => e.Contains("quantidade"));
    }

    [Fact]
    public void Batch_ListsEveryOperation()
    {
        var requests = new ScriptRequest[]
        {
            ClienteTable(),
            new CreateIndexRequest { Table = "Cliente", Columns = [new("Nome")] },
        };

        var result = _generator.GenerateBatch(requests, new ScriptOptions { IncludeHeader = true });

        Assert.True(result.IsValid);
        Assert.Contains("--   1. Criar tabela — dbo.Cliente", result.Script);
        Assert.Contains("--   2. Criar índice — IX_Cliente_Nome em dbo.Cliente", result.Script);
        Assert.Equal(2, result.Script.Split(Environment.NewLine).Count(l => l == "GO"));
    }

    [Fact]
    public void Batch_PrefixesErrorsWithPosition()
    {
        var result = _generator.GenerateBatch([ClienteTable(), new DropTableRequest()], NoHeader);

        Assert.Contains("[2] Excluir tabela: Tabela é obrigatório.", result.Errors);
    }

    [Theory]
    [InlineData("Nome DESC, [Id], Data asc", "Nome:Desc|Id:Asc|Data:Asc")]
    [InlineData("[Nome Completo] DESC", "Nome Completo:Desc")]
    public void IndexColumn_ParsesDirection(string text, string expected)
    {
        var parsed = IndexColumn.ParseList(text);

        Assert.Equal(expected, string.Join("|", parsed.Select(c => $"{c.Name}:{c.Direction}")));
    }
}
