using System.Text.RegularExpressions;

namespace DinoTranslator.Shared.Call;

/// <summary>
/// Validação da descrição de sessão (SDP) que vai dentro do código. O código só abre o
/// canal de dados; câmera, áudio e tela são negociados depois, por dentro da conexão cifrada.
/// </summary>
public static partial class SessionDescriptions
{
    public const int MaxLength = 8_000;
    public const int MaxLines = 80;
    public const int MaxLineLength = 400;
    public const int MaxCandidates = 16;

    /// <summary>Lança <see cref="CallCodeException"/> se a SDP não for a esperada.</summary>
    public static void Validate(string? sdp)
    {
        if (string.IsNullOrEmpty(sdp) || sdp.Length > MaxLength)
        {
            throw Invalid();
        }
        if (sdp.Any(c => c is not ('\r' or '\n') && (c < ' ' || c > '~')))
        {
            throw Invalid();
        }

        var lines = Lines(sdp);
        if (lines.Length > MaxLines || lines.Any(l => l.Length > MaxLineLength) || lines[0] != "v=0")
        {
            throw Invalid();
        }

        var media = lines.Where(l => l.StartsWith("m=", StringComparison.Ordinal)).ToList();
        if (media is not [var application] || !application.StartsWith("m=application ", StringComparison.Ordinal))
        {
            throw Invalid();
        }
        if (lines.Count(l => l.StartsWith("a=candidate:", StringComparison.Ordinal)) > MaxCandidates)
        {
            throw Invalid();
        }
        if (!lines.Any(l => l.StartsWith("a=ice-ufrag:", StringComparison.Ordinal))
            || !lines.Any(l => l.StartsWith("a=ice-pwd:", StringComparison.Ordinal))
            || !lines.Any(l => l.StartsWith("a=setup:", StringComparison.Ordinal)))
        {
            throw Invalid();
        }

        _ = Fingerprint(sdp);
    }

    /// <summary>Impressão digital SHA-256 do certificado DTLS, em maiúsculas.</summary>
    public static string Fingerprint(string sdp)
    {
        var fingerprints = Lines(sdp)
            .Select(l => FingerprintLine().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups["value"].Value.ToUpperInvariant())
            .Distinct()
            .ToList();
        return fingerprints is [var single] ? single : throw Invalid();
    }

    private static string[] Lines(string sdp) =>
        sdp.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

    private static CallCodeException Invalid() => new("Código incompleto ou alterado.");

    [GeneratedRegex("^a=fingerprint:sha-256 (?<value>[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){31})$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintLine();
}
