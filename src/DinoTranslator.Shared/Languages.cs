namespace DinoTranslator.Shared;

/// <summary>
/// Idiomas suportados. Cada usuário fala sempre o mesmo idioma, definido na conta.
/// </summary>
public static class Languages
{
    public const string PortugueseBrazil = "pt-BR";
    public const string Russian = "ru-RU";

    public static IReadOnlyList<string> All { get; } = [PortugueseBrazil, Russian];

    public static bool IsSupported(string? code) => code is PortugueseBrazil or Russian;

    /// <summary>Idioma para o qual a fala de quem usa <paramref name="code"/> é traduzida.</summary>
    public static string TargetOf(string code) => code switch
    {
        PortugueseBrazil => Russian,
        Russian => PortugueseBrazil,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Idioma não suportado."),
    };
}
