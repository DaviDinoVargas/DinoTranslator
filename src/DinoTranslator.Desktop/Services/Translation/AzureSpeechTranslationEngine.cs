using DinoTranslator.Shared;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;

namespace DinoTranslator.Desktop.Services.Translation;

/// <summary>
/// Azure Speech: reconhece e traduz na mesma conexão de streaming, com resultados
/// provisórios enquanto a pessoa fala e o final depois da pausa.
/// </summary>
public sealed class AzureSpeechTranslationEngine(string region, string key) : ISpeechTranslationEngine
{
    /// <summary>Pausa que fecha um trecho: curta o bastante para a legenda final sair rápido.</summary>
    public const int SegmentationSilenceMs = 600;

    public async Task<ISpeechTranslationStream> StartAsync(SpeechStreamOptions options, CancellationToken cancellationToken)
    {
        var target = TranslationTarget(options.TargetLanguage);
        var config = SpeechTranslationConfig.FromSubscription(key, region);
        config.SpeechRecognitionLanguage = options.SourceLanguage;
        config.AddTargetLanguage(target);
        config.SetProfanity(ProfanityOption.Raw);
        // Provisórias estáveis: menos palavras trocando de lugar na tela.
        config.SetProperty(PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "2");
        config.SetProperty(PropertyId.SpeechServiceResponse_TranslationRequestStablePartialResult, "true");
        config.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, SegmentationSilenceMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var stream = new Stream(config, target, options.Phrases);
        try
        {
            await stream.StartAsync().WaitAsync(cancellationToken);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
        return stream;
    }

    /// <summary>Código de destino da tradução (o Azure traduz para "pt" = português do Brasil).</summary>
    public static string TranslationTarget(string language) => language switch
    {
        Languages.PortugueseBrazil => "pt",
        Languages.English => "en",
        Languages.Ukrainian => "uk",
        Languages.Russian => "ru",
        Languages.Spanish => "es",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Idioma não suportado."),
    };

    /// <summary>Erro do serviço em uma frase para a pessoa; a chave nunca aparece.</summary>
    public static string Describe(CancellationErrorCode code) => code switch
    {
        CancellationErrorCode.AuthenticationFailure => "O Azure Speech recusou a chave ou a região. Confira em Configurações.",
        CancellationErrorCode.Forbidden => "O recurso do Azure Speech não permite esta operação.",
        CancellationErrorCode.TooManyRequests => "Limite de uso do Azure Speech atingido. Tente de novo em instantes.",
        CancellationErrorCode.ConnectionFailure or CancellationErrorCode.ServiceUnavailable or CancellationErrorCode.ServiceTimeout =>
            "Sem conexão com o Azure Speech. A chamada continua, sem legendas.",
        _ => "A tradução parou de responder. A chamada continua, sem legendas.",
    };

    private sealed class Stream : ISpeechTranslationStream
    {
        private readonly PushAudioInputStream _audio = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16_000, 16, 1));
        private readonly TranslationRecognizer _recognizer;
        private readonly string _target;
        private readonly Lock _gate = new();
        private int _segment;
        private int _revision;

        public Stream(SpeechTranslationConfig config, string target, IReadOnlyList<string> phrases)
        {
            _target = target;
            _recognizer = new TranslationRecognizer(config, AudioConfig.FromStreamInput(_audio));
            if (phrases.Count > 0)
            {
                var glossary = PhraseListGrammar.FromRecognizer(_recognizer);
                foreach (var phrase in phrases)
                {
                    glossary.AddPhrase(phrase);
                }
            }
            _recognizer.Recognizing += (_, e) => Emit(e.Result, final: false);
            _recognizer.Recognized += (_, e) =>
            {
                if (e.Result.Reason == ResultReason.TranslatedSpeech)
                {
                    Emit(e.Result, final: true);
                }
            };
            _recognizer.Canceled += (_, e) =>
            {
                if (e.Reason == CancellationReason.Error)
                {
                    Failed?.Invoke(Describe(e.ErrorCode));
                }
            };
        }

        public event Action<SpeechUpdate>? Updated;

        public event Action<string>? Failed;

        public Task StartAsync() => _recognizer.StartContinuousRecognitionAsync();

        public void Write(byte[] pcm) => _audio.Write(pcm);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _recognizer.StopContinuousRecognitionAsync();
            }
            catch (ApplicationException)
            {
                // Já parado pelo serviço.
            }
            _audio.Close();
            _recognizer.Dispose();
            _audio.Dispose();
        }

        private void Emit(TranslationRecognitionResult result, bool final)
        {
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                return;
            }
            result.Translations.TryGetValue(_target, out var translated);

            SpeechUpdate update;
            lock (_gate)
            {
                update = new SpeechUpdate(_segment, _revision++, result.Text, translated, final);
                if (final)
                {
                    _segment++;
                    _revision = 0;
                }
            }
            Updated?.Invoke(update);
        }
    }
}
