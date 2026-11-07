using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinoTranslator.Shared.Call;

/// <summary>Mensagem trocada pelo encontro automático.</summary>
/// <param name="Type"><see cref="RendezvousMessage.Answer"/>: o código de resposta do convidado.</param>
/// <param name="Code">Código no mesmo formato do colado pelo mensageiro (validado do mesmo jeito).</param>
public sealed record RendezvousMessage(string Type, string Code)
{
    public const string Answer = "resposta";
}

/// <summary>
/// Encontro automático da chamada: depois do convite, a resposta volta por um serviço público
/// de mensagens em vez de pelo mensageiro, e os dois computadores começam a se procurar ao
/// mesmo tempo — é isso que atravessa os roteadores das duas casas.
/// <para>
/// Tópico e chave derivam do id da chamada, que só existe nos códigos. O serviço vê um tópico
/// aleatório e bytes cifrados (AES-GCM); sem o convite, ninguém lê nem forja uma mensagem.
/// </para>
/// </summary>
public sealed class CallRendezvous
{
    /// <summary>O ntfy.sh aceita até 4096 bytes por mensagem.</summary>
    public const int MaxMessageLength = 4000;

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly byte[] _key;
    private readonly byte[] _topicBytes;

    public CallRendezvous(string callId)
    {
        var secret = Convert.FromHexString(callId);
        Topic = "dt" + Convert.ToHexStringLower(HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 16, info: "DinoTranslator encontro: topico"u8.ToArray()));
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, info: "DinoTranslator encontro: chave"u8.ToArray());
        _topicBytes = Encoding.ASCII.GetBytes(Topic);
    }

    /// <summary>Nome do canal no serviço: "dt" + 32 dígitos hexadecimais.</summary>
    public string Topic { get; }

    public string Seal(RendezvousMessage message)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var sealedBytes = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(NonceSize, plaintext.Length), sealedBytes.AsSpan(NonceSize + plaintext.Length), _topicBytes);
        }
        var text = Base64Url.EncodeToString(sealedBytes);
        return text.Length <= MaxMessageLength ? text : throw new InvalidOperationException("Mensagem grande demais para o encontro.");
    }

    /// <summary>Abre uma mensagem do serviço; qualquer coisa adulterada ou de fora vira <c>null</c>.</summary>
    public RendezvousMessage? Open(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxMessageLength)
        {
            return null;
        }
        try
        {
            var sealedBytes = Base64Url.DecodeFromChars(text.AsSpan().Trim());
            if (sealedBytes.Length <= NonceSize + TagSize)
            {
                return null;
            }
            var plaintext = new byte[sealedBytes.Length - NonceSize - TagSize];
            using (var aes = new AesGcm(_key, TagSize))
            {
                aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize, plaintext.Length), sealedBytes.AsSpan(^TagSize), plaintext, _topicBytes);
            }
            var message = JsonSerializer.Deserialize<RendezvousMessage>(plaintext, Json);
            return message is { Type: RendezvousMessage.Answer, Code: { Length: > 0 and <= CallCodes.MaxEncodedLength } } ? message : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }
}
