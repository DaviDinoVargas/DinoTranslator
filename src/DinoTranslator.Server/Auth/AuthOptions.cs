namespace DinoTranslator.Server.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Limite de contas no servidor; 0 = sem limite.</summary>
    public int MaxUsers { get; set; }

    /// <summary>Cadastro pela rede (POST /api/auth/register). Desligado, só o comando "users add" cria contas.</summary>
    public bool OpenRegistration { get; set; } = true;

    /// <summary>Se preenchido, o cadastro pela rede exige este código (convite para um grupo fechado de pessoas).</summary>
    public string? RegistrationCode { get; set; }

    public int RegisterPermitsPerHour { get; set; } = 5;

    /// <summary>
    /// Curto de propósito: plano e suspensão vão dentro do token, e cada instância confere o
    /// carimbo da sessão com cache de poucos segundos.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 10;

    public int RefreshTokenDays { get; set; } = 30;

    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    public int LoginPermitsPerMinute { get; set; } = 10;

    public int RefreshPermitsPerMinute { get; set; } = 30;
}
