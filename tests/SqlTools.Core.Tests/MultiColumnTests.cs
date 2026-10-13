using SqlTools.Core.Generation;
using SqlTools.Core.Model;
using SqlTools.Core.Requests;

namespace SqlTools.Core.Tests;

/// <summary>Várias colunas em uma só interação: validação, geração, importação de texto.</summary>
public class MultiColumnTests
{
    private static readonly SqlServerScriptGenerator Generator = new();
    private static readonly ScriptOptions Plain = new();

    private static List<ColumnDefinition> Columns(int count) =>
        Enumerable.Range(1, count).Select(i => new ColumnDefinition { Name = $"Campo{i}", DataType = "NVARCHAR", Length = 50 }).ToList();

    [Fact]
    public void AddColumns_25Columns_GeneratesOneBatchPerColumn()
    {
        var result = Generator.Generate(new AddColumnsRequest { Table = "Cliente", Columns = Columns(25) }, Plain);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        for (var i = 1; i <= 25; i++)
            Assert.Contains($"Campo{i}", result.Script);
        Assert.Equal(25, result.Script.Split('\n').Count(l => l.Trim() == "GO"));
        Assert.Contains("-- [25/25]", result.Script);
    }

    [Fact]
    public void AddColumns_RejectsEmptyListDuplicatesAndTwoIdentities()
    {
        Assert.False(Generator.Generate(new AddColumnsRequest { Table = "T" }, Plain).IsValid);

        var duplicates = Columns(2);
        duplicates[1].Name = "CAMPO1";
        var dup = Generator.Generate(new AddColumnsRequest { Table = "T", Columns = duplicates }, Plain);
        Assert.Contains(dup.Errors, e => e.Contains("mais de uma vez"));

        var identities = new List<ColumnDefinition>
        {
            new() { Name = "A", DataType = "INT", IsNullable = false, IsIdentity = true },
            new() { Name = "B", DataType = "INT", IsNullable = false, IsIdentity = true },
        };
        var two = Generator.Generate(new AddColumnsRequest { Table = "T", Columns = identities }, Plain);
        Assert.Contains(two.Errors, e => e.Contains("IDENTITY"));
    }

    [Fact]
    public void AddColumns_MatchesSeparateRequests()
    {
        var columns = Columns(3);
        var composite = Generator.Generate(new AddColumnsRequest { Table = "T", Columns = columns }, Plain);
        var separate = Generator.GenerateBatch(columns.Select(c => (ScriptRequest)new AddColumnRequest { Table = "T", Column = c }).ToList(), Plain);

        Assert.Equal(separate.Script, composite.Script);
    }

    [Fact]
    public void AddColumns_HonorsSafeGuardAndPrint()
    {
        var result = Generator.Generate(new AddColumnsRequest { Table = "T", Columns = Columns(2) },
            new ScriptOptions { SafeGuard = true, AddPrintCommands = true });

        Assert.True(result.IsValid);
        Assert.Contains("PRINT", result.Script);
        Assert.Contains("RAISERROR", result.Script);
    }

    [Fact]
    public void AlterRenameDrop_GenerateOneBatchPerItem()
    {
        var alter = Generator.Generate(new AlterColumnsRequest { Table = "T", Columns = Columns(4) }, Plain);
        Assert.True(alter.IsValid, string.Join("; ", alter.Errors));
        Assert.Equal(4, alter.Script.Split('\n').Count(l => l.Trim() == "GO"));

        var rename = Generator.Generate(new RenameColumnsRequest
        {
            Table = "T", Renames = [new() { From = "A", To = "B" }, new() { From = "C", To = "D" }],
        }, Plain);
        Assert.True(rename.IsValid, string.Join("; ", rename.Errors));
        Assert.Equal(2, rename.Script.Split('\n').Count(l => l.Trim() == "GO"));

        var drop = Generator.Generate(new DropColumnsRequest { Table = "T", ColumnNames = ["A", "B", "C"] }, Plain);
        Assert.True(drop.IsValid, string.Join("; ", drop.Errors));
        Assert.Equal(3, drop.Script.Split('\n').Count(l => l.Trim() == "GO"));
    }

    [Fact]
    public void AlterColumns_RejectsIdentityAndDefault()
    {
        var columns = Columns(1);
        columns[0].DefaultExpression = "N'x'";
        Assert.False(Generator.Generate(new AlterColumnsRequest { Table = "T", Columns = columns }, Plain).IsValid);
    }

    [Fact]
    public void RenameColumns_RejectsChainsDuplicatesAndSameName()
    {
        static RenameColumnsRequest Rename(params (string From, string To)[] items) =>
            new() { Table = "T", Renames = items.Select(i => new ColumnRename { From = i.From, To = i.To }).ToList() };

        Assert.False(Generator.Generate(Rename(("A", "B"), ("B", "C")), Plain).IsValid); // encadeada
        Assert.False(Generator.Generate(Rename(("A", "B"), ("A", "C")), Plain).IsValid); // mesma origem
        Assert.False(Generator.Generate(Rename(("A", "C"), ("B", "C")), Plain).IsValid); // mesmo destino
        Assert.False(Generator.Generate(Rename(("A", "A")), Plain).IsValid);
        Assert.False(Generator.Generate(Rename(("", "B")), Plain).IsValid);
        Assert.True(Generator.Generate(Rename(("A", "B"), ("C", "D")), Plain).IsValid);
    }

    [Fact]
    public void DropColumns_RejectsEmptyAndDuplicates()
    {
        Assert.False(Generator.Generate(new DropColumnsRequest { Table = "T" }, Plain).IsValid);
        Assert.False(Generator.Generate(new DropColumnsRequest { Table = "T", ColumnNames = ["A", "a"] }, Plain).IsValid);
    }

    [Fact]
    public void Composite_CanBeMixedInABatch()
    {
        var result = Generator.GenerateBatch(
        [
            new AddColumnsRequest { Table = "T", Columns = Columns(2) },
            new DropColumnsRequest { Table = "T", ColumnNames = ["X"] },
        ], Plain);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains("-- [3/3]", result.Script);
    }

    // ---------- importação de texto ----------

    [Fact]
    public void Parser_ReadsTypesNullabilityIdentityAndDefaults()
    {
        var result = ColumnDefinitionParser.Parse("""
            Nome        NVARCHAR(100) NOT NULL,
            Email       varchar(150)
            Bio         NVARCHAR(MAX)
            Saldo       DECIMAL(12,2) NOT NULL DEFAULT 0
            CriadoEm    DATETIME2(3) NOT NULL CONSTRAINT DF_X_CriadoEm DEFAULT SYSDATETIME()
            Codigo      INT IDENTITY(10,5)
            -- comentário
            [Data Nasc] DATE NULL
            Ativo       BIT DEFAULT (1) NOT NULL
            """);

        Assert.Empty(result.Errors);
        var c = result.Items;
        Assert.Equal(8, c.Count);
        Assert.Equal(("Nome", "NVARCHAR", 100, false), (c[0].Name, c[0].DataType, c[0].Length, c[0].IsNullable));
        Assert.True(c[1].IsNullable);
        Assert.Equal(150, c[1].Length);
        Assert.True(c[2].IsMax);
        Assert.Equal((12, 2, false, "0"), (c[3].Precision, c[3].Scale, c[3].IsNullable, c[3].DefaultExpression));
        Assert.Equal(3, c[4].FractionalSeconds);
        Assert.Equal("DF_X_CriadoEm", c[4].DefaultConstraintName);
        Assert.Equal("SYSDATETIME()", c[4].DefaultExpression);
        Assert.True(c[5].IsIdentity);
        Assert.False(c[5].IsNullable);
        Assert.Equal((10L, 5L), (c[5].IdentitySeed, c[5].IdentityIncrement));
        Assert.Equal("Data Nasc", c[6].Name);
        Assert.Equal("1", c[7].DefaultExpression);
        Assert.False(c[7].IsNullable);
    }

    [Fact]
    public void Parser_ReportsBadLinesButKeepsGoodOnes()
    {
        var result = ColumnDefinitionParser.Parse("""
            Ok INT
            SemTipo
            Ruim FOO(10)
            Pk INT PRIMARY KEY
            Tam NVARCHAR(abc)
            Outro BIT
            """);

        Assert.Equal(["Ok", "Outro"], result.Items.Select(i => i.Name));
        Assert.Equal(4, result.Errors.Count);
        Assert.StartsWith("Linha 2:", result.Errors[0]);
    }

    [Fact]
    public void Parser_ImportedColumnsGenerateScript()
    {
        var parsed = ColumnDefinitionParser.Parse("Nome NVARCHAR(100) NOT NULL DEFAULT N'x'\nIdade INT");
        var result = Generator.Generate(new AddColumnsRequest { Table = "Pessoa", Columns = parsed.Items }, Plain);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains("N'x'", result.Script);
    }

    [Theory]
    [InlineData("A -> B")]
    [InlineData("A->B")]
    [InlineData("A\tB")]
    [InlineData("A,B")]
    [InlineData("A;B")]
    [InlineData("A = B")]
    [InlineData("[A] -> [B]")]
    [InlineData("A B")]
    public void RenameParser_AcceptsCommonSeparators(string line)
    {
        var result = ColumnRenameParser.Parse(line);

        Assert.Empty(result.Errors);
        var rename = Assert.Single(result.Items);
        Assert.Equal(("A", "B"), (rename.From, rename.To));
    }

    [Fact]
    public void RenameParser_ReportsBadLines()
    {
        var result = ColumnRenameParser.Parse("A -> B\nsó\nX -> Y -> Z\n\nC -> D");

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.Errors.Count);
        Assert.StartsWith("Linha 2:", result.Errors[0]);
    }
}
