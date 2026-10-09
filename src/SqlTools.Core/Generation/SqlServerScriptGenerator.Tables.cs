using SqlTools.Core.Model;
using SqlTools.Core.Requests;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Generation;

public sealed partial class SqlServerScriptGenerator
{
    private static GuardedScript BuildCreateTable(CreateTableRequest r)
    {
        var t = r.QualifiedTable;
        var isDefaultSchema = r.Schema.Trim().Equals("dbo", StringComparison.OrdinalIgnoreCase);
        var createSchema = r.CreateSchemaIfMissing && !isDefaultSchema;

        var script = new GuardedScript
        {
            SuccessMessage = $"Tabela {r.TableDisplay} criada.",
            Action = w =>
            {
                if (createSchema)
                {
                    w.Line($"IF {Catalog.SchemaMissing(r.Schema)}");
                    w.Begin();
                    w.Line($"EXEC({SqlText.Literal($"CREATE SCHEMA {SqlText.Quote(r.Schema)}")});");
                    w.Line($"PRINT {SqlText.Literal($"Schema {r.Schema.Trim()} criado.")};");
                    w.End();
                    w.Line();
                }

                var lines = r.Columns.Select(c => c.ToDefinitionSql(r.Table)).ToList();
                if (r.PrimaryKeyColumns.Count > 0)
                    lines.Add($"CONSTRAINT {SqlText.Quote(r.GetPrimaryKeyName())} PRIMARY KEY " +
                              $"{(r.PrimaryKeyClustered ? "CLUSTERED" : "NONCLUSTERED")} ({SqlText.QuoteList(r.PrimaryKeyColumns)})");

                w.Line($"CREATE TABLE {t}");
                w.Line("(");
                w.Indented(() =>
                {
                    for (var i = 0; i < lines.Count; i++)
                        w.Line(lines[i] + (i < lines.Count - 1 ? "," : ""));
                });
                w.Line(");");
            },
        };

        if (!createSchema)
            script.Error(Catalog.SchemaMissing(r.Schema), $"Schema {r.Schema.Trim()} não existe.");

        script
            .Skip(Catalog.TableExists(t), $"Tabela {r.TableDisplay} já existe. Nenhuma alteração realizada.")
            .Error(Catalog.ObjectExists(t), $"Já existe um objeto que não é tabela chamado {r.TableDisplay}.");

        var constraintNames = r.Columns.Where(c => c.HasDefault).Select(c => c.GetDefaultConstraintName(r.Table)).ToList();
        if (r.PrimaryKeyColumns.Count > 0)
            constraintNames.Insert(0, r.GetPrimaryKeyName());
        foreach (var name in constraintNames)
            script.Error(Catalog.ObjectExists(r.QualifiedInSchema(name)), NameInUseMessage(r, name));

        return script;
    }

    private static GuardedScript BuildDropTable(DropTableRequest r)
    {
        var t = r.QualifiedTable;
        return new GuardedScript
            {
                SuccessMessage = $"Tabela {r.TableDisplay} excluída.",
                Action = w => w.Line($"DROP TABLE {t};"),
            }
            .Skip(Catalog.TableMissing(t), $"Tabela {r.TableDisplay} não existe. Nenhuma alteração realizada.")
            .Error(Catalog.TableReferencedByOtherTables(t),
                $"Tabela {r.TableDisplay} é referenciada por chaves estrangeiras de outras tabelas. Remova-as antes.");
    }

    private static GuardedScript BuildAddColumn(AddColumnRequest r)
    {
        var t = r.QualifiedTable;
        var c = r.Column;
        var column = $"{r.TableDisplay}.{c.Name.Trim()}";

        var script = new GuardedScript
            {
                SuccessMessage = $"Coluna {column} adicionada.",
                Action = w =>
                {
                    var withValues = r.FillExistingRowsWithDefault && c.HasDefault && c.IsNullable ? " WITH VALUES" : "";
                    w.Line($"ALTER TABLE {t} ADD {c.ToDefinitionSql(r.Table)}{withValues};");
                },
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Skip(Catalog.ColumnExists(t, c.Name), $"Coluna {column} já existe. Nenhuma alteração realizada.");

        var isRowVersion = c.DataType.Equals("ROWVERSION", StringComparison.OrdinalIgnoreCase);
        if (!c.IsNullable && !c.HasDefault && !c.IsIdentity && !isRowVersion)
            script.Error(Catalog.TableHasRows(t),
                $"Tabela {r.TableDisplay} possui registros: coluna NOT NULL sem valor padrão não pode ser adicionada.");

        if (c.IsIdentity)
            script.Error(Catalog.TableHasIdentity(t), $"Tabela {r.TableDisplay} já possui uma coluna IDENTITY.");

        if (c.HasDefault)
        {
            var dfName = c.GetDefaultConstraintName(r.Table);
            script.Error(Catalog.ObjectExists(r.QualifiedInSchema(dfName)), NameInUseMessage(r, dfName));
        }

        return script;
    }

    private static GuardedScript BuildAlterColumn(AlterColumnRequest r)
    {
        var t = r.QualifiedTable;
        var c = r.Column;
        var column = $"{r.TableDisplay}.{c.Name.Trim()}";

        var script = new GuardedScript
            {
                SuccessMessage = $"Coluna {column} alterada para {c.FormatType()} {(c.IsNullable ? "NULL" : "NOT NULL")}.",
                Action = w => w.Line($"ALTER TABLE {t} ALTER COLUMN {c.ToDefinitionSql(r.Table, includeIdentityAndDefault: false)};"),
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Error(Catalog.ColumnMissing(t, c.Name), $"Coluna {column} não existe.");

        if (!c.IsNullable)
        {
            // A checagem de NULLs usa SQL dinâmico: referenciar diretamente uma coluna inexistente
            // em uma tabela existente faria o lote inteiro falhar na compilação.
            var check = $"IF EXISTS (SELECT 1 FROM {t} WHERE {SqlText.Quote(c.Name)} IS NULL) SET @Result = 1;";
            script.Prelude.Add("DECLARE @HasNulls BIT = 0;");
            script.Prelude.Add($"IF {Catalog.ColumnExists(t, c.Name)}");
            script.Prelude.Add($"    EXEC sp_executesql {SqlText.Literal(check)}, N'@Result BIT OUTPUT', @Result = @HasNulls OUTPUT;");
            script.Error("@HasNulls = 1", $"Coluna {column} possui valores NULL: atualize os dados antes de torná-la NOT NULL.");
        }

        return script;
    }

    private static GuardedScript BuildRenameColumn(RenameColumnRequest r)
    {
        var t = r.QualifiedTable;
        var oldName = r.ColumnName.Trim();
        var newName = r.NewName.Trim();
        var objectName = $"{t}.{SqlText.Quote(oldName)}";

        return new GuardedScript
            {
                SuccessMessage = $"Coluna {r.TableDisplay}.{oldName} renomeada para {newName}.",
                Action = w => w.Line($"EXEC sp_rename {SqlText.Literal(objectName)}, {SqlText.Literal(newName)}, N'COLUMN';"),
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Skip($"{Catalog.ColumnMissing(t, oldName)} AND {Catalog.ColumnExists(t, newName)}",
                $"Coluna {r.TableDisplay}.{oldName} já foi renomeada para {newName}. Nenhuma alteração realizada.")
            .Error(Catalog.ColumnMissing(t, oldName), $"Coluna {r.TableDisplay}.{oldName} não existe.")
            .Error(Catalog.ColumnExists(t, newName), $"Já existe uma coluna {r.TableDisplay}.{newName}.");
    }

    private static GuardedScript BuildDropColumn(DropColumnRequest r)
    {
        var t = r.QualifiedTable;
        var name = r.ColumnName.Trim();
        var column = $"{r.TableDisplay}.{name}";

        var script = new GuardedScript
            {
                SuccessMessage = $"Coluna {column} excluída.",
                Action = w =>
                {
                    if (r.DropDefaultConstraint)
                    {
                        w.Line("DECLARE @DefaultConstraint SYSNAME, @Sql NVARCHAR(MAX);");
                        w.Line("SELECT @DefaultConstraint = dc.name");
                        w.Line("FROM sys.default_constraints dc");
                        w.Line("JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id");
                        w.Line($"WHERE dc.parent_object_id = OBJECT_ID({SqlText.Literal(t)}) AND c.name = {SqlText.Literal(name)};");
                        w.Line();
                        w.Line("IF @DefaultConstraint IS NOT NULL");
                        w.Begin();
                        w.Line($"SET @Sql = {SqlText.Literal($"ALTER TABLE {t} DROP CONSTRAINT ")} + QUOTENAME(@DefaultConstraint) + N';';");
                        w.Line("EXEC sp_executesql @Sql;");
                        w.Line("PRINT N'Constraint default ' + @DefaultConstraint + N' removida.';");
                        w.End();
                        w.Line();
                    }
                    w.Line($"ALTER TABLE {t} DROP COLUMN {SqlText.Quote(name)};");
                },
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Skip(Catalog.ColumnMissing(t, name), $"Coluna {column} não existe. Nenhuma alteração realizada.")
            .Error(Catalog.ColumnUsedInIndex(t, name), $"Coluna {column} é usada em índice ou PK/UNIQUE. Remova-os antes.")
            .Error(Catalog.ColumnUsedInForeignKey(t, name), $"Coluna {column} é usada em chave estrangeira. Remova-a antes.");

        if (!r.DropDefaultConstraint)
            script.Error(Catalog.ColumnHasDefault(t, name), $"Coluna {column} possui constraint DEFAULT. Remova-a antes.");

        return script;
    }
}
