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

public static class SpeechTranslationEngines
{
    public static ISpeechTranslationEngine? Create(TranslationSettings settings, TimeProvider time) => settings.Provider switch
    {
        TranslationProvider.AzureSpeech => new AzureSpeechTranslationEngine(settings.Region!, settings.Key!),
#if DEBUG
        TranslationProvider.Simulated => new SimulatedSpeechTranslationEngine(),
#endif
        _ => null,
    };
}
