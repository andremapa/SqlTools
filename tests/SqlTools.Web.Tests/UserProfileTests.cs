using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlTools.Web.Data;
using SqlTools.Web.Services;

namespace SqlTools.Web.Tests;

/// <summary>Banco de usuários em SQLite na memória (a conexão fica aberta durante o teste, senão o banco some).</summary>
public sealed class UserStore : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;

    public UserProfileService Profiles { get; }

    public UserStore()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite(_connection))
            .AddSingleton<UserProfileService>()
            .BuildServiceProvider();
        Profiles = _services.GetRequiredService<UserProfileService>();
    }

    public AppDbContext NewContext() => _services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    public async Task InitializeAsync() => await UserStoreBootstrapper.EnsureAsync(NewContext());

    public async Task<string> AddUserAsync(string userName = "maria.silva", string? displayName = "Maria Silva", string email = "maria@example.test")
    {
        var db = NewContext();
        var user = new ApplicationUser { Id = Guid.NewGuid().ToString(), UserName = userName, NormalizedUserName = userName.ToUpperInvariant(), Email = email, DisplayName = displayName };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return _services.DisposeAsync();
    }
}

public class UserProfileTests
{
    /// <summary>Um JPEG mínimo: só importa que comece com FF D8 FF.</summary>
    private static byte[] Jpeg(int length = 2_000)
    {
        var data = new byte[length];
        data[0] = 0xFF;
        data[1] = 0xD8;
        data[2] = 0xFF;
        data[3] = 0xE0;
        return data;
    }

    private static async Task<UserStore> NewStoreAsync()
    {
        var store = new UserStore();
        await store.InitializeAsync();
        return store;
    }

    // ------------------------------------------------------------------ banco de usuários

    [Fact]
    public async Task Bootstrap_AddsThePhotoTableToADatabaseCreatedBeforePhotos()
    {
        await using var store = await NewStoreAsync();
        await using (var drop = store.NewContext())
            await drop.Database.ExecuteSqlRawAsync("""DROP TABLE "UserPhotos";""");
        Assert.Equal(0, await store.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name = 'UserPhotos'"));

        // Como no banco que já existe em App_Data: a tabela não existe e EnsureCreated não a criaria.
        await UserStoreBootstrapper.EnsureAsync(store.NewContext());

        Assert.Equal(1, await store.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name = 'UserPhotos'"));
        var id = await store.AddUserAsync();
        Assert.True((await store.Profiles.SavePhotoAsync(id, Jpeg())).Ok);
    }

    [Fact]
    public async Task Bootstrap_IsIdempotent()
    {
        await using var store = await NewStoreAsync();

        await UserStoreBootstrapper.EnsureAsync(store.NewContext());
        await UserStoreBootstrapper.EnsureAsync(store.NewContext());

        Assert.Equal(1, await store.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name = 'UserPhotos'"));
    }

    // ------------------------------------------------------------------ perfil

    [Fact]
    public async Task Profile_WithoutPhoto_HasNoUrl_AndFallsBackToTheUserName()
    {
        await using var store = await NewStoreAsync();
        var id = await store.AddUserAsync(displayName: null);

        var profile = await store.Profiles.GetAsync(id);

        Assert.NotNull(profile);
        Assert.Null(profile.PhotoUrl);
        Assert.Equal("maria.silva", profile.Name);
    }

    [Fact]
    public async Task Profile_OfAnUnknownUser_IsNull()
    {
        await using var store = await NewStoreAsync();

        Assert.Null(await store.Profiles.GetAsync("nao-existe"));
    }

    // ------------------------------------------------------------------ foto

    [Fact]
    public async Task SavePhoto_StoresIt_AndTheProfileGetsAVersionedUrl()
    {
        await using var store = await NewStoreAsync();
        var id = await store.AddUserAsync();

        var result = await store.Profiles.SavePhotoAsync(id, Jpeg());

        Assert.True(result.Ok, result.Error);
        var profile = await store.Profiles.GetAsync(id);
        Assert.NotNull(profile!.PhotoUrl);
        Assert.StartsWith("/perfil/foto?v=", profile.PhotoUrl);
        Assert.Equal(Jpeg(), (await store.Profiles.GetPhotoAsync(id))!.Value.Data);
    }

    [Fact]
    public async Task SavePhoto_Replaces_AndChangesTheVersionSoTheBrowserDropsTheCache()
    {
        await using var store = await NewStoreAsync();
        var id = await store.AddUserAsync();
        await store.Profiles.SavePhotoAsync(id, Jpeg(1_000));
        var first = (await store.Profiles.GetPhotoAsync(id))!.Value;

        await Task.Delay(5);
        await store.Profiles.SavePhotoAsync(id, Jpeg(3_000));
        var second = (await store.Profiles.GetPhotoAsync(id))!.Value;

        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal(3_000, second.Data.Length);
        Assert.Equal(1, await store.ScalarAsync("SELECT COUNT(*) FROM UserPhotos")); // substitui, não acumula
    }

    [Fact]
    public async Task SavePhoto_Rejects_Empty_TooLarge_AndNonJpegData()
    {
        await using var store = await NewStoreAsync();
        var id = await store.AddUserAsync();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 };
        var html = System.Text.Encoding.UTF8.GetBytes("<script>alert(1)</script>");

        var empty = await store.Profiles.SavePhotoAsync(id, []);
        var tooLarge = await store.Profiles.SavePhotoAsync(id, Jpeg(UserProfileService.MaxPhotoBytes + 1));
        var notJpeg = await store.Profiles.SavePhotoAsync(id, png);
        var script = await store.Profiles.SavePhotoAsync(id, html);

        Assert.Contains("vazia", empty.Error);
        Assert.Contains("grande demais", tooLarge.Error);
        Assert.Contains("inválido", notJpeg.Error);
        Assert.Contains("inválido", script.Error);
        Assert.Null(await store.Profiles.GetPhotoAsync(id)); // nada foi guardado
    }

    [Fact]
    public async Task SavePhoto_AcceptsExactlyTheMaximumSize()
    {
        await using var store = await NewStoreAsync();
        var id = await store.AddUserAsync();

        Assert.True((await store.Profiles.SavePhotoAsync(id, Jpeg(UserProfileService.MaxPhotoBytes))).Ok);
    }

    [Fact]
    public async Task SavePhoto_ForAnUnknownUser_Fails()
    {
        await using var store = await NewStoreAsync();

        var result = await store.Profiles.SavePhotoAsync("nao-existe", Jpeg());

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task RemovePhoto_BringsBackTheInitials_AndIsIdempotent()
    {
        await using var store = await NewStoreAsync();
        var id = await store.AddUserAsync();
        await store.Profiles.SavePhotoAsync(id, Jpeg());

        await store.Profiles.RemovePhotoAsync(id);
        var again = await store.Profiles.RemovePhotoAsync(id);

        Assert.True(again.Ok);
        Assert.Null((await store.Profiles.GetAsync(id))!.PhotoUrl);
    }

    [Fact]
    public async Task APhotoBelongsToItsUserOnly()
    {
        await using var store = await NewStoreAsync();
        var maria = await store.AddUserAsync("maria", "Maria", "maria@x.test");
        var joao = await store.AddUserAsync("joao", "João", "joao@x.test");

        await store.Profiles.SavePhotoAsync(maria, Jpeg(1_500));

        Assert.NotNull(await store.Profiles.GetPhotoAsync(maria));
        Assert.Null(await store.Profiles.GetPhotoAsync(joao));
        Assert.Null((await store.Profiles.GetAsync(joao))!.PhotoUrl);
    }

    [Fact]
    public async Task DeletingTheUser_DeletesThePhoto()
    {
        await using var store = await NewStoreAsync();
        var id = await store.AddUserAsync();
        await store.Profiles.SavePhotoAsync(id, Jpeg());
        Assert.Equal(1, await store.ScalarAsync("SELECT COUNT(*) FROM UserPhotos"));

        await using (var db = store.NewContext())
            await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync();

        Assert.Equal(0, await store.ScalarAsync("SELECT COUNT(*) FROM UserPhotos"));
    }

    [Fact]
    public async Task ListingUsers_DoesNotLoadThePhotos()
    {
        await using var store = await NewStoreAsync();
        var id = await store.AddUserAsync();
        await store.Profiles.SavePhotoAsync(id, Jpeg(100_000));

        // A foto fica em outra tabela: a entidade do usuário nem tem o conteúdo da imagem.
        Assert.DoesNotContain(typeof(ApplicationUser).GetProperties(), p => p.PropertyType == typeof(byte[]));
    }
}
