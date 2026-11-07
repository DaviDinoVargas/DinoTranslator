using System.Diagnostics;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Local;
using DinoTranslator.Shared;
using Xunit.Abstractions;

namespace DinoTranslator.Desktop.Tests;

/// <summary>
/// Só roda com os modelos baixados: na pasta de DINOTRANSLATOR_MODELS ou na padrão,
/// %LOCALAPPDATA%\DinoTranslator\modelos.
/// </summary>
public sealed class LocalModelsFactAttribute : FactAttribute
{
    public static ModelFolder Folder { get; } = new(Environment.GetEnvironmentVariable("DINOTRANSLATOR_MODELS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DinoTranslator", "modelos"));

    public LocalModelsFactAttribute(WhisperModel model = WhisperModel.Turbo)
    {
        if (!Folder.IsInstalled(model))
        {
            Skip = "Modelos locais não baixados (Início › Tradução › Neste computador).";
        }
    }
}

[Collection("Modelos locais")]
public sealed class LocalModelsIntegrationTests(ITestOutputHelper output)
{
    private const string Sentence = "Olá Anna, hoje o dia está lindo aqui em São Paulo e eu fui à praia.";

    [LocalModelsFact]
    public void Traduz_entre_os_idiomas_da_chamada()
    {
        using var runtime = LocalSpeechRuntime.Load(LocalModelsFactAttribute.Folder, WhisperModel.Turbo);
        var translator = runtime.Translator;
        translator.Translate("Aquecimento.", Languages.PortugueseBrazil, Languages.English);

        var cases = new (string Text, string From, string To, Func<string, bool> Check)[]
        {
            ("Bom dia, como você está?", Languages.PortugueseBrazil, Languages.Russian, IsCyrillic),
            ("Привет! Как прошёл твой день?", Languages.Russian, Languages.PortugueseBrazil, IsLatin),
            ("The weather is beautiful today.", Languages.English, Languages.Spanish, t => t.Contains("hoy", StringComparison.OrdinalIgnoreCase)),
            ("Доброго дня! Як справи?", Languages.Ukrainian, Languages.PortugueseBrazil, IsLatin),
            ("Gracias por la llamada.", Languages.Spanish, Languages.Ukrainian, IsCyrillic),
        };

        foreach (var (text, from, to, check) in cases)
        {
            var watch = Stopwatch.StartNew();
            var translated = translator.Translate(text, from, to);
            output.WriteLine($"{from} → {to} em {watch.ElapsedMilliseconds} ms: {text} ⇒ {translated}");
            Assert.False(string.IsNullOrWhiteSpace(translated));
            Assert.True(check(translated), translated);
        }
    }

    [LocalModelsFact(WhisperModel.Turbo)]
    public Task Reconhece_fala_em_portugues_com_o_modelo_preciso() => RecognizeAsync(WhisperModel.Turbo);

    [LocalModelsFact(WhisperModel.Small)]
    public Task Reconhece_fala_em_portugues_com_o_modelo_rapido() => RecognizeAsync(WhisperModel.Small);

    [LocalModelsFact]
    public async Task Fala_vira_legenda_final_traduzida_depois_da_pausa()
    {
        var factory = new SpeechEngineFactory(LocalModelsFactAttribute.Folder);
        var engine = factory.Create(new TranslationSettings(TranslationProvider.Local, null, null, WhisperModel.Turbo, LocalModelsFactAttribute.Folder.Path))!;
        await using var stream = await engine.StartAsync(
            new SpeechStreamOptions(Languages.PortugueseBrazil, Languages.Russian, ["Anna"]), CancellationToken.None);
        var updates = new List<(SpeechUpdate Update, long At)>();
        var final = new TaskCompletionSource<SpeechUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        stream.Failed += message =>
        {
            output.WriteLine($"FALHA: {message}");
            final.TrySetException(new InvalidOperationException(message));
        };
        stream.Updated += update =>
        {
            lock (updates)
            {
                updates.Add((update, clock.ElapsedMilliseconds));
            }
            if (update.Final)
            {
                final.TrySetResult(update);
            }
        };

        // Em tempo real: blocos de 100 ms, como a captura da chamada entrega.
        var speech = Speak(Sentence);
        long speechEnded = 0;
        foreach (var block in Blocks([.. new byte[3_200 * 5], .. speech, .. new byte[3_200 * 10]]))
        {
            stream.Write(block);
            if (speechEnded == 0 && clock.ElapsedMilliseconds * 32 >= (3_200 * 5 + speech.Length) / 100 * 100)
            {
                speechEnded = clock.ElapsedMilliseconds;
            }
            await Task.Delay(100);
        }
        speechEnded = (3_200 * 5 + speech.Length) / 32;

        var result = await final.Task.WaitAsync(TimeSpan.FromSeconds(30));
        lock (updates)
        {
            foreach (var (update, at) in updates)
            {
                output.WriteLine($"{at,6} ms {(update.Final ? "FINAL" : "prov.")} #{update.Revision}: {update.SourceText} ⇒ {update.TranslatedText}");
            }
            var finalAt = updates.Single(u => u.Update.Final).At;
            output.WriteLine($"Fim da fala em ~{speechEnded} ms; legenda final {finalAt - speechEnded} ms depois.");
        }
        Assert.Contains("praia", result.SourceText, StringComparison.OrdinalIgnoreCase);
        Assert.True(IsCyrillic(result.TranslatedText!), result.TranslatedText);
        factory.Dispose();
    }

    private async Task RecognizeAsync(WhisperModel model)
    {
        using var runtime = LocalSpeechRuntime.Load(LocalModelsFactAttribute.Folder, model);
        await using var recognizer = runtime.CreateRecognizer(Languages.PortugueseBrazil, ["Anna"]);
        var samples = ToFloat(Speak(Sentence));
        await runtime.TranscribeAsync(recognizer, samples[..16_000], CancellationToken.None);

        var watch = Stopwatch.StartNew();
        var text = await runtime.TranscribeAsync(recognizer, samples, CancellationToken.None);

        output.WriteLine($"{model} em {runtime.Device}: {samples.Length / 16} ms de áudio reconhecidos em {watch.ElapsedMilliseconds} ms: {text}");
        Assert.Contains("praia", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Paulo", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Voz do próprio Windows em português, em PCM 16 kHz mono 16 bits.</summary>
    private static byte[] Speak(string text)
    {
        using var synthesizer = new SpeechSynthesizer();
        synthesizer.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0, new System.Globalization.CultureInfo("pt-BR"));
        using var audio = new MemoryStream();
        synthesizer.SetOutputToAudioStream(audio, new SpeechAudioFormatInfo(16_000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synthesizer.Speak(text);
        return audio.ToArray();
    }

    private static IEnumerable<byte[]> Blocks(byte[] pcm)
    {
        for (var offset = 0; offset < pcm.Length; offset += 3_200)
        {
            yield return pcm[offset..Math.Min(offset + 3_200, pcm.Length)];
        }
    }

    private static float[] ToFloat(byte[] pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        }
        return samples;
    }

    private static bool IsCyrillic(string text) => text.Any(c => c is >= 'А' and <= 'я');

    private static bool IsLatin(string text) => text.Any(char.IsAsciiLetter) && !IsCyrillic(text);
}

/// <summary>Os testes com modelos reais disputam a placa de vídeo: um de cada vez.</summary>
[CollectionDefinition("Modelos locais", DisableParallelization = true)]
public sealed class LocalModelsCollection;
