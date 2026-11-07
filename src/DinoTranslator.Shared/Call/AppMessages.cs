using System.Text.Json;

namespace DinoTranslator.Shared.Call;

public static class AppMessageTypes
{
    /// <summary>A outra pessoa encerrou a chamada.</summary>
    public const string Bye = "bye";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>([Bye], StringComparer.Ordinal);
}

/// <summary>Mensagem do app pelo canal de dados da chamada (JSON, até 16 KB).</summary>
public sealed record AppMessage(string Type);

public static class AppMessages
{
    public const int MaxLength = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(AppMessage message) => JsonSerializer.Serialize(message, Json);

    /// <summary>Mensagem malformada, grande demais ou de tipo desconhecido é descartada.</summary>
    public static AppMessage? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return null;
        }
        try
        {
            var message = JsonSerializer.Deserialize<AppMessage>(text, Json);
            return message?.Type is { } type && AppMessageTypes.All.Contains(type) ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
