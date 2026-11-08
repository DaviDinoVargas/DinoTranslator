namespace DinoTranslator.Installation;

/// <summary>
/// Tudo o que os motores de tradução usam, com endereço fixo, tamanho e SHA-256. Os pacotes
/// convertidos para o CTranslate2 (gerados por tools/criar-extras.ps1) ficam na release
/// "componentes-1" deste projeto; os modelos originais vêm das versões fixas dos repositórios.
/// Licenças: Whisper e faster-whisper (MIT), Silero VAD (MIT), OPUS-MT (CC BY 4.0), NLLB-200
/// (CC BY-NC 4.0, uso não comercial), Python (PSF), CTranslate2 (MIT) e as bibliotecas CUDA
/// redistribuíveis da NVIDIA.
/// </summary>
public static class ExtrasCatalog
{
    private const string ReleaseAddress = "https://github.com/DaviDinoVargas/DinoTranslator/releases/download/componentes-1/";
    private const string WhisperRepository = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/";
    private const string NllbRepository = "https://huggingface.co/Xenova/nllb-200-distilled-600M/resolve/261c31d1a5732c67cdd16d80e8d6088507c7ccea/";

    /// <summary>Pasta (dentro de "extras") dos modelos no formato original: Whisper (whisper.cpp) e NLLB em ONNX.</summary>
    public const string OriginalModels = "modelos";

    public static IReadOnlyList<ExtraComponent> All { get; } =
    [
        // Modelos originais do app (os mesmos que o app baixa nos ajustes).
        Original("whisper-turbo", "Whisper turbo", WhisperRepository + "ggml-large-v3-turbo-q5_0.bin", "ggml-large-v3-turbo-q5_0.bin", 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
        Original("whisper-small", "Whisper small", WhisperRepository + "ggml-small-q5_1.bin", "ggml-small-q5_1.bin", 190_085_487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"),
        Original("whisper-large", "Whisper large-v3", WhisperRepository + "ggml-large-v3-q5_0.bin", "ggml-large-v3-q5_0.bin", 1_081_140_203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1"),
        Original("nllb-codificador", "NLLB 600M (codificador)", NllbRepository + "onnx/encoder_model_quantized.onnx", "nllb-encoder-quantized.onnx", 419_120_483, "5cde664eacba07a62f198857ec6c06e09572b1ebb77c8137f1fa99ac604a3a28"),
        Original("nllb-decodificador", "NLLB 600M (decodificador)", NllbRepository + "onnx/decoder_model_merged_quantized.onnx", "nllb-decoder-merged-quantized.onnx", 475_505_771, "dd66608c2a4194e78f95548fa0e64f24302303698c5b09fa8e1f9e16ec00676b"),
        Original("nllb-vocabulario", "NLLB 600M (vocabulário)", NllbRepository + "sentencepiece.bpe.model", "nllb-sentencepiece.bpe.model", 4_852_054, "14bb8dfb35c0ffdea7bc01e56cea38b9e3d5efcdcb9c251d6b40538e1aab555a"),

        // Motores extras.
        Release("motor-python", "Motor auxiliar (Python, faster-whisper e CTranslate2)", 141_817_514, "7a2b531c1f64c3f8a7b9a0ddddb227113087b195355bb1069301efa3669a7cf1", "python"),
        Release("motor-cuda12", "Bibliotecas CUDA da NVIDIA (cuBLAS e cuDNN)", 1_322_736_755, "91dcd652ecaa12fc32d6db50122a7a19d6217407f2396d96409932f833401dd3", "cuda12"),
        Release("silero", "Detector de fala Silero", 1_946_170, "09ad744e4902ff6699e9968d74992eba7334dd5038e536bf970b0f092d020ef5", "silero"),
        Release("faster-whisper-turbo", "faster-whisper turbo", 1_619_042_355, "eefc77b85108202095e2c157027ae8de2fbb00efbe2442da7be2ccfe636b316f", "faster-whisper-turbo"),
        Release("ct2-nllb-1.3b", "NLLB 1.3B", 1_386_081_643, "05707ca3c4a5f7f0e559fd4501360d4e814f59486fa1ca6e9781850b86ec893d", "ct2/nllb-1.3b"),
        Release("ct2-nllb-600m", "NLLB 600M (CTranslate2)", 626_734_682, "77fcbf6a00c6d57a34c2e2a244879ea7cb3282e750767ac10429d85e1112ba42", "ct2/nllb-600m"),
        Release("ct2-opus-en-es", "MarianMT inglês → espanhol", 236_908_481, "9612ccc0650a80745ea1658ab4ed886161b86237e6c829bc6314c9a57cf8bfa7", "ct2/opus-en-es"),
        Release("ct2-opus-en-pt", "MarianMT inglês → português", 236_646_773, "610474a0c7a6c8c2e28be63c70abb2e198690b959c2c0cfef5191ef37cd19db4", "ct2/opus-en-pt"),
        Release("ct2-opus-en-zle", "MarianMT inglês → russo e ucraniano", 243_748_121, "a2abf9dfc86fe42d4367c39cbd242d07385aa14f21496c8b89aae2299464c038", "ct2/opus-en-zle"),
        Release("ct2-opus-es-zle", "MarianMT espanhol → russo e ucraniano", 243_760_599, "8e61a3880ddb33728dc18125f8b3db63c62e51f194433f612917b06b578e3c63", "ct2/opus-es-zle"),
        Release("ct2-opus-itc-itc", "MarianMT português ↔ espanhol", 216_513_040, "f0221609dbdd1498bd0ad07aa13f0d7a67d1f1a1a76971bfcf323ba37567d1c6", "ct2/opus-itc-itc"),
        Release("ct2-opus-pt-zle", "MarianMT português → russo e ucraniano", 243_508_782, "12fa7374235f59c43ab17f3c9ee3819bf24a267c2317be6674793e5f52b1bac9", "ct2/opus-pt-zle"),
        Release("ct2-opus-romance-en", "MarianMT português e espanhol → inglês", 80_683_425, "73bc9d75528c139859885d5beaa5ac039d0be485e08d91ecad06f66699b19c14", "ct2/opus-romance-en"),
        Release("ct2-opus-zle-en", "MarianMT russo e ucraniano → inglês", 243_169_694, "fc9c67148db4e5072c2d94b0d026ee934bb5ec7cd712d01f7a8a09523664de29", "ct2/opus-zle-en"),
        Release("ct2-opus-zle-es", "MarianMT russo e ucraniano → espanhol", 243_696_855, "43e6b6d73afbedfef482336a9b769d0d72d5c85b94fcab4782526ec7176940dd", "ct2/opus-zle-es"),
        Release("ct2-opus-zle-pt", "MarianMT russo e ucraniano → português", 243_509_361, "f50fb06143c8c8722b71519a9578d9acc96dcf3481fbc208872d57c2713a2aae", "ct2/opus-zle-pt"),
        Release("ct2-opus-zle-zle", "MarianMT russo ↔ ucraniano", 217_635_326, "1cb4745c29214626e96f86cedfbb342cfa8f355be63051acfb6db3a03399442f", "ct2/opus-zle-zle"),
    ];

    public static long TotalSize { get; } = All.Sum(c => c.Size);

    private static ExtraComponent Release(string name, string title, long size, string sha256, string folder) =>
        new(name, title, new Uri(ReleaseAddress + name + ".zip"), size, sha256, folder, ExtraKind.Zip);

    private static ExtraComponent Original(string name, string title, string url, string fileName, long size, string sha256) =>
        new(name, title, new Uri(url), size, sha256, OriginalModels, ExtraKind.File, fileName);
}
