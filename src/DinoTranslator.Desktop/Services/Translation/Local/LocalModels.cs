using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace DinoTranslator.Desktop.Services.Translation.Local;

/// <summary>Arquivo de modelo baixado de uma versão fixa do repositório e conferido pelo SHA-256.</summary>
public sealed record ModelFile(string FileName, Uri Source, long Size, string Sha256);

/// <summary>Tamanho do modelo de reconhecimento de fala.</summary>
public enum WhisperModel
{
    /// <summary>Whisper large-v3-turbo (q5_0): reconhece melhor, pede mais da placa de vídeo.</summary>
    Turbo,

    /// <summary>Whisper small (q5_1): mais leve e mais rápido, erra um pouco mais.</summary>
    Small,
}

/// <summary>
/// Modelos do motor local. Os endereços apontam para uma versão fixa de cada repositório e
/// cada arquivo é conferido pelo tamanho e pelo SHA-256: um arquivo diferente é recusado.
/// </summary>
public static class LocalModels
{
    private const string WhisperRepository = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/";
    private const string NllbRepository = "https://huggingface.co/Xenova/nllb-200-distilled-600M/resolve/261c31d1a5732c67cdd16d80e8d6088507c7ccea/";

    public static ModelFile WhisperTurbo { get; } = new(
        "ggml-large-v3-turbo-q5_0.bin",
        new Uri(WhisperRepository + "ggml-large-v3-turbo-q5_0.bin"),
        574_041_195,
        "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2");

    public static ModelFile WhisperSmall { get; } = new(
        "ggml-small-q5_1.bin",
        new Uri(WhisperRepository + "ggml-small-q5_1.bin"),
        190_085_487,
        "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb");

    /// <summary>NLLB-200 (600M, destilado, quantizado): traduz entre os cinco idiomas em qualquer direção.</summary>
    public static ModelFile TranslatorEncoder { get; } = new(
        "nllb-encoder-quantized.onnx",
        new Uri(NllbRepository + "onnx/encoder_model_quantized.onnx"),
        419_120_483,
        "5cde664eacba07a62f198857ec6c06e09572b1ebb77c8137f1fa99ac604a3a28");

    public static ModelFile TranslatorDecoder { get; } = new(
        "nllb-decoder-merged-quantized.onnx",
        new Uri(NllbRepository + "onnx/decoder_model_merged_quantized.onnx"),
        475_505_771,
        "dd66608c2a4194e78f95548fa0e64f24302303698c5b09fa8e1f9e16ec00676b");

    public static ModelFile TranslatorVocabulary { get; } = new(
        "nllb-sentencepiece.bpe.model",
        new Uri(NllbRepository + "sentencepiece.bpe.model"),
        4_852_054,
        "14bb8dfb35c0ffdea7bc01e56cea38b9e3d5efcdcb9c251d6b40538e1aab555a");

    public static ModelFile Whisper(WhisperModel model) => model == WhisperModel.Small ? WhisperSmall : WhisperTurbo;

    public static IReadOnlyList<ModelFile> Required(WhisperModel model) =>
        [Whisper(model), TranslatorEncoder, TranslatorDecoder, TranslatorVocabulary];

    public static long DownloadSize(WhisperModel model) => Required(model).Sum(f => f.Size);

    public static IReadOnlyList<ModelFile> All { get; } =
        [WhisperTurbo, WhisperSmall, TranslatorEncoder, TranslatorDecoder, TranslatorVocabulary];
}

/// <summary>
/// Pasta dos modelos: %LOCALAPPDATA%\DinoTranslator\modelos por padrão, ou outra escolhida
/// nas configurações (ex.: em outro disco).
/// </summary>
public sealed class ModelFolder(string path)
{
    public const int MaxPathLength = 200;

    /// <summary>Arquivos já conferidos nesta sessão: o SHA-256 de 1,5 GB leva alguns segundos.</summary>
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WrittenUtc)> Verified = new(StringComparer.OrdinalIgnoreCase);

    public string Path { get; } = path;

    public string PathOf(ModelFile file) => System.IO.Path.Combine(Path, file.FileName);

    /// <summary>Instalado = existe com o tamanho certo; o SHA-256 é conferido ao baixar e ao carregar.</summary>
    public bool IsInstalled(ModelFile file)
    {
        var info = new FileInfo(PathOf(file));
        return info.Exists && info.Length == file.Size;
    }

    public bool IsInstalled(WhisperModel model) => LocalModels.Required(model).All(IsInstalled);

    /// <summary>
    /// Confere o SHA-256 antes de carregar: a pasta pode ficar fora da área protegida do
    /// usuário, e um modelo trocado não pode chegar ao motor.
    /// </summary>
    public void Verify(ModelFile file)
    {
        var info = new FileInfo(PathOf(file));
        if (!info.Exists || info.Length != file.Size)
        {
            throw new ModelDownloadException($"{file.FileName} não está na pasta dos modelos. Baixe de novo nas configurações.");
        }
        if (Verified.TryGetValue(info.FullName, out var known) && known == (info.Length, info.LastWriteTimeUtc))
        {
            return;
        }

        using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (actual != file.Sha256)
        {
            throw new ModelDownloadException($"{file.FileName} foi alterado e não será usado. Baixe de novo nas configurações.");
        }
        Verified[info.FullName] = (info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Apaga só os arquivos do catálogo; a pasta só some se ficar vazia.</summary>
    public void Remove()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }
        foreach (var file in LocalModels.All)
        {
            File.Delete(PathOf(file));
            File.Delete(PathOf(file) + ".part");
        }
        if (!Directory.EnumerateFileSystemEntries(Path).Any())
        {
            Directory.Delete(Path);
        }
    }

    /// <summary>Espaço livre no disco da pasta, ou <c>null</c> se não der para saber.</summary>
    public long? FreeSpace()
    {
        var root = System.IO.Path.GetPathRoot(Path);
        return root is not null && new DriveInfo(root) is { IsReady: true } drive ? drive.AvailableFreeSpace : null;
    }

    /// <summary>Pasta aceita: caminho completo num disco fixo deste computador, sem pasta de rede.</summary>
    public static string? ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength
            || !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "Escolha uma pasta num disco deste computador.";
        }
        var root = System.IO.Path.GetPathRoot(path);
        if (root is null || new DriveInfo(root) is not { IsReady: true, DriveType: DriveType.Fixed })
        {
            return "Escolha uma pasta num disco fixo deste computador (não de rede nem removível).";
        }
        return null;
    }
}
