#if DEBUG
using System.Buffers.Binary;

namespace DinoTranslator.Desktop.Services.Translation;

/// <summary>
/// Só no build Debug: legendas geradas a partir do volume do áudio, sem serviço externo.
/// Serve para testar a chamada inteira (captura, legendas, canal de dados e tela) sem chave.
/// </summary>
public sealed class SimulatedSpeechTranslationEngine : ISpeechTranslationEngine
{
    public Task<ISpeechTranslationStream> StartAsync(SpeechStreamOptions options, CancellationToken cancellationToken) =>
        Task.FromResult<ISpeechTranslationStream>(new Stream(options));

    private sealed class Stream(SpeechStreamOptions options) : ISpeechTranslationStream
    {
        private const double VoiceThreshold = 0.02;
        private const int SilenceToCloseMs = 600;
        private const int MsPerWord = 200;

        private int _segment;
        private int _revision;
        private double _voiceMs;
        private double _silenceMs;
        private int _words;
        private bool _speaking;

        public event Action<SpeechUpdate>? Updated;

        public event Action<string>? Failed
        {
            add { }
            remove { }
        }

        public void Write(byte[] pcm)
        {
            var samples = pcm.Length / 2;
            if (samples == 0)
            {
                return;
            }
            double sum = 0;
            for (var i = 0; i < samples; i++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)) / 32768.0;
                sum += sample * sample;
            }
            var durationMs = samples / 16.0;
            var voice = Math.Sqrt(sum / samples) > VoiceThreshold;

            if (voice)
            {
                _speaking = true;
                _silenceMs = 0;
                _voiceMs += durationMs;
                var words = 1 + (int)(_voiceMs / MsPerWord);
                if (words != _words)
                {
                    _words = words;
                    Updated?.Invoke(Update(final: false));
                }
            }
            else if (_speaking)
            {
                _silenceMs += durationMs;
                if (_silenceMs >= SilenceToCloseMs)
                {
                    Updated?.Invoke(Update(final: true));
                    _segment++;
                    _revision = 0;
                    _speaking = false;
                    _voiceMs = 0;
                    _words = 0;
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private SpeechUpdate Update(bool final)
        {
            var words = string.Join(' ', Enumerable.Range(1, _words).Select(i => $"palavra{i}"));
            return new SpeechUpdate(
                _segment,
                _revision++,
                $"[{options.SourceLanguage}] trecho {_segment}: {words}",
                $"[{options.TargetLanguage}] trecho {_segment}: {words}",
                final);
        }
    }
}
#endif
