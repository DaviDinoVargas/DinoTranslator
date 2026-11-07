using System.Buffers.Binary;
using System.Buffers.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DinoTranslator.Shared.Call;

[JsonConverter(typeof(JsonStringEnumConverter<CallCodeKind>))]
public enum CallCodeKind
{
    Invite = 1,
    Answer = 2,
}

/// <summary>Conteúdo de um código de convite ou de resposta.</summary>
/// <param name="CallId">128 bits aleatórios em hexadecimal; a resposta repete o id do convite.</param>
/// <param name="Sdp">Descrição de sessão WebRTC só com o canal de dados.</param>
public sealed record CallCode(
    int Version,
    CallCodeKind Kind,
    string CallId,
    DateTimeOffset CreatedAt,
    Profile Profile,
    string Sdp);

public sealed class CallCodeException(string message) : Exception(message);

/// <summary>
/// Monta e lê os códigos trocados pelo mensageiro: <c>DT1.</c> + base64url de JSON comprimido.
/// Tudo o que vem de fora é tratado como hostil: tamanho limitado antes e depois de
/// descomprimir, campos desconhecidos recusados e cada valor validado.
/// </summary>
public static partial class CallCodes
{
    public const string Prefix = "DT1.";
    public const int CurrentVersion = 1;
    public const int MaxEncodedLength = 8 * 1024;
    public const int MaxDecodedBytes = 32 * 1024;

    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(30);

    /// <summary>Tolerância para relógios dos dois computadores fora de sincronia.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string NewCallId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static string Encode(CallCode code)
    {
        Validate(code);
        var json = JsonSerializer.SerializeToUtf8Bytes(code, Json);
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(json);
        }
        return Prefix + Base64Url.EncodeToString(compressed.ToArray());
    }

    /// <summary>Lê um código colado pela pessoa; espaços e quebras de linha são ignorados.</summary>
    public static CallCode Decode(string? text, CallCodeKind expectedKind, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxEncodedLength * 2)
        {
            throw new CallCodeException("Cole o código completo recebido pelo mensageiro.");
        }

        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (!compact.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CallCodeException("Isto não é um código do DinoTranslator.");
        }
        if (compact.Length > MaxEncodedLength)
        {
            throw Tampered();
        }

        CallCode? code;
        try
        {
            var compressed = Base64Url.DecodeFromChars(compact.AsSpan(Prefix.Length));
            code = JsonSerializer.Deserialize<CallCode>(Inflate(compressed), Json);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException)
        {
            throw Tampered();
        }

        Validate(code);

        if (code!.Kind != expectedKind)
        {
            throw new CallCodeException(expectedKind == CallCodeKind.Invite
                ? "Este é um código de resposta. Aqui vai o código de convite."
                : "Este é um código de convite. Aqui vai o código de resposta.");
        }
        if (code.CreatedAt > now + ClockTolerance || now - code.CreatedAt > Validity + ClockTolerance)
        {
            throw new CallCodeException("Este código expirou. Peça um novo.");
        }
        return code;
    }

    public static void Validate(CallCode? code)
    {
        if (code is null)
        {
            throw Tampered();
        }
        if (code.Version != CurrentVersion)
        {
            throw new CallCodeException("Código de outra versão do DinoTranslator. Atualizem os dois apps.");
        }
        if (!Enum.IsDefined(code.Kind) || code.CallId is null || !CallIdPattern().IsMatch(code.CallId))
        {
            throw Tampered();
        }
        if (Profile.Validate(code.Profile) is { } profileError)
        {
            throw new CallCodeException($"Perfil inválido no código: {profileError}");
        }
        SessionDescriptions.Validate(code.Sdp);
    }

    /// <summary>
    /// 12 dígitos derivados das impressões digitais DTLS dos dois lados. Se os dois veem o
    /// mesmo número, ninguém trocou as chaves no caminho.
    /// </summary>
    public static string SecurityCode(string localFingerprint, string remoteFingerprint)
    {
        var ordered = string.Join('|', new[] { localFingerprint, remoteFingerprint }.Order(StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(ordered));
        var digits = (BinaryPrimitives.ReadUInt64BigEndian(hash) % 1_000_000_000_000UL).ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
        return $"{digits[..4]} {digits[4..8]} {digits[8..]}";
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var deflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var buffer = new byte[MaxDecodedBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = deflate.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }
        if (total > MaxDecodedBytes)
        {
            throw new InvalidDataException("Conteúdo grande demais.");
        }
        return buffer[..total];
    }

    private static CallCodeException Tampered() => new("Código incompleto ou alterado. Copie de novo a mensagem inteira.");

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CallIdPattern();
}
