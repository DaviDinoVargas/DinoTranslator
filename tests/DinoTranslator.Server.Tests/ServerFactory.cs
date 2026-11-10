using System.Net.Http.Json;
using System.Security.Cryptography;
using DinoTranslator.Server.Auth;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DinoTranslator.Server.Tests;

/// <summary>
/// Sobe o servidor em memória no ambiente "Testing", com banco SQLite temporário próprio
/// e relógio controlável.
/// </summary>
public class ServerFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _settings;

    /// <summary>Usado como fixture de classe pelo xUnit, que exige um único construtor público.</summary>
    public ServerFactory()
        : this(new Dictionary<string, string?>())
    {
    }

    /// <summary>Com configurações próprias do teste (limites, cadastro fechado etc.).</summary>
    internal ServerFactory(IReadOnlyDictionary<string, string?> settings) => _settings = settings;

    public const string DefaultPassword = "senha-de-teste-forte";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"dinotranslator-test-{Guid.NewGuid():N}.db");

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Database", $"Data Source={_databasePath};Pooling=False");
        builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        // Limites altos por padrão; o teste do limite usa os próprios valores.
        builder.UseSetting("Auth:LoginPermitsPerMinute", "1000");
        builder.UseSetting("Auth:RefreshPermitsPerMinute", "1000");
        builder.UseSetting("Auth:RegisterPermitsPerHour", "1000");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    public async Task<UserProfile> CreateUserAsync(
        string userName,
        string password = DefaultPassword,
        string language = Languages.PortugueseBrazil,
        Gender gender = Gender.Unspecified)
    {
        await using var scope = Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdministration>();
        var user = await admin.CreateAsync(new NewUser(userName, userName, language, gender, password));
        return AuthService.ToProfile(user);
    }

    public async Task<TokenResponse> LoginAsync(HttpClient client, string userName, string password = DefaultPassword)
    {
        var response = await client.PostAsJsonAsync(AuthRoutes.Login, new LoginRequest(userName, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }
}
