using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SqlTools.Web.Data;

public sealed class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
}

/// <summary>Banco local (SQLite) usado apenas para os usuários da aplicação.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options);
