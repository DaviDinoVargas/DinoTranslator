using DinoTranslator.Shared;
using DinoTranslator.Shared.Auth;

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

    /// <summary>Plano da conta. A versão paga (grupos, chat, chamadas em grupo) exige Plus válido.</summary>
    public AccountPlan Plan { get; set; }

    /// <summary>Até quando o Plus vale; <c>null</c> = sem prazo.</summary>
    public DateTime? PlanExpiresAtUtc { get; set; }

    /// <summary>Conta suspensa pelo dono do servidor: não entra e perde as sessões abertas.</summary>
    public DateTime? DisabledAtUtc { get; set; }

    public bool HasPlus(DateTime nowUtc) => Plan == AccountPlan.Plus && (PlanExpiresAtUtc is not { } until || until > nowUtc);
}
