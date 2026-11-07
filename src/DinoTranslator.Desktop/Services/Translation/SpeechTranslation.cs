using DinoTranslator.Desktop.Services.Translation.Local;

namespace DinoTranslator.Desktop.Services.Translation;

/// <param name="Phrases">Glossário: nomes e termos que o reconhecimento deve preferir.</param>
public sealed record SpeechStreamOptions(string SourceLanguage, string TargetLanguage, IReadOnlyList<string> Phrases);

/// <summary>Atualização de um trecho: provisória (vai se ajustando) ou final.</summary>
public sealed record SpeechUpdate(int Segment, int Revision, string SourceText, string? TranslatedText, bool Final);

/// <summary>Fala de uma pessoa: entra áudio PCM 16 kHz mono 16 bits, saem legendas.</summary>
public interface ISpeechTranslationStream : IAsyncDisposable
{
    event Action<SpeechUpdate>? Updated;

    /// <summary>Mensagem pronta para a pessoa ler (sem detalhes internos).</summary>
    event Action<string>? Failed;

    void Write(byte[] pcm);
}

/// <summary>Motor de reconhecimento e tradução: um fluxo por pessoa que fala.</summary>
public interface ISpeechTranslationEngine
{
    Task<ISpeechTranslationStream> StartAsync(SpeechStreamOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// Cria o motor configurado. Os modelos locais ficam carregados entre uma chamada e outra:
/// carregá-los leva alguns segundos.
/// </summary>
public sealed class SpeechEngineFactory(ModelFolder models) : IDisposable
{
    private readonly Lock _gate = new();
    private LocalSpeechRuntime? _local;

    public ModelFolder Models { get; } = models;

    /// <summary>O motor configurado pode ser usado agora (no local: modelos baixados).</summary>
    public bool IsReady(TranslationSettings settings) => settings.Provider switch
    {
        TranslationProvider.Local => settings.LocalModel is { } model && Models.IsInstalled(model),
        TranslationProvider.None => false,
        _ => true,
    };

    public ISpeechTranslationEngine? Create(TranslationSettings settings)
    {
        if (!IsReady(settings))
        {
            return null;
        }
        return settings.Provider switch
        {
            TranslationProvider.AzureSpeech => new AzureSpeechTranslationEngine(settings.Region!, settings.Key!),
            TranslationProvider.Local => new LocalSpeechTranslationEngine(() => LocalRuntime(settings.LocalModel!.Value)),
#if DEBUG
            TranslationProvider.Simulated => new SimulatedSpeechTranslationEngine(),
#endif
            _ => null,
        };
    }

    /// <summary>Libera os modelos locais (ex.: antes de apagá-los do disco).</summary>
    public void ReleaseLocal()
    {
        lock (_gate)
        {
            _local?.Dispose();
            _local = null;
        }
    }

    public void Dispose() => ReleaseLocal();

    private LocalSpeechRuntime LocalRuntime(WhisperModel model)
    {
        lock (_gate)
        {
            if (_local?.Model != model)
            {
                _local?.Dispose();
                _local = LocalSpeechRuntime.Load(Models, model);
            }
            return _local;
        }
    }
}
