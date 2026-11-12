using DinoTranslator.Desktop.Services.Translation.Extras;
using DinoTranslator.Desktop.Services.Translation.Local;

namespace DinoTranslator.Desktop.Services.Translation;

/// <param name="Phrases">Glossário: nomes e termos que o reconhecimento deve preferir.</param>
/// <param name="Glossary">Glossário aplicado na tradução (termos mantidos ou com tradução fixa).</param>
public sealed record SpeechStreamOptions(string SourceLanguage, string TargetLanguage, IReadOnlyList<string> Phrases, Glossary? Glossary = null);

/// <summary>Atualização de um trecho: provisória (vai se ajustando) ou final (com os tempos medidos).</summary>
public sealed record SpeechUpdate(int Segment, int Revision, string SourceText, string? TranslatedText, bool Final, SpeechTiming? Timing = null);

/// <summary>
/// Tempos de uma frase no motor local: o reconhecimento e a tradução da versão final, e da hora em
/// que a pessoa parou de falar até a legenda final.
/// </summary>
public sealed record SpeechTiming(int RecognitionMs, int TranslationMs, int AfterSpeechMs);

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

    /// <summary>Traduz também o que é escrito no chat (o Azure Speech só traduz fala).</summary>
    bool CanTranslateText => false;

    /// <summary>Tradução de um texto escrito; <c>null</c> quando o motor não traduz texto.</summary>
    Task<string?> TranslateTextAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// Cria o motor configurado e guarda as peças do motor local carregadas entre uma chamada e outra
/// (carregá-las leva alguns segundos). Durante a chamada, a combinação pode ser trocada: as peças
/// que saem da combinação são liberadas, para não ocupar a memória da placa de vídeo.
/// </summary>
public sealed class SpeechEngineFactory(ModelFolder defaultModels, ExtrasFolder? extras = null) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _pythonGate = new(1, 1);
    private WhisperCppEngine? _whisper;
    private string? _whisperFolder;
    private NllbTranslator? _nllb;
    private string? _nllbFolder;
    private SileroModel? _silero;
    private PythonEngine? _python;

    /// <summary>%LOCALAPPDATA%\DinoTranslator\modelos.</summary>
    public ModelFolder DefaultModels { get; } = defaultModels;

    public ExtrasFolder Extras { get; } = extras ?? ExtrasFolder.Default();

    public ModelFolder ModelsFor(TranslationSettings settings) =>
        settings.ModelsFolder is { } path ? new ModelFolder(path) : DefaultModels;

    /// <summary>A pasta que tem o arquivo: a configurada (ou a padrão) e, depois, a dos extras do instalador.</summary>
    public ModelFolder FolderFor(TranslationSettings settings, ModelFile file)
    {
        var configured = ModelsFor(settings);
        if (configured.IsInstalled(file))
        {
            return configured;
        }
        var bundled = new ModelFolder(Extras.Models);
        return bundled.IsInstalled(file) ? bundled : configured;
    }

    /// <summary>O motor configurado pode ser usado agora (no local: o Whisper escolhido e o NLLB baixados).</summary>
    public bool IsReady(TranslationSettings settings) => settings.Provider switch
    {
        TranslationProvider.Local => settings.LocalModel is { } model
            && LocalModels.Required(model).All(file => FolderFor(settings, file).IsInstalled(file)),
        TranslationProvider.None => false,
        _ => true,
    };

    /// <summary>O que dá para escolher na chamada, com os modelos e componentes instalados.</summary>
    public EngineAvailability Availability(TranslationSettings settings) =>
        EngineAvailability.Check(new MergedFolder(this, settings), Extras);

    /// <summary>
    /// Motor para a versão Plus (serviço pago): só peças com licença comercial. Local, o MarianMT com o
    /// Whisper; na nuvem, o provedor configurado (com a chave da própria pessoa). null: sem tradução
    /// permitida neste computador (falta instalar o MarianMT nos motores extras).
    /// </summary>
    public ISpeechTranslationEngine? CreateForPlus(TranslationSettings settings, EngineChoice? preferred = null)
    {
        if (settings.Provider != TranslationProvider.Local)
        {
            return Create(settings);
        }
        return IsReady(settings) && EngineChoice.ForPlus(settings, Availability(settings), preferred) is { } choice
            ? Create(settings, choice)
            : null;
    }

    /// <summary>Por que a versão Plus está sem tradução neste computador, se estiver.</summary>
    public string? PlusTranslationProblem(TranslationSettings settings) =>
        CreateForPlus(settings) is not null ? null
        : !IsReady(settings) ? "A tradução não está configurada neste computador."
        : "Na versão Plus a tradução local usa o MarianMT (licença comercial). Instale os motores extras em Ajustes › Tradução.";

    public ISpeechTranslationEngine? Create(TranslationSettings settings, EngineChoice? choice = null)
    {
        if (!IsReady(settings))
        {
            return null;
        }
        return settings.Provider switch
        {
            TranslationProvider.AzureSpeech => new AzureSpeechTranslationEngine(settings.Region!, settings.Key!),
            TranslationProvider.Local => new ComposedSpeechTranslationEngine(this, settings, choice ?? EngineChoice.For(settings)),
#if DEBUG
            TranslationProvider.Simulated => new SimulatedSpeechTranslationEngine(),
#endif
            _ => null,
        };
    }

    internal async Task<IRecognitionPart> RecognitionAsync(
        TranslationSettings settings, EngineChoice choice, string language, IReadOnlyList<string> glossary, CancellationToken cancellationToken)
    {
        if (choice.UsesWhisperCpp)
        {
            var engine = await Task.Run(() => WhisperCpp(settings, choice.WhisperCppModel), cancellationToken);
            return new WhisperCppPart(engine, engine.CreateRecognizer(language, glossary), choice.Careful);
        }
        var python = await PythonAsync(cancellationToken);
        var gpu = choice.Recognizer == RecognizerOption.FasterWhisperGpu;
        var key = RecognizerKey(choice.Recognizer);
        await python.LoadAsync(key, "whisper", Extras.FasterWhisperTurbo, gpu, cancellationToken);
        return new FasterWhisperPart(python, key, language, glossary.Count > 0 ? string.Join(", ", glossary) : null, choice.Careful);
    }

    internal async Task<ITranslationPart> TranslationAsync(TranslationSettings settings, EngineChoice choice, CancellationToken cancellationToken)
    {
        if (choice.Translator == TranslatorOption.Nllb600)
        {
            return new NllbOnnxPart(await Task.Run(() => NllbOnnx(settings), cancellationToken));
        }
        var python = await PythonAsync(cancellationToken);
        var gpu = choice.Translator is TranslatorOption.Nllb600Gpu or TranslatorOption.Nllb13Gpu or TranslatorOption.OpusGpu;
        if (choice.Translator is TranslatorOption.OpusGpu or TranslatorOption.OpusCpu)
        {
            // OPUS-MT: um modelo por grupo de idiomas, carregado quando o par aparece; sem modelo, o NLLB
            // original — menos no uso pago, em que o NLLB (não comercial) nunca entra.
            var fallback = !choice.Commercial && LocalModels.Translator.All(file => FolderFor(settings, file).IsInstalled(file))
                ? new NllbOnnxPart(await Task.Run(() => NllbOnnx(settings), cancellationToken))
                : null;
            return new Ct2Part(python, async (source, target, token) =>
            {
                if (OpusModels.For(source, target) is not { } name || !Extras.HasModel(Extras.Ct2(name)))
                {
                    return null;
                }
                var key = $"{name}-{(gpu ? "gpu" : "cpu")}";
                await python.LoadAsync(key, "marian", Extras.Ct2(name), gpu, token);
                return key;
            }, fallback);
        }
        var model = choice.Translator == TranslatorOption.Nllb600Gpu ? "nllb-600m" : "nllb-1.3b";
        var nllbKey = $"{model}-{(gpu ? "gpu" : "cpu")}";
        await python.LoadAsync(nllbKey, "nllb", Extras.Ct2(model), gpu, cancellationToken);
        return new Ct2Part(python, (_, _, _) => Task.FromResult<string?>(nllbKey), fallback: null);
    }

    internal async Task<SileroModel> SileroAsync(CancellationToken cancellationToken) =>
        await Task.Run(() =>
        {
            lock (_gate)
            {
                return _silero ??= new SileroModel(Extras.Silero);
            }
        }, cancellationToken);

    /// <summary>Libera as peças que não fazem parte da combinação (a placa de vídeo tem só 4 GB em muitos computadores).</summary>
    public async Task RetainAsync(EngineChoice choice)
    {
        lock (_gate)
        {
            if (!choice.UsesWhisperCpp)
            {
                _whisper?.Dispose();
                _whisper = null;
            }
            if (choice.Translator is not (TranslatorOption.Nllb600 or TranslatorOption.OpusCpu or TranslatorOption.OpusGpu))
            {
                _nllb?.Dispose();
                _nllb = null;
            }
        }
        if (_python is { Running: true } python)
        {
            var keep = new HashSet<string>();
            if (!choice.UsesWhisperCpp)
            {
                keep.Add(RecognizerKey(choice.Recognizer));
            }
            var gpu = choice.Translator is TranslatorOption.Nllb600Gpu or TranslatorOption.Nllb13Gpu or TranslatorOption.OpusGpu;
            var suffix = gpu ? "gpu" : "cpu";
            switch (choice.Translator)
            {
                case TranslatorOption.Nllb600Gpu:
                    keep.Add($"nllb-600m-{suffix}");
                    break;
                case TranslatorOption.Nllb13Gpu or TranslatorOption.Nllb13Cpu:
                    keep.Add($"nllb-1.3b-{suffix}");
                    break;
                case TranslatorOption.OpusGpu or TranslatorOption.OpusCpu:
                    keep.UnionWith(OpusModels.All.Select(name => $"{name}-{suffix}"));
                    break;
            }
            try
            {
                await python.KeepOnlyAsync(keep);
            }
            catch (PythonEngineException)
            {
                // Motor auxiliar parado: nada a liberar.
            }
        }
    }

    /// <summary>Fecha o motor auxiliar e o Silero (antes de substituir os arquivos dos extras).</summary>
    public async Task StopExtrasAsync()
    {
        if (_python is { } python)
        {
            _python = null;
            await python.DisposeAsync();
        }
        lock (_gate)
        {
            _silero?.Dispose();
            _silero = null;
        }
    }

    /// <summary>Libera os modelos locais (ex.: antes de apagá-los do disco).</summary>
    public void ReleaseLocal()
    {
        lock (_gate)
        {
            _whisper?.Dispose();
            _whisper = null;
            _whisperFolder = null;
            _nllb?.Dispose();
            _nllb = null;
            _nllbFolder = null;
        }
    }

    public void Dispose()
    {
        ReleaseLocal();
        lock (_gate)
        {
            _silero?.Dispose();
            _silero = null;
        }
        if (_python is { } python)
        {
            _python = null;
            python.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
    }

    private static string RecognizerKey(RecognizerOption option) =>
        option == RecognizerOption.FasterWhisperGpu ? "fw-turbo-gpu" : "fw-turbo-cpu";

    private WhisperCppEngine WhisperCpp(TranslationSettings settings, WhisperModel model)
    {
        lock (_gate)
        {
            var folder = FolderFor(settings, LocalModels.Whisper(model));
            if (_whisper is null || _whisper.Model != model || _whisperFolder != folder.Path)
            {
                _whisper?.Dispose();
                _whisper = null;
                _whisper = WhisperCppEngine.Load(folder, model);
                _whisperFolder = folder.Path;
            }
            return _whisper;
        }
    }

    private NllbTranslator NllbOnnx(TranslationSettings settings)
    {
        lock (_gate)
        {
            var folder = FolderFor(settings, LocalModels.TranslatorDecoder);
            if (_nllb is null || _nllbFolder != folder.Path)
            {
                _nllb?.Dispose();
                _nllb = null;
                _nllb = LocalSpeechRuntime.LoadNllb(folder);
                _nllbFolder = folder.Path;
            }
            return _nllb;
        }
    }

    private async Task<PythonEngine> PythonAsync(CancellationToken cancellationToken)
    {
        await _pythonGate.WaitAsync(cancellationToken);
        try
        {
            if (_python is { Running: true } running)
            {
                return running;
            }
            if (_python is { } stopped)
            {
                await stopped.DisposeAsync();
            }
            if (!Extras.HasPython)
            {
                throw new PythonEngineException("Os motores extras não estão instalados neste computador.");
            }
            _python = await PythonEngine.StartAsync(Extras, cancellationToken);
            return _python;
        }
        finally
        {
            _pythonGate.Release();
        }
    }

    /// <summary>Visão das duas pastas de modelos (a configurada e a dos extras) para conferir o que está instalado.</summary>
    private sealed class MergedFolder(SpeechEngineFactory factory, TranslationSettings settings) : IModelFiles
    {
        public bool IsInstalled(ModelFile file) => factory.FolderFor(settings, file).IsInstalled(file);
    }
}
