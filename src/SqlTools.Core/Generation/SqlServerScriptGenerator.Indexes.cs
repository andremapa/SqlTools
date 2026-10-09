using SqlTools.Core.Model;
using SqlTools.Core.Requests;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Generation;

public sealed partial class SqlServerScriptGenerator
{
    private static GuardedScript BuildCreateIndex(CreateIndexRequest r)
    {
        var t = r.QualifiedTable;
        var name = r.GetIndexName();

        var script = new GuardedScript
            {
                SuccessMessage = $"Índice {name} criado em {r.TableDisplay}.",
                Action = w =>
                {
                    var keys = string.Join(", ", r.Columns.Select(c => $"{SqlText.Quote(c.Name)} {c.Direction.ToSql()}"));
                    w.Line($"CREATE {(r.IsUnique ? "UNIQUE " : "")}{(r.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} " +
                           $"INDEX {SqlText.Quote(name)} ON {t} ({keys})" +
                           (r.IncludedColumns.Count == 0 && string.IsNullOrWhiteSpace(r.Filter) ? ";" : ""));
                    w.Indented(() =>
                    {
                        var hasFilter = !string.IsNullOrWhiteSpace(r.Filter);
                        if (r.IncludedColumns.Count > 0)
                            w.Line($"INCLUDE ({SqlText.QuoteList(r.IncludedColumns)})" + (hasFilter ? "" : ";"));
                        if (hasFilter)
                            w.Line($"WHERE {r.Filter!.Trim()};");
                    });
                },
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Skip(Catalog.IndexExists(t, name), $"Índice {name} já existe em {r.TableDisplay}. Nenhuma alteração realizada.");

        AddColumnsMustExist(script, r, t, r.Columns.Select(c => c.Name).Concat(r.IncludedColumns));
        if (r.IsClustered)
            script.Error(Catalog.TableHasClusteredIndex(t), $"Tabela {r.TableDisplay} já possui índice CLUSTERED.");

        return script;
    }

    private static GuardedScript BuildDropIndex(DropIndexRequest r)
    {
        var t = r.QualifiedTable;
        var name = r.IndexName.Trim();

        return new GuardedScript
            {
                SuccessMessage = $"Índice {name} removido de {r.TableDisplay}.",
                Action = w => w.Line($"DROP INDEX {SqlText.Quote(name)} ON {t};"),
            }
            .Error(Catalog.TableMissing(t), TableMissingMessage(r))
            .Skip(Catalog.IndexMissing(t, name), $"Índice {name} não existe em {r.TableDisplay}. Nenhuma alteração realizada.")
            .Error(Catalog.IndexBacksConstraint(t, name),
                $"Índice {name} pertence a uma PK/UNIQUE: use 'Excluir constraint'.");
    }
}
