using DinoTranslator.Server.Auth;
using DinoTranslator.Shared;

namespace DinoTranslator.Server.Cli;

/// <summary>
/// Comando <c>users</c>, executado no próprio servidor — é a única forma de criar contas.
/// </summary>
public sealed class UserCommands(UserAdministration admin, TextWriter output, ISecretReader secrets)
{
    public const string Usage = """
        Uso:
          users add <usuario> --nome "<nome de exibição>" --idioma pt-BR|en-US|uk-UA|ru-RU|es-ES [--genero masculino|feminino]
          users list
          users reset-password <usuario>
          users unlock <usuario>
          users remove <usuario>
        """;

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            return args switch
            {
                ["add", var userName, .. var options] => await AddAsync(userName, options, cancellationToken),
                ["list"] => await ListAsync(cancellationToken),
                ["reset-password", var userName] => await ResetPasswordAsync(userName, cancellationToken),
                ["unlock", var userName] => await UnlockAsync(userName, cancellationToken),
                ["remove", var userName] => await RemoveAsync(userName, cancellationToken),
                _ => PrintUsage(),
            };
        }
        catch (UserAdministrationException ex)
        {
            await output.WriteLineAsync($"Erro: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> AddAsync(string userName, string[] options, CancellationToken cancellationToken)
    {
        if (!TryParseOptions(options, out var values))
        {
            return PrintUsage();
        }

        var displayName = values.GetValueOrDefault("--nome");
        var language = values.GetValueOrDefault("--idioma");
        if (displayName is null || language is null)
        {
            return PrintUsage();
        }

        var gender = values.GetValueOrDefault("--genero") switch
        {
            null => Gender.Unspecified,
            "masculino" => Gender.Male,
            "feminino" => Gender.Female,
            _ => (Gender?)null,
        };
        if (gender is null)
        {
            return PrintUsage();
        }

        var password = ReadNewPassword();
        if (password is null)
        {
            await output.WriteLineAsync("Erro: as senhas não conferem.");
            return 1;
        }

        var user = await admin.CreateAsync(new NewUser(userName, displayName, language, gender.Value, password), cancellationToken);
        await output.WriteLineAsync($"Conta \"{user.UserName}\" criada.");
        return 0;
    }

    private async Task<int> ListAsync(CancellationToken cancellationToken)
    {
        var users = await admin.ListAsync(cancellationToken);
        if (users.Count == 0)
        {
            await output.WriteLineAsync("Nenhuma conta criada.");
        }
        foreach (var user in users)
        {
            var locked = user.LockoutEndUtc is not null ? " · bloqueada" : string.Empty;
            await output.WriteLineAsync($"{user.UserName} · {user.DisplayName} · {user.Language}{locked}");
        }
        return 0;
    }

    private async Task<int> ResetPasswordAsync(string userName, CancellationToken cancellationToken)
    {
        var password = ReadNewPassword();
        if (password is null)
        {
            await output.WriteLineAsync("Erro: as senhas não conferem.");
            return 1;
        }

        await admin.ResetPasswordAsync(userName, password, cancellationToken);
        await output.WriteLineAsync("Senha trocada. Todas as sessões foram encerradas.");
        return 0;
    }

    private async Task<int> UnlockAsync(string userName, CancellationToken cancellationToken)
    {
        await admin.UnlockAsync(userName, cancellationToken);
        await output.WriteLineAsync("Conta desbloqueada.");
        return 0;
    }

    private async Task<int> RemoveAsync(string userName, CancellationToken cancellationToken)
    {
        await admin.RemoveAsync(userName, cancellationToken);
        await output.WriteLineAsync("Conta removida.");
        return 0;
    }

    private string? ReadNewPassword()
    {
        var password = secrets.ReadSecret("Senha: ");
        if (!secrets.IsInteractive)
        {
            return password;
        }
        return password == secrets.ReadSecret("Confirme a senha: ") ? password : null;
    }

    private int PrintUsage()
    {
        output.WriteLine(Usage);
        return 2;
    }

    private static bool TryParseOptions(string[] options, out Dictionary<string, string> values)
    {
        values = [];
        if (options.Length % 2 != 0)
        {
            return false;
        }
        for (var i = 0; i < options.Length; i += 2)
        {
            if (!options[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(options[i], options[i + 1]))
            {
                return false;
            }
        }
        return true;
    }
}
