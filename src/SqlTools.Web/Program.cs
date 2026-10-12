using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using SqlTools.Core.Generation;
using SqlTools.Web.Components;
using SqlTools.Web.Data;
using SqlTools.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // A foto de perfil (JPEG reduzido, em base64) volta do navegador ao servidor pelo circuito: o padrão de 32 KB não basta.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 256 * 1024);

// --- Autenticação -----------------------------------------------------------
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDir);
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Users") ?? $"Data Source={Path.Combine(dataDir, "sqltools.db")}"));

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddAuthorization();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/entrar";
    o.AccessDeniedPath = "/acesso-negado";
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
    o.SlidingExpiration = true;
});

builder.Services.AddIdentityCore<ApplicationUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.SignIn.RequireConfirmedAccount = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager();

// Revalida a sessão a cada minuto: usuário desativado ou com perfil alterado perde o acesso rápido.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

// --- Aplicação --------------------------------------------------------------
builder.Services.AddSingleton<IScriptGenerator, SqlServerScriptGenerator>();
builder.Services.AddSingleton<IDatabaseSchemaReader, SqlServerSchemaReader>();
builder.Services.AddScoped<ScriptSession>();
builder.Services.AddScoped<DatabaseConnection>();
builder.Services.AddSingleton<UserAdminService>();
builder.Services.AddSingleton<UserProfileService>();
builder.Services.AddScoped<ProfileState>();

var app = builder.Build();

await SeedAsync(app);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapPost("/sair", async (HttpContext http, SignInManager<ApplicationUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.LocalRedirect("/entrar");
}).RequireAuthorization();

// Foto do usuário logado. "private, no-cache": o navegador sempre confere (ETag) antes de reutilizar, então trocar a foto vale na hora.
app.MapGet("/perfil/foto", async (HttpContext http, UserProfileService profiles) =>
{
    var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (id is null || await profiles.GetPhotoAsync(id) is not { } photo)
        return Results.NotFound();

    http.Response.Headers.CacheControl = "private, no-cache";
    http.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(photo.Data, "image/jpeg", entityTag: new EntityTagHeaderValue($"\"{photo.Version}\""));
}).RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Cria o banco de usuários e o perfil Admin. Garante que sempre exista ao menos um administrador.</summary>
static async Task SeedAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    await UserStoreBootstrapper.EnsureAsync(services.GetRequiredService<AppDbContext>());

    var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync(UserAdminService.AdminRole))
        await roles.CreateAsync(new IdentityRole(UserAdminService.AdminRole));

    // Bancos criados antes do painel de administração não têm administrador. Promove o único usuário
    // existente ou, havendo vários, o e-mail indicado em "Auth:AdminEmail".
    var users = services.GetRequiredService<UserManager<ApplicationUser>>();
    if ((await users.GetUsersInRoleAsync(UserAdminService.AdminRole)).Count > 0)
        return;

    var all = await users.Users.Take(2).ToListAsync();
    var adminEmail = app.Configuration["Auth:AdminEmail"];
    var promoted = !string.IsNullOrWhiteSpace(adminEmail)
        ? await users.FindByEmailAsync(adminEmail.Trim())
        : all.Count == 1 ? all[0] : null;

    if (promoted is not null)
    {
        await users.AddToRoleAsync(promoted, UserAdminService.AdminRole);
        app.Logger.LogWarning("Nenhum administrador encontrado: {Email} foi promovido a administrador.", promoted.Email);
    }
    else if (all.Count > 1)
    {
        app.Logger.LogWarning("Nenhum administrador encontrado. Defina Auth:AdminEmail com o e-mail de um usuário existente.");
    }
}
