# SqlTools

Aplicação web (Blazor) que **gera scripts T-SQL para SQL Server**: criação, alteração e exclusão de tabelas, colunas, constraints, índices, views e triggers. Os scripts são **idempotentes**: verificam o estado do banco antes de alterar, então podem ser executados mais de uma vez sem erro.

A aplicação **nunca executa os scripts**. Ela só gera o texto, que você copia ou baixa como `.sql` e roda onde preferir (SSMS, sqlcmd, pipeline...). Opcionalmente ela se conecta ao banco, em modo somente leitura, apenas para sugerir tabelas, colunas, constraints e índices existentes.

## Funcionalidades

### Geração de scripts

| Grupo | Operações |
|---|---|
| **Tabelas** | Criar (com PK, IDENTITY, defaults e criação opcional do schema), excluir |
| **Views** | Criar/recriar, adicionar colunas a uma view existente, excluir |
| **Triggers** | Criar/recriar (AFTER/INSTEAD OF e INSERT/UPDATE/DELETE), alterar a definição existente, excluir |
| **Colunas** | Adicionar, alterar tipo/nulidade, renomear, excluir |
| **Constraints** | Chave primária, chave estrangeira, unique, check, default, excluir |
| **Índices** | Criar (unique, clustered, INCLUDE, filtro WHERE), excluir |
| **Lote** | Junta várias operações em um único arquivo, na ordem escolhida |

**Views e triggers** usam o formato clássico `DROP`, `GO`, `CREATE`, `GO`. Em "Adicionar colunas à view", as colunas novas são inseridas no `SELECT` principal por um analisador de T-SQL (que ignora strings, comentários e subconsultas), e a view é recriada. Views com `UNION`/`INTERSECT`/`EXCEPT` ou com lista de colunas no cabeçalho não são suportadas nessa tela; para elas, use "Criar/recriar view" com o `SELECT` completo.

### Opções do script (painel ao lado de cada script)

Todas começam **desmarcadas**.

- **Safe guard**: por padrão o script é simples e só verifica se o objeto já existe. Com a opção, entram as validações extras (tabela, coluna ou schema inexistente, nome em uso, dados que impedem a alteração, dependências). Em views e triggers, também liga a transação que desfaz o `DROP` se o `CREATE` falhar.
- **Adicionar comandos PRINT**: o script informa o que fez ("Tabela criada.", "já existe..."). Sem a opção, nenhum `PRINT` é escrito.
- **Transação com rollback**: envolve cada operação em `TRY/CATCH` com transação. Não se aplica a views e triggers, que têm vários lotes.
- **Cabeçalho**: comentário com a operação e a data de geração.
- Campo opcional **Banco para USE** no início do script.

Exemplo do script padrão (sem opções):

```sql
IF OBJECT_ID(N'[dbo].[Cliente]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Cliente]
    (
        [Id] INT IDENTITY(1,1) NOT NULL,
        [Nome] NVARCHAR(100) NOT NULL,
        CONSTRAINT [PK_Cliente] PRIMARY KEY CLUSTERED ([Id])
    );
END
GO
```

E o de uma view:

```sql
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF OBJECT_ID(N'[dbo].[vw_ClientesAtivos]', N'V') IS NOT NULL
BEGIN
    DROP VIEW [dbo].[vw_ClientesAtivos];
END
GO

CREATE VIEW [dbo].[vw_ClientesAtivos]
AS
SELECT c.Id, c.Nome
FROM dbo.Cliente c
WHERE c.Ativo = 1
GO
```

> **Atenção:** no formato simples, se o `CREATE` de uma view ou trigger falhar, o `DROP` já aconteceu e a versão antiga se perde. Ligue o **Safe guard** para que o script desfaça o `DROP` nesse caso.

### Conexão com o banco (opcional, somente leitura)

- Tela no estilo do SSMS: servidor, autenticação (SQL Server ou Windows), usuário e senha; depois, a lista de bancos para escolher.
- Com a conexão, as telas oferecem listas de tabelas, views, triggers, colunas, constraints e índices, e o explorador em árvore na própria tela de conexão.
- Otimizada para bancos de ERP com milhares de tabelas: ao conectar só os nomes são lidos, os detalhes são carregados sob demanda, as consultas usam `READ UNCOMMITTED` (um DDL aberto não bloqueia a leitura) e as listas são filtradas e paginadas.
- `TrustServerCertificate` é sempre ligado, sem aparecer na tela.
- **Lembrar neste navegador**: guarda os dados da conexão criptografados (Data Protection), separados por usuário.

### Usuários e acesso

- Login por **nome de usuário ou e-mail** (ASP.NET Core Identity + SQLite local).
- O **primeiro cadastro** vira administrador; depois disso o cadastro público fica fechado.
- **Painel de administração** (`/admin/usuarios`): cadastrar, editar, atribuir ou remover perfil de administrador, desativar/ativar, redefinir senha e excluir usuários. Há proteções contra o administrador remover ou excluir a si mesmo e contra remover o último administrador ativo.
- **Foto de perfil** (`/perfil`): o navegador corta a imagem em quadrado, reduz para 256 px e converte para JPEG antes de enviar. O menu do usuário fica no canto superior direito.

## Tecnologias

- .NET 10, Blazor Web App (Interactive Server)
- ASP.NET Core Identity, Entity Framework Core com SQLite (somente para os usuários da aplicação)
- Microsoft.Data.SqlClient (leitura de metadados do SQL Server)
- xUnit; testes de integração com SQL Server LocalDB

## Estrutura

```
src/
  SqlTools.Core/   Geração de scripts (independente de interface): requisições, gerador T-SQL, analisador de T-SQL
  SqlTools.Web/    Aplicação Blazor: páginas, componentes, login, leitura de metadados do banco
tests/
  SqlTools.Core.Tests/   Gerador, analisador e execução dos scripts em um banco real
  SqlTools.Web.Tests/    Leitura de metadados, conexão e perfil de usuário
```

## Como executar

Requer o [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/SqlTools.Web
```

A aplicação abre em `http://localhost:5185` (e `https://localhost:7096`, no perfil https). No primeiro acesso, abra `/cadastro` e crie a conta do administrador.

### Configuração (`appsettings.json`, opcional)

| Chave | Efeito |
|---|---|
| `Auth:AllowRegistration` | `true` reabre o cadastro público (por padrão só o primeiro usuário pode se cadastrar) |
| `Auth:AdminEmail` | Para bancos de usuários sem administrador e com vários usuários: e-mail do usuário a promover |
| `ConnectionStrings:Users` | Caminho do banco SQLite de usuários (padrão: `App_Data/sqltools.db`) |

O banco de usuários fica em `src/SqlTools.Web/App_Data/` e **não é versionado**.

## Testes

```bash
dotnet test
```

Os testes de integração criam um banco temporário no **SQL Server LocalDB** (`(localdb)\MSSQLLocalDB`) e executam os scripts gerados, inclusive duas vezes para provar a idempotência. Sem LocalDB, esses testes são ignorados. Para usar outra instância, defina a variável de ambiente `SQLTOOLS_TEST_SERVER`.

## Segurança

- Os scripts gerados **não são executados** pela aplicação; a conexão com o banco só lê metadados.
- Identificadores e literais são escapados nos scripts (`QUOTENAME`/aspas duplicadas).
- Todas as páginas exigem login. Se for expor a aplicação na internet (por exemplo, com um túnel), crie os usuários pelo painel e não marque "Administrador" para quem não precisa: um usuário logado pode apontar a aplicação para qualquer servidor SQL que a máquina alcance.
- A foto de perfil só aceita JPEG de até 150 KB (conferido no servidor) e cada usuário acessa apenas a própria foto.

## Limitações

- Somente **SQL Server** (T-SQL).
- O lote de operações fica na memória da sessão: recarregar a página (F5) o descarta.
- Não há edição do nome de usuário pelo próprio usuário nem troca de senha pela página de perfil (a redefinição de senha é feita pelo administrador).
