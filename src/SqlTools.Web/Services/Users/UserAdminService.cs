using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SqlTools.Web.Data;

namespace SqlTools.Web.Services;

public sealed record UserRow(string Id, string UserName, string Email, string Name, bool IsAdmin, bool IsDisabled, bool IsLockedOut);

/// <summary>
/// Regras do nome de usuário. Não pode conter "@": assim nunca coincide com o e-mail de outra pessoa
/// e o login por "usuário ou e-mail" é sempre inequívoco. (Contas antigas, cujo usuário é o próprio e-mail, continuam válidas.)
/// </summary>
public static partial class UsernameRules
{
    public const string Pattern = "^[A-Za-z0-9._-]{3,32}$";
    public const string Message = "Use de 3 a 32 caracteres: letras, números, ponto, hífen ou sublinhado (sem @).";

    public static bool IsValid(string? username) => !string.IsNullOrWhiteSpace(username) && UsernameRegex().IsMatch(username.Trim());

    [System.Text.RegularExpressions.GeneratedRegex(Pattern)]
    private static partial System.Text.RegularExpressions.Regex UsernameRegex();
}

public sealed record OpResult(bool Ok, string? Error = null)
{
    public static OpResult Success { get; } = new(true);
    public static OpResult Fail(string error) => new(false, error);
}

/// <summary>Traduz os erros do Identity para mensagens em português.</summary>
public static class IdentityErrors
{
    public static string Translate(IdentityError error) => error.Code switch
    {
        "DuplicateUserName" => "Já existe uma conta com esse nome de usuário.",
        "DuplicateEmail" => "Já existe uma conta com esse e-mail.",
        "PasswordTooShort" => "A senha deve ter pelo menos 8 caracteres.",
        "PasswordRequiresDigit" => "A senha deve ter pelo menos um número.",
        "PasswordRequiresLower" => "A senha deve ter pelo menos uma letra minúscula.",
        "InvalidEmail" or "InvalidUserName" => "E-mail inválido.",
        _ => error.Description,
    };

    public static string Describe(IEnumerable<IdentityError> errors) =>
        string.Join(" ", errors.Select(Translate).Distinct());
}

/// <summary>
/// Administração de usuários. Cada operação usa seu próprio escopo de DI (e DbContext), porque o painel
/// roda em um circuito Blazor de longa duração e um contexto compartilhado ficaria desatualizado.
/// </summary>
public sealed class UserAdminService(IServiceScopeFactory scopes)
{
    public const string AdminRole = "Admin";

    public Task<bool> HasUsersAsync() => With(m => m.Users.AnyAsync());

    public Task<List<UserRow>> ListAsync() => With(async m =>
    {
        var users = await m.Users.AsNoTracking().ToListAsync();
        var admins = (await m.GetUsersInRoleAsync(AdminRole)).Select(u => u.Id).ToHashSet();
        var now = DateTimeOffset.UtcNow;

        return users
            .OrderBy(u => u.Email, StringComparer.OrdinalIgnoreCase)
            .Select(u => new UserRow(
                u.Id, u.UserName ?? "", u.Email ?? "", u.DisplayName ?? "", admins.Contains(u.Id),
                IsDisabled(u), !IsDisabled(u) && u.LockoutEnd > now))
            .ToList();
    });

    public Task<OpResult> CreateAsync(string name, string username, string email, string password, bool isAdmin) => With(async m =>
    {
        username = username.Trim();
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("Informe o nome.");
        if (!UsernameRules.IsValid(username)) return OpResult.Fail(UsernameRules.Message);
        if (string.IsNullOrWhiteSpace(email)) return OpResult.Fail("Informe o e-mail.");

        var user = new ApplicationUser { UserName = username, Email = email, EmailConfirmed = true, DisplayName = name.Trim() };
        var created = await m.CreateAsync(user, password);
        if (!created.Succeeded)
            return OpResult.Fail(IdentityErrors.Describe(created.Errors));

        if (isAdmin)
            await m.AddToRoleAsync(user, AdminRole);
        return OpResult.Success;
    });

    public Task<OpResult> UpdateAsync(string id, string name, string username, string email) => With(async m =>
    {
        var user = await m.FindByIdAsync(id);
        if (user is null) return OpResult.Fail("Usuário não encontrado.");

        name = name.Trim();
        username = username.Trim();
        email = email.Trim();
        if (name.Length == 0) return OpResult.Fail("Informe o nome.");
        if (email.Length == 0) return OpResult.Fail("Informe o e-mail.");

        // Contas antigas usam o e-mail como usuário (com "@"): mantê-lo continua permitido, mas um valor novo segue a regra.
        if (!string.Equals(username, user.UserName, StringComparison.Ordinal) && !UsernameRules.IsValid(username))
            return OpResult.Fail(UsernameRules.Message);

        // Uma única gravação: se o e-mail ou o usuário já existirem, nada é alterado.
        user.DisplayName = name;
        user.UserName = username;
        user.Email = email;
        user.EmailConfirmed = true; // o administrador é quem garante o e-mail

        var result = await m.UpdateAsync(user);
        return result.Succeeded ? OpResult.Success : OpResult.Fail(IdentityErrors.Describe(result.Errors));
    });

    public Task<OpResult> SetAdminAsync(string id, bool makeAdmin, string actingUserId) => With(async m =>
    {
        var user = await m.FindByIdAsync(id);
        if (user is null) return OpResult.Fail("Usuário não encontrado.");

        if (!makeAdmin)
        {
            if (id == actingUserId) return OpResult.Fail("Você não pode remover o seu próprio acesso de administrador.");
            if (await IsLastActiveAdminAsync(m, user)) return OpResult.Fail("Este é o último administrador ativo. Promova outro usuário antes.");
        }

        var result = makeAdmin ? await m.AddToRoleAsync(user, AdminRole) : await m.RemoveFromRoleAsync(user, AdminRole);
        if (!result.Succeeded) return OpResult.Fail(IdentityErrors.Describe(result.Errors));

        await m.UpdateSecurityStampAsync(user); // o perfil novo vale no próximo login
        return OpResult.Success;
    });

    public Task<OpResult> SetDisabledAsync(string id, bool disabled, string actingUserId) => With(async m =>
    {
        var user = await m.FindByIdAsync(id);
        if (user is null) return OpResult.Fail("Usuário não encontrado.");

        if (disabled)
        {
            if (id == actingUserId) return OpResult.Fail("Você não pode desativar a sua própria conta.");
            if (await IsLastActiveAdminAsync(m, user)) return OpResult.Fail("Este é o último administrador ativo. Promova outro usuário antes.");

            await m.SetLockoutEnabledAsync(user, true);
            await m.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            await m.UpdateSecurityStampAsync(user); // derruba sessões abertas na próxima validação
        }
        else
        {
            await m.SetLockoutEndDateAsync(user, null);
            await m.ResetAccessFailedCountAsync(user);
        }
        return OpResult.Success;
    });

    public Task<OpResult> ResetPasswordAsync(string id, string newPassword) => With(async m =>
    {
        var user = await m.FindByIdAsync(id);
        if (user is null) return OpResult.Fail("Usuário não encontrado.");

        // Valida antes de trocar, para nunca deixar a conta sem senha por causa de uma senha fraca.
        foreach (var validator in m.PasswordValidators)
        {
            var valid = await validator.ValidateAsync(m, user, newPassword);
            if (!valid.Succeeded) return OpResult.Fail(IdentityErrors.Describe(valid.Errors));
        }

        user.PasswordHash = m.PasswordHasher.HashPassword(user, newPassword);
        var updated = await m.UpdateSecurityStampAsync(user); // grava o hash e invalida sessões antigas
        if (!updated.Succeeded) return OpResult.Fail(IdentityErrors.Describe(updated.Errors));

        await m.ResetAccessFailedCountAsync(user);
        return OpResult.Success;
    });

    public Task<OpResult> DeleteAsync(string id, string actingUserId) => With(async m =>
    {
        var user = await m.FindByIdAsync(id);
        if (user is null) return OpResult.Fail("Usuário não encontrado.");
        if (id == actingUserId) return OpResult.Fail("Você não pode excluir a sua própria conta.");
        if (await IsLastActiveAdminAsync(m, user)) return OpResult.Fail("Este é o último administrador ativo. Promova outro usuário antes.");

        var result = await m.DeleteAsync(user);
        return result.Succeeded ? OpResult.Success : OpResult.Fail(IdentityErrors.Describe(result.Errors));
    });

    private static bool IsDisabled(ApplicationUser user) => user.LockoutEnd == DateTimeOffset.MaxValue;

    /// <summary>Verdadeiro se o usuário é administrador ativo e não existe outro administrador ativo.</summary>
    private static async Task<bool> IsLastActiveAdminAsync(UserManager<ApplicationUser> m, ApplicationUser user)
    {
        var activeAdmins = (await m.GetUsersInRoleAsync(AdminRole)).Where(u => !IsDisabled(u)).ToList();
        return activeAdmins.Any(u => u.Id == user.Id) && activeAdmins.All(u => u.Id == user.Id);
    }

    private async Task<T> With<T>(Func<UserManager<ApplicationUser>, Task<T>> action)
    {
        using var scope = scopes.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }
}
