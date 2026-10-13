using SqlTools.Core.Generation;
using SqlTools.Core.Model;
using SqlTools.Core.Requests;

namespace SqlTools.Core.Tests;

/// <summary>Quais tipos aceitam IDENTITY (autoincremento) e como a coluna se mantém coerente quando o tipo muda.</summary>
public class ColumnIdentityTests
{
    [Theory]
    [InlineData("INT")]
    [InlineData("BIGINT")]
    [InlineData("SMALLINT")]
    [InlineData("TINYINT")]
    [InlineData("int")] // o nome do tipo não diferencia maiúsculas
    public void IntegerTypes_AllowIdentity(string type)
    {
        Assert.True(new ColumnDefinition { DataType = type }.CanBeIdentity);
    }

    [Theory]
    [InlineData("DECIMAL")]
    [InlineData("NUMERIC")]
    public void DecimalAndNumeric_AllowIdentityOnlyWithScaleZero(string type)
    {
        Assert.False(new ColumnDefinition { DataType = type, Scale = 2 }.CanBeIdentity);
        Assert.False(new ColumnDefinition { DataType = type, Scale = 1 }.CanBeIdentity);
        Assert.True(new ColumnDefinition { DataType = type, Scale = 0 }.CanBeIdentity);
        Assert.True(new ColumnDefinition { DataType = type, Scale = null }.CanBeIdentity); // sem escala = 0
    }

    [Theory]
    [InlineData("BIT")]
    [InlineData("MONEY")]
    [InlineData("SMALLMONEY")]
    [InlineData("FLOAT")]
    [InlineData("REAL")]
    [InlineData("VARCHAR")]
    [InlineData("NVARCHAR")]
    [InlineData("CHAR")]
    [InlineData("NCHAR")]
    [InlineData("DATE")]
    [InlineData("DATETIME")]
    [InlineData("DATETIME2")]
    [InlineData("SMALLDATETIME")]
    [InlineData("DATETIMEOFFSET")]
    [InlineData("TIME")]
    [InlineData("UNIQUEIDENTIFIER")]
    [InlineData("VARBINARY")]
    [InlineData("BINARY")]
    [InlineData("XML")]
    [InlineData("ROWVERSION")]
    [InlineData("SQL_VARIANT")]
    public void EveryOtherType_DoesNotAllowIdentity(string type)
    {
        var column = new ColumnDefinition { DataType = type };

        Assert.False(column.CanBeIdentity);
        Assert.NotNull(column.IdentityUnavailableReason);
    }

    [Fact]
    public void EveryTypeInTheCatalogIsCoveredByTheRule()
    {
        // Se alguém acrescentar um tipo ao catálogo, a regra continua coerente com o que o catálogo declara.
        foreach (var type in SqlTypeCatalog.All)
        {
            var column = new ColumnDefinition { DataType = type.Name, Scale = 0 };
            Assert.Equal(type.SupportsIdentity, column.CanBeIdentity);
        }
    }

    [Fact]
    public void UnknownType_DoesNotAllowIdentity()
    {
        Assert.False(new ColumnDefinition { DataType = "TIPO_X" }.CanBeIdentity);
    }

    [Fact]
    public void TheReason_ExplainsWhatToDo()
    {
        Assert.Null(new ColumnDefinition { DataType = "INT" }.IdentityUnavailableReason);
        Assert.Contains("escala 0", new ColumnDefinition { DataType = "DECIMAL", Scale = 2 }.IdentityUnavailableReason);
        Assert.Contains("INT, BIGINT", new ColumnDefinition { DataType = "VARCHAR" }.IdentityUnavailableReason);
    }

    // ------------------------------------------------------------------ a coluna acompanha o tipo

    [Fact]
    public void ChangingToATypeWithoutIdentity_TurnsIdentityOff()
    {
        var column = new ColumnDefinition { DataType = "INT", IsIdentity = true, IsNullable = false };

        column.DataType = "NVARCHAR";

        Assert.False(column.IsIdentity);
    }

    [Fact]
    public void ChangingBetweenTypesThatAllowIdentity_KeepsIt()
    {
        var column = new ColumnDefinition { DataType = "INT", IsIdentity = true, IsNullable = false };

        column.DataType = "BIGINT";

        Assert.True(column.IsIdentity);
    }

    [Fact]
    public void ChangingTheScaleOfADecimal_TurnsIdentityOffWhenItStopsBeingZero()
    {
        var column = new ColumnDefinition { DataType = "DECIMAL", Precision = 10, Scale = 0, IsIdentity = true, IsNullable = false };
        Assert.True(column.IsIdentity);

        column.Scale = 2;

        Assert.False(column.IsIdentity);
    }

    [Fact]
    public void TurningTheTypeBackDoesNotTurnIdentityOnAgain()
    {
        var column = new ColumnDefinition { DataType = "INT", IsIdentity = true, IsNullable = false };
        column.DataType = "VARCHAR";

        column.DataType = "INT";

        Assert.False(column.IsIdentity); // é o usuário quem liga de novo
    }

    [Fact]
    public void SettingIdentityOnATypeThatDoesNotAllowIt_StillFailsValidation()
    {
        var column = new ColumnDefinition { Name = "C", DataType = "VARCHAR", IsIdentity = true, IsNullable = false };

        Assert.Contains(column.Validate(), e => e.Contains("IDENTITY"));
    }

    [Fact]
    public void ADecimalWithScaleGreaterThanZero_FailsValidationWhenIdentityIsForced()
    {
        var column = new ColumnDefinition { Name = "C", DataType = "DECIMAL", Precision = 10, Scale = 2, IsIdentity = true, IsNullable = false };

        Assert.Contains(column.Validate(), e => e.Contains("escala 0"));
    }

    // ------------------------------------------------------------------ cópia (usada pelo lote)

    [Theory]
    [InlineData("BIGINT", null)]
    [InlineData("DECIMAL", 0)]
    public void Clone_KeepsAValidIdentityColumn(string type, int? scale)
    {
        var request = new AddColumnRequest
        {
            Table = "T",
            Column = new() { Name = "Id", DataType = type, Scale = scale, IsIdentity = true, IsNullable = false },
        };

        var clone = (AddColumnRequest)request.Clone();

        Assert.True(clone.Column.IsIdentity);
        Assert.Equal(request.Column.FormatType(), clone.Column.FormatType());
    }

    [Fact]
    public void Clone_DoesNotInventIdentity_ForAColumnWithoutIt()
    {
        var clone = (AddColumnRequest)new AddColumnRequest { Table = "T", Column = new() { Name = "Nome", DataType = "VARCHAR", Length = 10 } }.Clone();

        Assert.False(clone.Column.IsIdentity);
        Assert.Equal("VARCHAR", clone.Column.DataType);
    }

    // ------------------------------------------------------------------ script

    [Fact]
    public void TheScript_EmitsIdentityOnlyForAColumnThatAllowsIt()
    {
        var generator = new SqlServerScriptGenerator();
        var column = new ColumnDefinition { Name = "Id", DataType = "BIGINT", IsIdentity = true, IsNullable = false };

        var withIdentity = generator.Generate(new CreateTableRequest { Table = "T", Columns = [column] }, new ScriptOptions()).Script;
        column.DataType = "NVARCHAR"; // a tela faria isto ao trocar o tipo
        var afterChange = generator.Generate(new CreateTableRequest { Table = "T", Columns = [column] }, new ScriptOptions()).Script;

        Assert.Contains("[Id] BIGINT IDENTITY(1,1) NOT NULL", withIdentity);
        Assert.DoesNotContain("IDENTITY", afterChange);
    }
}
