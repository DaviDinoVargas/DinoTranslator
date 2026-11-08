using DinoTranslator.Shared;

namespace DinoTranslator.Desktop.Services.Translation.Local;

/// <summary>
/// Motor local: reconhecimento (Whisper) e tradução (NLLB) neste computador, sem conta nem
/// chave e sem o áudio sair da máquina.
/// </summary>
public sealed class LocalSpeechTranslationEngine(Func<LocalSpeechRuntime> runtime) : ISpeechTranslationEngine
{
    public async Task<ISpeechTranslationStream> StartAsync(SpeechStreamOptions options, CancellationToken cancellationToken)
    {
        // Carregar os modelos leva alguns segundos na primeira chamada: fora da thread da janela.
        var loaded = await Task.Run(runtime, cancellationToken);
        return new Stream(loaded, options);
    }

    public bool CanTranslateText => true;

    public Task<string?> TranslateTextAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) =>
        Task.Run<string?>(() => runtime().Translator.Translate(text, sourceLanguage, targetLanguage, cancellationToken), cancellationToken);

    /// <summary>
    /// Fala de uma pessoa: o segmentador fecha os trechos pelo volume e a linha de montagem
    /// reconhece (placa de vídeo) e traduz (processador) ao mesmo tempo.
    /// </summary>
    private sealed class Stream : ISpeechTranslationStream
    {
        private readonly SpeechRecognizer _recognizer;
        private readonly SpeechSegmenter _segmenter = new();
        private readonly SpeechPipeline _pipeline;

        public Stream(LocalSpeechRuntime runtime, SpeechStreamOptions options)
        {
            _recognizer = runtime.CreateRecognizer(options.SourceLanguage, options.Phrases);
            var translate = Languages.NeedsTranslation(options.SourceLanguage, options.TargetLanguage)
                ? (text, token) => Task.Run(() => runtime.Translator.Translate(text, options.SourceLanguage, options.TargetLanguage, token), token)
                : (Func<string, CancellationToken, Task<string>>?)null;
            _pipeline = new SpeechPipeline((segment, token) => runtime.TranscribeAsync(_recognizer, segment.Samples, segment.Final, token), translate);
            _pipeline.Updated += update => Updated?.Invoke(update);
            _pipeline.Failed += () => Failed?.Invoke("O motor local parou de responder. A chamada continua, sem legendas.");
        }

        public event Action<SpeechUpdate>? Updated;

        public event Action<string>? Failed;

        public void Write(byte[] pcm)
        {
            if (_segmenter.Append(pcm) is { } segment)
            {
                _pipeline.Enqueue(segment);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _pipeline.DisposeAsync();
            await _recognizer.DisposeAsync();
        }
    }
}
