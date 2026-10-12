using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SqlTools.Web.Data;

public sealed class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
}

/// <summary>
/// Foto de perfil. Fica numa tabela própria (e não numa coluna do usuário) para que listar usuários não carregue as imagens.
/// </summary>
public sealed class UserPhoto
{
    public required string UserId { get; set; }

    /// <summary>Imagem JPEG já reduzida (o navegador redimensiona antes de enviar).</summary>
    public required byte[] Data { get; set; }

    /// <summary>Muda a cada troca de foto: serve de ETag e de parâmetro para o navegador não usar a imagem antiga em cache.</summary>
    public long Version { get; set; }
}

/// <summary>Banco local (SQLite) usado apenas para os usuários da aplicação.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<UserPhoto> UserPhotos => Set<UserPhoto>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<UserPhoto>(photo =>
        {
            photo.HasKey(p => p.UserId);
            // Excluir o usuário exclui a foto.
            photo.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>Cria o banco de usuários e o mantém atualizado (sem migrations: as mudanças são poucas e idempotentes).</summary>
public static class UserStoreBootstrapper
{
    public static async Task EnsureAsync(AppDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        // EnsureCreated não altera um banco que já existe: bancos criados antes da foto de perfil não têm esta tabela.
        // Em bancos novos ela já foi criada acima e este comando não faz nada.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "UserPhotos" (
                "UserId" TEXT NOT NULL CONSTRAINT "PK_UserPhotos" PRIMARY KEY,
                "Data" BLOB NOT NULL,
                "Version" INTEGER NOT NULL,
                CONSTRAINT "FK_UserPhotos_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            """);
    }
}
