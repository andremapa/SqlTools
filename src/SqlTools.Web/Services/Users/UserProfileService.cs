using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using SqlTools.Web.Data;

namespace SqlTools.Web.Services;

public sealed record UserProfile(string Id, string UserName, string DisplayName, string Email, long? PhotoVersion)
{
    /// <summary>Nome para exibir: o nome cadastrado ou, na falta dele, o nome de usuário.</summary>
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;

    public string? PhotoUrl => PhotoVersion is { } v ? $"/perfil/foto?v={v}" : null;
}

/// <summary>Perfil do usuário: dados de exibição e foto. Cada operação usa seu próprio escopo (e DbContext).</summary>
public sealed class UserProfileService(IServiceScopeFactory scopes)
{
    /// <summary>Tamanho máximo da foto guardada (já reduzida pelo navegador).</summary>
    public const int MaxPhotoBytes = 150 * 1024;

    public async Task<UserProfile?> GetAsync(string userId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.UserName, u.DisplayName, u.Email })
            .FirstOrDefaultAsync();
        if (user is null)
            return null;

        // Só a versão: a imagem em si só é lida quando o navegador a pede.
        var version = await db.UserPhotos.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (long?)p.Version)
            .FirstOrDefaultAsync();

        return new UserProfile(userId, user.UserName ?? "", user.DisplayName ?? "", user.Email ?? "", version);
    }

    public async Task<(byte[] Data, long Version)?> GetPhotoAsync(string userId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var photo = await db.UserPhotos.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
        return photo is null ? null : (photo.Data, photo.Version);
    }

    public async Task<OpResult> SavePhotoAsync(string userId, byte[] data)
    {
        // O navegador sempre envia um JPEG reduzido; aqui só se confere o resultado (não se confia no que chega).
        if (data.Length == 0)
            return OpResult.Fail("A imagem está vazia.");
        if (data.Length > MaxPhotoBytes)
            return OpResult.Fail($"A imagem é grande demais (máximo {MaxPhotoBytes / 1024} KB depois de reduzida).");
        if (!IsJpeg(data))
            return OpResult.Fail("Formato de imagem inválido.");

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.Users.AnyAsync(u => u.Id == userId))
            return OpResult.Fail("Usuário não encontrado.");

        var version = DateTime.UtcNow.Ticks;
        var photo = await db.UserPhotos.FirstOrDefaultAsync(p => p.UserId == userId);
        if (photo is null)
            db.UserPhotos.Add(new UserPhoto { UserId = userId, Data = data, Version = version });
        else
        {
            photo.Data = data;
            photo.Version = version;
        }

        await db.SaveChangesAsync();
        return OpResult.Success;
    }

    public async Task<OpResult> RemovePhotoAsync(string userId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.UserPhotos.Where(p => p.UserId == userId).ExecuteDeleteAsync();
        return OpResult.Success;
    }

    /// <summary>JPEG começa com FF D8 FF.</summary>
    public static bool IsJpeg(byte[] data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
}

/// <summary>
/// Perfil do usuário logado, por circuito. O menu do topo e a página de perfil compartilham este estado,
/// então trocar a foto atualiza o avatar do topo na hora.
/// </summary>
public sealed class ProfileState(UserProfileService service, AuthenticationStateProvider authentication)
{
    public UserProfile? Current { get; private set; }

    public event Action? Changed;

    public async Task LoadAsync()
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        var id = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        Current = id is null ? null : await service.GetAsync(id);
        Changed?.Invoke();
    }
}
