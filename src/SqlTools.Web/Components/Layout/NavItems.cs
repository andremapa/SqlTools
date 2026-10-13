namespace SqlTools.Web.Components.Layout;

public sealed record NavItem(string Title, string Href, string Icon);

/// <summary>
/// Grupo do menu. Com vários itens vira uma seção que pode ser recolhida; com um único item vira um link direto
/// (com o título e o ícone do grupo).
/// </summary>
public sealed record NavGroup(string Title, string Icon, IReadOnlyList<NavItem> Items)
{
    public bool IsSingleLink => Items.Count == 1;
}

/// <summary>Itens do menu e da página inicial.</summary>
public static class NavItems
{
    /// <summary>Primeiro item do menu, só para administradores.</summary>
    public static readonly NavGroup Admin = new("Administração", "users", [new("Usuários", "admin/usuarios", "users")]);

    public static readonly NavGroup Connection = new("Conexão", "database", [new("Conexão com o banco", "conexao", "database")]);

    public static readonly NavGroup Batch = new("Lote", "layers", [new("Script em lote", "lote", "layers")]);

    /// <summary>Os grupos de operações que geram script, na ordem do menu.</summary>
    public static readonly IReadOnlyList<NavGroup> Operations =
    [
        new("Tabelas", "table",
        [
            new("Criar tabela", "tabela/criar", "table-plus"),
            new("Excluir tabela", "tabela/excluir", "trash"),
            new("Adicionar colunas", "tabela/colunas/adicionar", "column-plus"),
            new("Alterar colunas", "tabela/colunas/alterar", "edit"),
            new("Renomear colunas", "tabela/colunas/renomear", "type"),
            new("Excluir colunas", "tabela/colunas/excluir", "trash"),
        ]),
        new("Views", "eye",
        [
            new("Criar/recriar view", "view/criar", "eye"),
            new("Adicionar colunas", "view/colunas", "column-plus"),
            new("Excluir view", "view/excluir", "trash"),
        ]),
        new("Triggers", "zap",
        [
            new("Criar/recriar trigger", "trigger/criar", "zap"),
            new("Alterar trigger", "trigger/alterar", "edit"),
            new("Excluir trigger", "trigger/excluir", "trash"),
        ]),
        new("Constraints", "key",
        [
            new("Chave primária", "constraint/pk", "key"),
            new("Chave estrangeira", "constraint/fk", "link"),
            new("Unique", "constraint/unique", "unique"),
            new("Check", "constraint/check", "check"),
            new("Default", "constraint/default", "flag"),
            new("Excluir constraint", "constraint/excluir", "x-circle"),
        ]),
        new("Índices", "list",
        [
            new("Criar índice", "indice/criar", "list"),
            new("Excluir índice", "indice/excluir", "trash"),
        ]),
    ];

    /// <summary>
    /// Ordem do menu: (Administração, se for admin), Conexão, Tabelas (com as colunas), Views, Triggers, Constraints, Índices e Lote.
    /// </summary>
    public static readonly IReadOnlyList<NavGroup> Menu = [Connection, .. Operations, Batch];
}
