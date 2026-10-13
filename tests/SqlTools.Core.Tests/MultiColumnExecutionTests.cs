using SqlTools.Core.Generation;
using SqlTools.Core.Model;
using SqlTools.Core.Requests;

namespace SqlTools.Core.Tests;

/// <summary>Executa no SQL Server um script de várias colunas de uma vez (adicionar, alterar, renomear, excluir).</summary>
public class MultiColumnExecutionTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly SqlServerScriptGenerator _generator = new();

    private void Run(ScriptRequest request, bool safeGuard)
    {
        var result = _generator.Generate(request, new ScriptOptions { SafeGuard = safeGuard, UseTransaction = safeGuard });
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        db.Run(result.Script);
        db.Run(result.Script); // idempotente: reexecutar não falha
    }

    private int ColumnCount(string table) =>
        db.Scalar<int>($"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.{table}')");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwentyFiveColumns_AreAddedAlteredRenamedAndDropped_InOneScript(bool safeGuard)
    {
        var table = "MultiCol" + (safeGuard ? "Guard" : "Plain");
        db.Run($"IF OBJECT_ID('dbo.{table}') IS NOT NULL DROP TABLE dbo.{table}; CREATE TABLE dbo.{table} (Id INT NOT NULL PRIMARY KEY);");

        var columns = Enumerable.Range(1, 25)
            .Select(i => new ColumnDefinition { Name = $"Campo{i}", DataType = "NVARCHAR", Length = 50 })
            .ToList();
        Run(new AddColumnsRequest { Table = table, Columns = columns }, safeGuard);
        Assert.Equal(26, ColumnCount(table));

        foreach (var c in columns.Take(10)) c.Length = 80;
        Run(new AlterColumnsRequest { Table = table, Columns = columns.Take(10).ToList() }, safeGuard);
        Assert.Equal(80, db.Scalar<int>($"SELECT max_length / 2 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.{table}') AND name = 'Campo1'"));

        Run(new RenameColumnsRequest
        {
            Table = table,
            Renames = Enumerable.Range(11, 5).Select(i => new ColumnRename { From = $"Campo{i}", To = $"Novo{i}" }).ToList(),
        }, safeGuard);
        Assert.Equal(5, db.Scalar<int>($"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.{table}') AND name LIKE 'Novo%'"));

        Run(new DropColumnsRequest { Table = table, ColumnNames = Enumerable.Range(1, 10).Select(i => $"Campo{i}").ToList() }, safeGuard);
        Assert.Equal(16, ColumnCount(table));

        db.Run($"DROP TABLE dbo.{table};");
    }
}
