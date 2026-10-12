using SqlTools.Core.Sql;

namespace SqlTools.Core.Tests;

public class ModuleParsingTests
{
    private static NewViewColumn Col(string expression, string name) => new(expression, name);

    private static ViewWithColumns Add(string definition, params NewViewColumn[] columns)
    {
        var result = TsqlModules.AddColumnsToView(definition, columns);
        Assert.True(result.Ok, result.Error);
        return result.Value!;
    }

    [Fact]
    public void AddColumns_InsertsBeforeFrom_KeepingTheRestOfTheQuery()
    {
        var view = Add("""
            CREATE VIEW [dbo].[vw_Cliente] AS
            SELECT c.Id, c.Nome
            FROM dbo.Cliente c
            WHERE c.Ativo = 1
            """, Col("c.Email", "Email"), Col("c.Saldo * 2 AS Dobro", "Dobro"));

        Assert.Equal("vw_Cliente", view.Name.Name);
        Assert.Equal("dbo", view.Name.Schema);
        Assert.Equal("", view.Options);
        Assert.Contains("c.Nome,\n    c.Email,\n    c.Saldo * 2 AS Dobro\nFROM dbo.Cliente c", view.Body.Replace("\r\n", "\n"));
        Assert.EndsWith("WHERE c.Ativo = 1", view.Body);
    }

    [Fact]
    public void AddColumns_IgnoresFromInsideStringsCommentsAndBrackets()
    {
        var view = Add("""
            CREATE VIEW v AS
            SELECT 'dados FROM outra' AS a, [FROM] , /* FROM x */ b -- FROM aqui
            FROM t
            """, Col("c", "c"));

        var body = view.Body.Replace("\r\n", "\n");
        Assert.Contains("'dados FROM outra' AS a", body);
        Assert.Contains("b, -- FROM aqui\n    c\nFROM t", body); // o comentário fica na linha da coluna a que pertence
        Assert.EndsWith("FROM t", body);
    }

    [Fact]
    public void AddColumns_KeepsTheTrailingLineCommentWithItsColumn()
    {
        var view = Add("CREATE VIEW v AS\nSELECT c.Id, c.Nome -- basicas\nFROM dbo.Cliente c", Col("c.Email", "Email"), Col("c.Saldo", "Saldo"));

        Assert.Equal("SELECT c.Id, c.Nome, -- basicas\n    c.Email,\n    c.Saldo\nFROM dbo.Cliente c", view.Body.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AddColumns_LineCommentAtTheVeryEnd_WithoutFrom()
    {
        var view = Add("CREATE VIEW v AS SELECT 1 AS a -- fim", Col("2 AS b", "b"));

        Assert.Equal("SELECT 1 AS a, -- fim\n    2 AS b", view.Body.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AddColumns_IgnoresSubqueriesInTheSelectList()
    {
        var view = Add("CREATE VIEW v AS SELECT (SELECT TOP 1 x FROM y) AS s, a FROM t", Col("z", "z"));

        Assert.Contains("a,\n    z FROM t", view.Body.Replace("\r\n", "\n"));
        Assert.Contains("(SELECT TOP 1 x FROM y) AS s", view.Body);
    }

    [Fact]
    public void AddColumns_WorksWithCommonTableExpressions()
    {
        var view = Add("CREATE VIEW v AS WITH x AS (SELECT 1 AS a FROM d) SELECT a FROM x", Col("2 AS b", "b"));

        Assert.StartsWith("WITH x AS (SELECT 1 AS a FROM d)", view.Body);
        Assert.Contains("SELECT a,\n    2 AS b FROM x", view.Body.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AddColumns_WithoutFrom_AppendsToTheEnd()
    {
        var view = Add("CREATE VIEW v AS SELECT 1 AS a;", Col("2 AS b", "b"));

        Assert.Equal("SELECT 1 AS a,\n    2 AS b;", view.Body.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AddColumns_KeepsViewOptions()
    {
        var view = Add("CREATE VIEW dbo.v WITH SCHEMABINDING, VIEW_METADATA AS SELECT a FROM dbo.t", Col("b", "b"));

        Assert.Equal("WITH SCHEMABINDING, VIEW_METADATA", view.Options);
    }

    [Fact]
    public void AddColumns_AcceptsCreateOrAlterAndQuotedNames()
    {
        var view = Add("CREATE OR ALTER VIEW [vendas].[Pedidos \"x\"] AS SELECT a FROM t", Col("b", "b"));

        Assert.Equal("vendas", view.Name.Schema);
        Assert.Equal("Pedidos \"x\"", view.Name.Name);
    }

    [Theory]
    [InlineData("CREATE VIEW v AS SELECT a FROM t UNION ALL SELECT a FROM u", "UNION")]
    [InlineData("CREATE VIEW v AS SELECT a FROM t EXCEPT SELECT a FROM u", "UNION")]
    [InlineData("CREATE VIEW v (x, y) AS SELECT a, b FROM t", "lista de colunas")]
    [InlineData("SELECT a FROM t", "CREATE VIEW")]
    [InlineData("CREATE TABLE x (a INT)", "CREATE VIEW")]
    [InlineData("CREATE VIEW v AS", "SELECT")]
    [InlineData("CREATE VIEW bd.dbo.v AS SELECT a FROM t", "nome")]
    public void AddColumns_ExplainsWhatItCannotDo(string definition, string expectedInMessage)
    {
        var result = TsqlModules.AddColumnsToView(definition, [Col("b", "b")]);

        Assert.False(result.Ok);
        Assert.Contains(expectedInMessage, result.Error);
    }

    [Fact]
    public void UnionInsideASubqueryIsFine()
    {
        var result = TsqlModules.AddColumnsToView(
            "CREATE VIEW v AS SELECT (SELECT 1 UNION SELECT 2) AS a FROM t", [Col("b", "b")]);

        Assert.True(result.Ok, result.Error);
    }

    [Theory]
    [InlineData("c.Email", "Email")]
    [InlineData("Email", "Email")]
    [InlineData("[c].[Meu Nome]", "Meu Nome")]
    [InlineData("p.Total * 1.1 AS TotalComImposto", "TotalComImposto")]
    [InlineData("CAST(a AS INT) AS Valor", "Valor")]
    [InlineData("Total = p.Total * 2", "Total")]
    [InlineData("UPPER(c.Nome) AS [Nome Maiusculo],", "Nome Maiusculo")]
    [InlineData("1 AS Um", "Um")]
    public void ReadNewColumn_FindsTheColumnName(string text, string expectedName)
    {
        var result = TsqlModules.ReadNewColumn(text);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(expectedName, result.Value!.Name);
    }

    [Theory]
    [InlineData("", "vazia")]
    [InlineData("UPPER(c.Nome)", "alias")]
    [InlineData("NULL", "alias")]
    [InlineData("1", "alias")]
    [InlineData("a FROM t", "FROM")]
    [InlineData("a WHERE b = 1", "WHERE")]
    [InlineData("(SELECT 1", "Parênteses")]
    [InlineData("a; DROP TABLE x", "';'")]
    public void ReadNewColumn_RejectsWhatCannotBeAColumn(string text, string expectedInMessage)
    {
        var result = TsqlModules.ReadNewColumn(text);

        Assert.False(result.Ok);
        Assert.Contains(expectedInMessage, result.Error);
    }

    [Theory]
    [InlineData("SELECT a FROM t", null)]
    [InlineData("  WITH x AS (SELECT 1 AS a) SELECT a FROM x", null)]
    [InlineData("", "Informe")]
    [InlineData("CREATE VIEW v AS SELECT 1", "apenas o SELECT")]
    [InlineData("DROP TABLE x", "começar com SELECT")]
    [InlineData("SELECT (a FROM t", "Parênteses")]
    [InlineData("SELECT a\nGO\nSELECT b", "GO")]
    public void ValidateSelectStatement(string text, string? expectedInMessage)
    {
        var error = TsqlModules.ValidateSelectStatement(text);

        if (expectedInMessage is null)
            Assert.Null(error);
        else
            Assert.Contains(expectedInMessage, error);
    }

    [Fact]
    public void ReadTriggerTarget_FindsTheTable()
    {
        var target = TsqlModules.ReadTriggerTarget("""
            CREATE TRIGGER [dbo].[trg_Cliente] ON [vendas].[Cliente]
            AFTER INSERT AS BEGIN SELECT 1 END
            """);

        Assert.True(target.Ok, target.Error);
        Assert.Equal("vendas", target.Value!.Schema);
        Assert.Equal("Cliente", target.Value.Name);
    }

    [Fact]
    public void ModuleName_MatchesIgnoringCaseAndMissingSchema()
    {
        Assert.True(new ModuleName("DBO", "Trg").Matches("dbo", " trg "));
        Assert.True(new ModuleName(null, "Trg").Matches("qualquer", "TRG"));
        Assert.False(new ModuleName("vendas", "Trg").Matches("dbo", "Trg"));
    }

    [Fact]
    public void Lexer_HandlesNestedCommentsAndEscapedQuotes()
    {
        var tokens = TsqlLexer.Tokenize("a /* x /* y */ z */ b 'it''s' [x]]y] -- fim", out var balanced);

        Assert.True(balanced);
        Assert.Equal(["a", "b", "'it''s'", "[x]]y]"], tokens.Select(t => t.Text));
        Assert.Equal("x]y", tokens[3].Unquoted);
    }
}
