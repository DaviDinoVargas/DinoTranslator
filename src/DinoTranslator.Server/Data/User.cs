using DinoTranslator.Shared;

namespace DinoTranslator.Server.Data;

public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Sempre em minúsculas (ver <see cref="Auth.Credentials.NormalizeUserName"/>).</summary>
    public required string UserName { get; set; }

    public required string DisplayName { get; set; }

    public required string Language { get; set; }

    public Gender Gender { get; set; }

    public required string PasswordHash { get; set; }

    /// <summary>Muda quando a senha muda; tokens emitidos com o carimbo antigo deixam de valer.</summary>
    public Guid SecurityStamp { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTime? LockoutEndUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
