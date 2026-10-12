using SqlTools.Core.Sql;

namespace SqlTools.Core.Generation;

/// <summary>
/// Condições T-SQL que consultam o catálogo do SQL Server.
/// Todas usam funções de metadados ou views sys.*, portanto compilam mesmo
/// quando a tabela/coluna ainda não existe.
/// </summary>
internal static class Catalog
{
    /// <param name="table">Nome qualificado e com colchetes, ex.: [dbo].[Cliente].</param>
    public static string TableExists(string table) => $"OBJECT_ID({SqlText.Literal(table)}, N'U') IS NOT NULL";

    public static string TableMissing(string table) => $"OBJECT_ID({SqlText.Literal(table)}, N'U') IS NULL";

    public static string ObjectExists(string qualifiedName) => $"OBJECT_ID({SqlText.Literal(qualifiedName)}) IS NOT NULL";

    public static string ViewExists(string view) => $"OBJECT_ID({SqlText.Literal(view)}, N'V') IS NOT NULL";

    public static string ViewMissing(string view) => $"OBJECT_ID({SqlText.Literal(view)}, N'V') IS NULL";

    public static string TriggerExists(string trigger) => $"OBJECT_ID({SqlText.Literal(trigger)}, N'TR') IS NOT NULL";

    public static string TriggerMissing(string trigger) => $"OBJECT_ID({SqlText.Literal(trigger)}, N'TR') IS NULL";

    /// <summary>Existe um objeto com esse nome, mas de outro tipo (ex.: uma tabela com o nome da view).</summary>
    public static string ObjectOfOtherType(string name, string type) =>
        $"OBJECT_ID({SqlText.Literal(name)}) IS NOT NULL AND OBJECT_ID({SqlText.Literal(name)}, N'{type}') IS NULL";

    /// <summary>Alvo de uma trigger: tabela ou view.</summary>
    public static string TableOrViewMissing(string name) =>
        $"OBJECT_ID({SqlText.Literal(name)}, N'U') IS NULL AND OBJECT_ID({SqlText.Literal(name)}, N'V') IS NULL";

    /// <summary>A trigger com esse nome existe, mas pertence a outra tabela/view.</summary>
    public static string TriggerOnOtherParent(string trigger, string parent) =>
        $"EXISTS (SELECT 1 FROM sys.triggers WHERE object_id = OBJECT_ID({SqlText.Literal(trigger)}, N'TR') AND parent_id <> OBJECT_ID({SqlText.Literal(parent)}))";

    public static string SchemaMissing(string schema) => $"SCHEMA_ID({SqlText.Literal(schema.Trim())}) IS NULL";

    public static string ColumnExists(string table, string column) =>
        $"COL_LENGTH({SqlText.Literal(table)}, {SqlText.Literal(column.Trim())}) IS NOT NULL";

    public static string ColumnMissing(string table, string column) =>
        $"COL_LENGTH({SqlText.Literal(table)}, {SqlText.Literal(column.Trim())}) IS NULL";

    public static string ColumnAllowsNull(string table, string column) =>
        $"COLUMNPROPERTY(OBJECT_ID({SqlText.Literal(table)}), {SqlText.Literal(column.Trim())}, 'AllowsNull') = 1";

    public static string TableHasIdentity(string table) =>
        $"OBJECTPROPERTY(OBJECT_ID({SqlText.Literal(table)}), 'TableHasIdentity') = 1";

    public static string TableHasRows(string table) => $"EXISTS (SELECT 1 FROM {table})";

    public static string TableHasClusteredIndex(string table) =>
        $"EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({SqlText.Literal(table)}) AND type = 1)";

    public static string TableHasPrimaryKey(string table) =>
        $"EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID({SqlText.Literal(table)}) AND type = 'PK')";

    /// <summary>Constraint com o nome informado, na tabela informada, de um dos tipos (PK, UQ, F, C, D).</summary>
    public static string ConstraintExists(string table, string name, params string[] types) =>
        $"EXISTS (SELECT 1 FROM sys.objects WHERE name = {SqlText.Literal(name.Trim())} " +
        $"AND parent_object_id = OBJECT_ID({SqlText.Literal(table)}) " +
        $"AND type IN ({string.Join(", ", types.Select(t => $"'{t}'"))}))";

    public static string ConstraintMissing(string table, string name, params string[] types) =>
        "NOT " + ConstraintExists(table, name, types);

    public static string IndexExists(string table, string name) =>
        $"EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({SqlText.Literal(table)}) AND name = {SqlText.Literal(name.Trim())})";

    public static string IndexMissing(string table, string name) => "NOT " + IndexExists(table, name);

    public static string IndexBacksConstraint(string table, string name) =>
        $"EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({SqlText.Literal(table)}) " +
        $"AND name = {SqlText.Literal(name.Trim())} AND (is_primary_key = 1 OR is_unique_constraint = 1))";

    public static string ColumnHasDefault(string table, string column) =>
        $"""
        EXISTS (
            SELECT 1
            FROM sys.default_constraints dc
            JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
            WHERE dc.parent_object_id = OBJECT_ID({SqlText.Literal(table)}) AND c.name = {SqlText.Literal(column.Trim())}
        )
        """;

    public static string ColumnUsedInIndex(string table, string column) =>
        $"""
        EXISTS (
            SELECT 1
            FROM sys.index_columns ic
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = OBJECT_ID({SqlText.Literal(table)}) AND c.name = {SqlText.Literal(column.Trim())}
        )
        """;

    public static string ColumnUsedInForeignKey(string table, string column) =>
        $"""
        EXISTS (
            SELECT 1
            FROM sys.foreign_key_columns fkc
            JOIN sys.columns c
                ON (c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id)
                OR (c.object_id = fkc.referenced_object_id AND c.column_id = fkc.referenced_column_id)
            WHERE c.object_id = OBJECT_ID({SqlText.Literal(table)}) AND c.name = {SqlText.Literal(column.Trim())}
        )
        """;

    public static string TableReferencedByOtherTables(string table) =>
        $"""
        EXISTS (
            SELECT 1
            FROM sys.foreign_keys
            WHERE referenced_object_id = OBJECT_ID({SqlText.Literal(table)})
              AND parent_object_id <> referenced_object_id
        )
        """;

    public static string KeyReferencedByForeignKey(string table, string constraint) =>
        $"""
        EXISTS (
            SELECT 1
            FROM sys.foreign_keys fk
            JOIN sys.key_constraints kc
                ON kc.parent_object_id = fk.referenced_object_id AND kc.unique_index_id = fk.key_index_id
            WHERE kc.parent_object_id = OBJECT_ID({SqlText.Literal(table)}) AND kc.name = {SqlText.Literal(constraint.Trim())}
        )
        """;

    /// <summary>
    /// Verdadeiro quando NÃO existe PK/UNIQUE (índice único sem filtro) cujas colunas-chave
    /// sejam exatamente as colunas informadas — requisito para ser alvo de uma FK.
    /// </summary>
    public static string NoUniqueKeyOn(string table, IReadOnlyCollection<string> columns)
    {
        var names = string.Join(", ", columns.Select(c => SqlText.Literal(c.Trim())));
        return $"""
            NOT EXISTS (
                SELECT 1
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID({SqlText.Literal(table)})
                  AND i.is_unique = 1
                  AND i.has_filter = 0
                  AND (SELECT COUNT(*) FROM sys.index_columns ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) = {columns.Count}
                  AND (SELECT COUNT(*) FROM sys.index_columns ic
                       JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                         AND c.name IN ({names})) = {columns.Count}
            )
            """;
    }
}
