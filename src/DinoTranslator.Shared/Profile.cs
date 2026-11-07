using System.Globalization;
using System.Text.Json.Serialization;

namespace DinoTranslator.Shared;

/// <summary>Como a tradução trata a outra pessoa (ты/вы, ти/ви, tú/usted, você/o senhor).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Formality>))]
public enum Formality
{
    Informal = 0,
    Formal = 1,
}

/// <summary>
/// Perfil que cada pessoa configura no app. Vai dentro do código da chamada para o outro
/// lado saber o nome, o idioma, a concordância e o fuso de quem está do outro lado.
/// </summary>
/// <param name="TimeZone">Fuso no formato IANA (ex.: <c>America/Sao_Paulo</c>).</param>
public sealed record Profile(string Name, string Language, Gender Gender, Formality Formality, string TimeZone)
{
    public const int MaxNameLength = 40;
    public const int MaxTimeZoneLength = 64;

    /// <summary>Devolve o motivo da recusa, ou <c>null</c> se o perfil for válido.</summary>
    public static string? Validate(Profile? profile)
    {
        if (profile is null)
        {
            return "Perfil ausente.";
        }
        if (string.IsNullOrEmpty(profile.Name) || profile.Name.Length > MaxNameLength || profile.Name != profile.Name.Trim())
        {
            return $"O nome precisa ter de 1 a {MaxNameLength} caracteres.";
        }
        // Controle e formatação invisível (ex.: inversão de direção do texto) permitiriam disfarçar o nome.
        if (profile.Name.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
        {
            return "O nome tem caracteres inválidos.";
        }
        if (!Languages.IsSupported(profile.Language))
        {
            return "Idioma inválido.";
        }
        if (!Enum.IsDefined(profile.Gender) || !Enum.IsDefined(profile.Formality))
        {
            return "Gênero ou tratamento inválido.";
        }
        if (string.IsNullOrEmpty(profile.TimeZone)
            || profile.TimeZone.Length > MaxTimeZoneLength
            || !TimeZoneInfo.TryFindSystemTimeZoneById(profile.TimeZone, out _))
        {
            return "Fuso horário inválido.";
        }
        return null;
    }
}
