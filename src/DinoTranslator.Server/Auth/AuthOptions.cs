namespace DinoTranslator.Server.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Limite rígido de contas. O app é só para duas pessoas.</summary>
    public int MaxUsers { get; set; } = 2;

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;

    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    public int LoginPermitsPerMinute { get; set; } = 10;

    public int RefreshPermitsPerMinute { get; set; } = 30;
}
