using SqlTools.Core.Model;
using SqlTools.Core.Requests;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Generation;

public sealed partial class SqlServerScriptGenerator
{
    private static GuardedScript BuildAddPrimaryKey(AddPrimaryKeyRequest r)
    {
        var t = r.QualifiedTable;
        var name = r.GetConstraintName();

        var script = new GuardedScript
            {
                SuccessMessage = $"Chave primária {name} criada em {r.TableDisplay}.",
                Action = w =>
                {
                    // SQL dinâmico: o SQL Server rejeita PK sobre coluna NULL já na compilação do lote,
                    // o que impediria as verificações acima de executarem.
                    var sql = $"ALTER TABLE {t} ADD CONSTRAINT {SqlText.Quote(name)} PRIMARY KEY " +
                              $"{(r.Clustered ? "CLUSTERED" : "NONCLUSTERED")} ({SqlText.QuoteList(r.Columns)});";
                    w.Line($"EXEC sp_executesql {SqlText.Literal(sql)};");
                },
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Skip(Catalog.ConstraintExists(t, name, "PK"), $"Chave primária {name} já existe. Nenhuma alteração realizada.")
            .Error(Catalog.TableHasPrimaryKey(t), $"Tabela {r.TableDisplay} já possui outra chave primária.")
            .Error(Catalog.ObjectExists(r.QualifiedInSchema(name)), NameInUseMessage(r, name));

        AddColumnsMustExist(script, r, t, r.Columns);
        foreach (var column in r.Columns)
            script.Error(Catalog.ColumnAllowsNull(t, column),
                $"Coluna {r.TableDisplay}.{column.Trim()} aceita NULL: altere-a para NOT NULL antes de criar a PK.");

        if (r.Clustered)
            script.Error(Catalog.TableHasClusteredIndex(t),
                $"Tabela {r.TableDisplay} já possui índice CLUSTERED: use uma PK NONCLUSTERED.");

        return script;
    }

    private static GuardedScript BuildAddForeignKey(AddForeignKeyRequest r)
    {
        var t = r.QualifiedTable;
        var rt = r.QualifiedReferencedTable;
        var name = r.GetConstraintName();

        var script = new GuardedScript
            {
                SuccessMessage = $"Chave estrangeira {name} criada em {r.TableDisplay}.",
                Action = w =>
                {
                    w.Line($"ALTER TABLE {t} WITH {(r.CheckExistingData ? "CHECK" : "NOCHECK")} " +
                           $"ADD CONSTRAINT {SqlText.Quote(name)} FOREIGN KEY ({SqlText.QuoteList(r.Columns)})");
                    w.Indented(() =>
                    {
                        var references = $"REFERENCES {rt} ({SqlText.QuoteList(r.ReferencedColumns)})";
                        var actions = new List<string>();
                        if (r.OnDelete != ReferentialAction.NoAction) actions.Add($"ON DELETE {r.OnDelete.ToSql()}");
                        if (r.OnUpdate != ReferentialAction.NoAction) actions.Add($"ON UPDATE {r.OnUpdate.ToSql()}");

                        if (actions.Count == 0)
                        {
                            w.Line(references + ";");
                        }
                        else
                        {
                            w.Line(references);
                            for (var i = 0; i < actions.Count; i++)
                                w.Line(actions[i] + (i == actions.Count - 1 ? ";" : ""));
                        }
                    });
                },
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Error(Catalog.TableMissing(rt), $"Tabela referenciada {r.ReferencedDisplay} não existe.")
            .Skip(Catalog.ConstraintExists(t, name, "F"), $"Chave estrangeira {name} já existe. Nenhuma alteração realizada.")
            .Error(Catalog.ObjectExists(r.QualifiedInSchema(name)), NameInUseMessage(r, name));

        AddColumnsMustExist(script, r, t, r.Columns);
        foreach (var column in r.ReferencedColumns)
            script.Error(Catalog.ColumnMissing(rt, column), $"Coluna {r.ReferencedDisplay}.{column.Trim()} não existe.");

        script.Error(Catalog.NoUniqueKeyOn(rt, r.ReferencedColumns),
            $"Colunas ({ColumnList.Format(r.ReferencedColumns)}) de {r.ReferencedDisplay} não formam PK/UNIQUE: " +
            "a FK precisa referenciar uma chave única.");

        return script;
    }

    private static GuardedScript BuildAddUnique(AddUniqueRequest r)
    {
        var t = r.QualifiedTable;
        var name = r.GetConstraintName();

        var script = new GuardedScript
            {
                SuccessMessage = $"Constraint UNIQUE {name} criada em {r.TableDisplay}.",
                Action = w => w.Line($"ALTER TABLE {t} ADD CONSTRAINT {SqlText.Quote(name)} UNIQUE " +
                                     $"{(r.Clustered ? "CLUSTERED" : "NONCLUSTERED")} ({SqlText.QuoteList(r.Columns)});"),
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Skip(Catalog.ConstraintExists(t, name, "UQ"), $"Constraint UNIQUE {name} já existe. Nenhuma alteração realizada.")
            .Error(Catalog.ObjectExists(r.QualifiedInSchema(name)), NameInUseMessage(r, name));

        AddColumnsMustExist(script, r, t, r.Columns);
        if (r.Clustered)
            script.Error(Catalog.TableHasClusteredIndex(t), $"Tabela {r.TableDisplay} já possui índice CLUSTERED.");

        return script;
    }

    private static GuardedScript BuildAddCheck(AddCheckRequest r)
    {
        var t = r.QualifiedTable;
        var name = r.ConstraintName.Trim();

        return new GuardedScript
            {
                SuccessMessage = $"Constraint CHECK {name} criada em {r.TableDisplay}.",
                Action = w => w.Line($"ALTER TABLE {t} WITH {(r.CheckExistingData ? "CHECK" : "NOCHECK")} " +
                                     $"ADD CONSTRAINT {SqlText.Quote(name)} CHECK ({r.Expression.Trim()});"),
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Skip(Catalog.ConstraintExists(t, name, "C"), $"Constraint CHECK {name} já existe. Nenhuma alteração realizada.")
            .Error(Catalog.ObjectExists(r.QualifiedInSchema(name)), NameInUseMessage(r, name));
    }

    private static GuardedScript BuildAddDefault(AddDefaultRequest r)
    {
        var t = r.QualifiedTable;
        var name = r.GetConstraintName();
        var column = r.ColumnName.Trim();

        return new GuardedScript
            {
                SuccessMessage = $"Constraint DEFAULT {name} criada em {r.TableDisplay}.{column}.",
                Action = w => w.Line($"ALTER TABLE {t} ADD CONSTRAINT {SqlText.Quote(name)} " +
                                     $"DEFAULT ({r.Expression.Trim()}) FOR {SqlText.Quote(column)};"),
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Error(Catalog.ColumnMissing(t, column), $"Coluna {r.TableDisplay}.{column} não existe.")
            .Skip(Catalog.ConstraintExists(t, name, "D"), $"Constraint DEFAULT {name} já existe. Nenhuma alteração realizada.")
            .Error(Catalog.ColumnHasDefault(t, column), $"Coluna {r.TableDisplay}.{column} já possui outra constraint DEFAULT.")
            .Error(Catalog.ObjectExists(r.QualifiedInSchema(name)), NameInUseMessage(r, name));
    }

    private static GuardedScript BuildDropConstraint(DropConstraintRequest r)
    {
        var t = r.QualifiedTable;
        var name = r.ConstraintName.Trim();
        string[] types = ["PK", "UQ", "F", "C", "D"];

        return new GuardedScript
            {
                SuccessMessage = $"Constraint {name} removida de {r.TableDisplay}.",
                Action = w => w.Line($"ALTER TABLE {t} DROP CONSTRAINT {SqlText.Quote(name)};"),
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Skip(Catalog.ConstraintMissing(t, name, types),
                $"Constraint {name} não existe em {r.TableDisplay}. Nenhuma alteração realizada.")
            .Error(Catalog.KeyReferencedByForeignKey(t, name),
                $"Constraint {name} é referenciada por chaves estrangeiras. Remova-as antes.");
    }

    private static void AddColumnsMustExist(GuardedScript script, ScriptRequest r, string table, IEnumerable<string> columns)
    {
        foreach (var column in columns)
            script.Error(Catalog.ColumnMissing(table, column), $"Coluna {r.TableDisplay}.{column.Trim()} não existe.");
    }
}
