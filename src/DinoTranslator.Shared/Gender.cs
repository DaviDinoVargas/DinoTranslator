using System.Text.Json.Serialization;

namespace DinoTranslator.Shared;

/// <summary>
/// Gênero de quem fala. O tradutor usa para concordância
/// (russo: "я пошёл" / "я пошла"; português: "cansado" / "cansada").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Gender>))]
public enum Gender
{
    Unspecified = 0,
    Male = 1,
    Female = 2,
}
