namespace SqlTools.Web.Components.Layout;

public sealed record NavItem(string Title, string Href, string Icon);

public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);

/// <summary>Catálogo das operações disponíveis (usado no menu e na página inicial).</summary>
public static class NavItems
{
    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        new("Tabelas",
        [
            new("Criar tabela", "tabela/criar", "table-plus"),
            new("Excluir tabela", "tabela/excluir", "trash"),
        ]),
        new("Colunas",
        [
            new("Adicionar coluna", "coluna/adicionar", "column-plus"),
            new("Alterar coluna", "coluna/alterar", "edit"),
            new("Renomear coluna", "coluna/renomear", "type"),
            new("Excluir coluna", "coluna/excluir", "trash"),
        ]),
        new("Constraints",
        [
            new("Chave primária", "constraint/pk", "key"),
            new("Chave estrangeira", "constraint/fk", "link"),
            new("Unique", "constraint/unique", "unique"),
            new("Check", "constraint/check", "check"),
            new("Default", "constraint/default", "flag"),
            new("Excluir constraint", "constraint/excluir", "x-circle"),
        ]),
        new("Índices",
        [
            new("Criar índice", "indice/criar", "list"),
            new("Excluir índice", "indice/excluir", "trash"),
        ]),
    ];
}
