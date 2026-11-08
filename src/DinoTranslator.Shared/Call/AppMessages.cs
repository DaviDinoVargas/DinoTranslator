using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DinoTranslator.Shared.Call;

/// <summary>Mensagem do app pelo canal de dados da chamada (JSON, até 16 KB). O campo "type" diz qual é.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(CaptionMessage), "caption")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
[JsonDerivedType(typeof(TextMessage), "text")]
[JsonDerivedType(typeof(CorrectionMessage), "correction")]
public abstract record AppMessage;

/// <summary>Primeira mensagem de cada lado: diz se este computador tem motor de tradução.</summary>
public sealed record HelloMessage(bool CanTranslate) : AppMessage;

/// <summary>A outra pessoa encerrou a chamada.</summary>
public sealed record ByeMessage : AppMessage;

/// <summary>Quem falou, do ponto de vista de quem envia a legenda.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaptionSpeaker>))]
public enum CaptionSpeaker
{
    /// <summary>A própria pessoa que envia (o computador que traduz).</summary>
    Sender = 1,

    /// <summary>A pessoa que recebe a legenda.</summary>
    Receiver = 2,
}

/// <summary>
/// Legenda provisória ou final de um trecho falado ou escrito, enviada por quem traduz.
/// <paramref name="Edited"/>: a pessoa corrigiu o texto de um trecho que já era final.
/// </summary>
public sealed record CaptionMessage(
    CaptionSpeaker Speaker,
    string SegmentId,
    int Revision,
    string SourceLanguage,
    string SourceText,
    string TargetLanguage,
    string? TranslatedText,
    bool Final,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Edited = false) : AppMessage
{
    public const int MaxTextLength = 2_000;
}

/// <summary>
/// Mensagem escrita no chat, para quem traduz devolver como legenda (com a tradução). O id é
/// escolhido por quem escreve e começa com "e-".
/// </summary>
public sealed record TextMessage(string Id, string Text) : AppMessage
{
    public const string IdPrefix = "e-";
}

/// <summary>A pessoa corrigiu o texto de uma fala (ou mensagem) dela; quem traduz refaz a tradução.</summary>
public sealed record CorrectionMessage(string SegmentId, string Text) : AppMessage;

public static partial class AppMessages
{
    public const int MaxLength = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(AppMessage message) => JsonSerializer.Serialize(message, Json);

    /// <summary>Mensagem malformada, grande demais, de tipo desconhecido ou com campo inválido é descartada.</summary>
    public static AppMessage? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return null;
        }
        try
        {
            var message = JsonSerializer.Deserialize<AppMessage>(text, Json);
            return message is not null && IsValid(message) ? message : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Texto que pode ir para a tela: tamanho limitado, sem controle nem formatação invisível.</summary>
    public static bool IsDisplayableText(string? text) =>
        text is not null
        && text.Length <= CaptionMessage.MaxTextLength
        && !text.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format);

    private static bool IsValid(AppMessage message) => message switch
    {
        HelloMessage or ByeMessage => true,
        CaptionMessage caption => IsValid(caption),
        TextMessage text => text.Id is not null
            && text.Id.StartsWith(TextMessage.IdPrefix, StringComparison.Ordinal)
            && SegmentIdPattern().IsMatch(text.Id)
            && IsWrittenText(text.Text),
        CorrectionMessage correction => correction.SegmentId is not null
            && SegmentIdPattern().IsMatch(correction.SegmentId)
            && IsWrittenText(correction.Text),
        _ => false,
    };

    /// <summary>Texto escrito por uma pessoa: exibível e com alguma letra ou número.</summary>
    public static bool IsWrittenText(string? text) =>
        IsDisplayableText(text) && text!.Any(char.IsLetterOrDigit);

    private static bool IsValid(CaptionMessage caption) =>
        Enum.IsDefined(caption.Speaker)
        && caption.SegmentId is not null
        && SegmentIdPattern().IsMatch(caption.SegmentId)
        && caption.Revision is >= 0 and <= 1_000_000
        && Languages.IsSupported(caption.SourceLanguage)
        && Languages.IsSupported(caption.TargetLanguage)
        && IsDisplayableText(caption.SourceText)
        && (caption.TranslatedText is null || IsDisplayableText(caption.TranslatedText));

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentIdPattern();
}
