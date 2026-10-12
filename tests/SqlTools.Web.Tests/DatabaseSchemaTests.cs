using SqlTools.Web.Services;

namespace SqlTools.Web.Tests;

public class DatabaseSchemaTests
{
    private static DatabaseSchema Build(int tables)
    {
        var schema = new DatabaseSchema { Server = "srv", Database = "erp" };
        for (var i = 1; i <= tables; i++)
            schema.Add(new TableInfo { ObjectId = i, Schema = i % 10 == 0 ? "fin" : "dbo", Name = $"TB_{i:0000}" });
        return schema;
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndTrimsInput()
    {
        var schema = Build(20);

        Assert.Equal(7, schema.Find(" DBO ", " tb_0007 ")?.ObjectId);
        Assert.Equal(10, schema.Find("fin", "TB_0010")?.ObjectId);
    }

    [Theory]
    [InlineData("dbo", "TB_0010")] // existe, mas em outro schema
    [InlineData("dbo", "NaoExiste")]
    [InlineData("", "TB_0001")]
    [InlineData("dbo", "")]
    [InlineData(null, null)]
    public void Find_ReturnsNullWhenNotFoundOrIncomplete(string? schema, string? table)
    {
        Assert.Null(Build(20).Find(schema, table));
    }

    [Fact]
    public void SearchTables_NeverReturnsMoreThanTheLimit()
    {
        var schema = Build(5000);

        Assert.Equal(50, schema.SearchTables(null, "", 50).Count);
        Assert.Equal(50, schema.SearchTables("dbo", "TB_", 50).Count);
    }

    [Fact]
    public void SearchTables_FiltersBySchemaAndText()
    {
        var schema = Build(100);

        var result = schema.SearchTables("fin", "tb_00", 50);

        Assert.Equal(["TB_0010", "TB_0020", "TB_0030", "TB_0040", "TB_0050", "TB_0060", "TB_0070", "TB_0080", "TB_0090"], result);
    }

    [Fact]
    public void Table_DetailsStartUnloaded_AndLoadOnce()
    {
        var table = new TableInfo { ObjectId = 1, Schema = "dbo", Name = "T" };
        Assert.False(table.DetailsLoaded);
        Assert.Empty(table.Columns);

        table.SetDetails([new ColumnInfo("Id", "INT", 4, 10, 0, false, true)], [new NamedObject("PK_T", "PK")], []);

        Assert.True(table.DetailsLoaded);
        Assert.Single(table.Columns);
        Assert.Equal("PK_T", table.Constraints[0].Name);
    }
}
