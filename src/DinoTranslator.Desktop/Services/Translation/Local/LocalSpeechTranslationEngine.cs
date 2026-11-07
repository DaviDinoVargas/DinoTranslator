using System.Threading.Channels;
using DinoTranslator.Shared;
using Whisper.net;

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

    private sealed class Stream : ISpeechTranslationStream
    {
        private readonly LocalSpeechRuntime _runtime;
        private readonly SpeechStreamOptions _options;
        private readonly WhisperProcessor _recognizer;
        private readonly SpeechSegmenter _segmenter = new();
        private readonly Channel<SpeechSegment> _segments = Channel.CreateUnbounded<SpeechSegment>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private readonly bool _translate;
        private int _segment = -1;
        private int _revision;
        private int _pending;

        public Stream(LocalSpeechRuntime runtime, SpeechStreamOptions options)
        {
            _runtime = runtime;
            _options = options;
            _translate = Languages.NeedsTranslation(options.SourceLanguage, options.TargetLanguage);
            _recognizer = runtime.CreateRecognizer(options.SourceLanguage, options.Phrases);
            _worker = Task.Run(ProcessAsync);
        }

        public event Action<SpeechUpdate>? Updated;

        public event Action<string>? Failed;

        public void Write(byte[] pcm)
        {
            if (_segmenter.Append(pcm) is { } segment && _segments.Writer.TryWrite(segment))
            {
                Interlocked.Increment(ref _pending);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _segments.Writer.TryComplete();
            await _stop.CancelAsync();
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Encerrando: trecho em andamento é descartado.
            }
            await _recognizer.DisposeAsync();
            _stop.Dispose();
        }

        private async Task ProcessAsync()
        {
            try
            {
                await foreach (var segment in _segments.Reader.ReadAllAsync(_stop.Token))
                {
                    // Provisória atrasada: se já chegou coisa mais nova, ela fica para trás.
                    if (Interlocked.Decrement(ref _pending) > 0 && !segment.Final)
                    {
                        continue;
                    }
                    await ProcessAsync(segment);
                }
            }
            catch (OperationCanceledException)
            {
                // Chamada encerrada.
            }
            catch (Exception)
            {
                Failed?.Invoke("O motor local parou de responder. A chamada continua, sem legendas.");
            }
        }

        private async Task ProcessAsync(SpeechSegment segment)
        {
            var text = await _runtime.TranscribeAsync(_recognizer, segment.Samples, _stop.Token);
            if (text.Length == 0)
            {
                return;
            }

            // A tradução local leva de 1 a 4 s por frase no processador: só o trecho final é
            // traduzido; a provisória mostra o texto reconhecido enquanto a pessoa fala.
            var translated = _translate && segment.Final
                ? await Task.Run(() => _runtime.Translator.Translate(text, _options.SourceLanguage, _options.TargetLanguage), _stop.Token)
                : null;

            if (segment.Index != _segment)
            {
                _segment = segment.Index;
                _revision = 0;
            }
            Updated?.Invoke(new SpeechUpdate(segment.Index, _revision++, text, translated, segment.Final));
        }
    }
}
