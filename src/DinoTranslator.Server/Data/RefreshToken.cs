namespace DinoTranslator.Server.Data;

/// <summary>
/// Refresh token de uma sessão. Só o hash SHA-256 é guardado. Cada uso troca o token
/// por um novo da mesma sessão; apresentar de novo um token já trocado revoga a sessão.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public Guid SessionId { get; set; }

    public required byte[] TokenHash { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Quando foi trocado por um token novo (rotação).</summary>
    public DateTime? UsedAtUtc { get; set; }

    /// <summary>Quando a sessão foi encerrada (saída, troca de senha ou reuso detectado).</summary>
    public DateTime? RevokedAtUtc { get; set; }
}
