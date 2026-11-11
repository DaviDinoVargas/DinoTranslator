using DinoTranslator.Server.Auth;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Auth;

namespace DinoTranslator.Server.Cli;

/// <summary>
/// Comando <c>users</c>, executado no próprio servidor: cria contas (mesmo com o cadastro
/// fechado), ativa o plano Plus e suspende contas.
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
          users plan <usuario> plus [--dias N]     (sem --dias: sem prazo; renovar soma ao que resta)
          users plan <usuario> gratis
          users plan <prefixo>* plus|gratis        (todas as contas que começam com o prefixo, 3+ letras)
          users disable <usuario>                  (suspende e encerra as sessões)
          users enable <usuario>
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
                ["plan", var prefix, var plan] when prefix.EndsWith('*') && plan is "plus" or "gratis" =>
                    await PlanPrefixAsync(prefix[..^1], plan == "plus" ? AccountPlan.Plus : AccountPlan.Free, cancellationToken),
                ["plan", var userName, "plus"] => await PlanAsync(userName, AccountPlan.Plus, null, cancellationToken),
                ["plan", var userName, "plus", "--dias", var days] when int.TryParse(days, out var n) => await PlanAsync(userName, AccountPlan.Plus, n, cancellationToken),
                ["plan", var userName, "gratis"] => await PlanAsync(userName, AccountPlan.Free, null, cancellationToken),
                ["disable", var userName] => await DisableAsync(userName, cancellationToken),
                ["enable", var userName] => await EnableAsync(userName, cancellationToken),
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
            var disabled = user.DisabledAtUtc is not null ? " · suspensa" : string.Empty;
            var plan = user.Plan == AccountPlan.Plus
                ? " · Plus" + (user.PlanExpiresAtUtc is { } until ? $" até {until:yyyy-MM-dd}" : string.Empty)
                : " · grátis";
            await output.WriteLineAsync($"{user.UserName} · {user.DisplayName} · {user.Language}{plan}{locked}{disabled}");
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

    private async Task<int> PlanAsync(string userName, AccountPlan plan, int? days, CancellationToken cancellationToken)
    {
        var user = await admin.SetPlanAsync(userName, plan, days, cancellationToken);
        await output.WriteLineAsync(user.Plan == AccountPlan.Plus
            ? $"Plus ativo para \"{user.UserName}\"" + (user.PlanExpiresAtUtc is { } until ? $" até {until:yyyy-MM-dd HH:mm} UTC." : ", sem prazo.")
            : $"\"{user.UserName}\" voltou ao plano grátis.");
        return 0;
    }

    private async Task<int> PlanPrefixAsync(string prefix, AccountPlan plan, CancellationToken cancellationToken)
    {
        var changed = await admin.SetPlanByPrefixAsync(prefix, plan, cancellationToken);
        await output.WriteLineAsync($"{changed} conta(s) com o prefixo \"{prefix}\" agora no plano {(plan == AccountPlan.Plus ? "Plus, sem prazo" : "grátis")}.");
        return 0;
    }

    private async Task<int> DisableAsync(string userName, CancellationToken cancellationToken)
    {
        await admin.DisableAsync(userName, cancellationToken);
        await output.WriteLineAsync("Conta suspensa. Todas as sessões foram encerradas.");
        return 0;
    }

    private async Task<int> EnableAsync(string userName, CancellationToken cancellationToken)
    {
        await admin.EnableAsync(userName, cancellationToken);
        await output.WriteLineAsync("Conta reativada.");
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
