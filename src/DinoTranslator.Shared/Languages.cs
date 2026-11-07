namespace DinoTranslator.Shared;

/// <param name="Code">Código BCP 47 usado pelos motores de reconhecimento e tradução.</param>
/// <param name="NativeName">Nome do idioma escrito nele mesmo.</param>
/// <param name="Tag">Sigla curta para a interface (ex.: <c>→ RU</c>).</param>
public sealed record Language(string Code, string NativeName, string Tag);

/// <summary>
/// Idiomas suportados. Cada pessoa fala um idioma por chamada; a fala é traduzida
/// para o idioma da outra pessoa, em qualquer combinação.
/// </summary>
public static class Languages
{
    public const string PortugueseBrazil = "pt-BR";
    public const string English = "en-US";
    public const string Ukrainian = "uk-UA";
    public const string Russian = "ru-RU";
    public const string Spanish = "es-ES";

    public static IReadOnlyList<Language> All { get; } =
    [
        new(PortugueseBrazil, "Português (Brasil)", "PT"),
        new(English, "English", "EN"),
        new(Ukrainian, "Українська", "UK"),
        new(Russian, "Русский", "RU"),
        new(Spanish, "Español", "ES"),
    ];

    public static bool IsSupported(string? code) => code is not null && All.Any(l => l.Code == code);

    public static Language Get(string code) =>
        All.FirstOrDefault(l => l.Code == code)
        ?? throw new ArgumentOutOfRangeException(nameof(code), code, "Idioma não suportado.");

    /// <summary>Mesmo idioma dos dois lados: a legenda mostra só a transcrição.</summary>
    public static bool NeedsTranslation(string fromCode, string toCode) => fromCode != toCode;
}
