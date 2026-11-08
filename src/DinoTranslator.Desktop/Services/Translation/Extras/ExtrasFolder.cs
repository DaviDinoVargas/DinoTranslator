using System.IO;
using DinoTranslator.Shared;

namespace DinoTranslator.Desktop.Services.Translation.Extras;

/// <summary>
/// Motores extras, instalados pelo instalador numa pasta "extras" ao lado do app: o Python com
/// faster-whisper e CTranslate2, as bibliotecas CUDA, o Silero e os modelos convertidos. Ficam
/// na pasta do próprio app (mesma confiança dos arquivos do app) e são conferidos pelo SHA-256 ao
/// baixar. No build Debug, DINOTRANSLATOR_EXTRAS e DINOTRANSLATOR_PYTHON apontam para outra pasta.
/// </summary>
public sealed class ExtrasFolder(string root, string? python = null)
{
    public string Root { get; } = root;

    public string Python { get; } = python ?? Path.Combine(root, "python", "python.exe");

    /// <summary>O script do motor auxiliar vem com o app (atualiza junto com ele).</summary>
    public string MotorScript { get; } = Path.Combine(AppContext.BaseDirectory, "Motor", "motor.py");

    public string Silero => Path.Combine(Root, "silero", "silero_vad.onnx");

    public string FasterWhisperTurbo => Path.Combine(Root, "faster-whisper-turbo");

    /// <summary>Modelos do formato original (Whisper e NLLB em ONNX) trazidos pelo instalador.</summary>
    public string Models => Path.Combine(Root, "modelos");

    public string Ct2(string name) => Path.Combine(Root, "ct2", name);

    public bool HasPython => File.Exists(Python) && File.Exists(MotorScript);

    /// <summary>Bibliotecas da NVIDIA: na pasta cuda12 ou, em desenvolvimento, nos pacotes do Python.</summary>
    public bool HasCuda => Directory.Exists(Path.Combine(Root, "cuda12"))
        || Directory.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Python)!)!, "Lib", "site-packages", "nvidia"));

    public bool HasSilero => File.Exists(Silero);

    public bool HasModel(string folder) => File.Exists(Path.Combine(folder, "model.bin"));

    public static ExtrasFolder Default()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("DINOTRANSLATOR_EXTRAS") is { Length: > 0 } root)
        {
            return new ExtrasFolder(root, Environment.GetEnvironmentVariable("DINOTRANSLATOR_PYTHON") is { Length: > 0 } python ? python : null);
        }
#endif
        return new ExtrasFolder(Path.Combine(AppContext.BaseDirectory, "extras"));
    }
}

/// <summary>
/// Modelos OPUS-MT (MarianMT) convertidos: cada um cobre um grupo de idiomas numa direção. "zle" são
/// as eslavas orientais (russo e ucraniano) e "itc" as itálicas (português e espanhol).
/// </summary>
public static class OpusModels
{
    public static IReadOnlyList<string> All { get; } =
    [
        "opus-pt-zle", "opus-zle-pt", "opus-en-zle", "opus-zle-en", "opus-en-pt", "opus-romance-en",
        "opus-en-es", "opus-es-zle", "opus-zle-es", "opus-zle-zle", "opus-itc-itc",
    ];

    /// <summary>O modelo que traduz de um idioma para o outro, ou <c>null</c> se não houver.</summary>
    public static string? For(string source, string target)
    {
        var (from, to) = (Group(source), Group(target));
        return (from, to) switch
        {
            ("pt", "zle") => "opus-pt-zle",
            ("zle", "pt") => "opus-zle-pt",
            ("en", "zle") => "opus-en-zle",
            ("zle", "en") => "opus-zle-en",
            ("en", "pt") => "opus-en-pt",
            ("pt" or "es", "en") => "opus-romance-en",
            ("en", "es") => "opus-en-es",
            ("es", "zle") => "opus-es-zle",
            ("zle", "es") => "opus-zle-es",
            ("zle", "zle") => "opus-zle-zle",
            ("pt" or "es", "pt" or "es") => "opus-itc-itc",
            _ => null,
        };
    }

    private static string Group(string language) => language switch
    {
        Languages.Russian or Languages.Ukrainian => "zle",
        _ => language[..2],
    };
}
