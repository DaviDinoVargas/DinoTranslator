using System.Diagnostics;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Extras;
using DinoTranslator.Desktop.Services.Translation.Local;
using DinoTranslator.Shared;
using Xunit.Abstractions;

namespace DinoTranslator.Desktop.Tests;

/// <summary>Só roda com os motores extras: DINOTRANSLATOR_EXTRAS (e DINOTRANSLATOR_PYTHON, em desenvolvimento).</summary>
public sealed class ExtrasFactAttribute : FactAttribute
{
    public static ExtrasFolder? Extras { get; } = Environment.GetEnvironmentVariable("DINOTRANSLATOR_EXTRAS") is { Length: > 0 } root
        ? new ExtrasFolder(root, Environment.GetEnvironmentVariable("DINOTRANSLATOR_PYTHON") is { Length: > 0 } python ? python : null)
        : null;

    public ExtrasFactAttribute()
    {
        if (Extras is not { HasPython: true } || !LocalModelsFactAttribute.Folder.IsInstalled(WhisperModel.Turbo))
        {
            Skip = "Motores extras ou modelos locais não instalados.";
        }
    }
}

/// <summary>
/// Comparativo dos motores com a mesma fala sintética: cada combinação reconhece e traduz duas
/// frases em tempo real e o tempo até a legenda final é medido. Serve para escolher o padrão.
/// </summary>
[Collection("Modelos locais")]
public sealed class EngineComparisonTests(ITestOutputHelper output)
{
    private static readonly string[] Speech =
    [
        "Amanhã de manhã eu vou chegar às sete e meia, então me liga antes de sair.",
        "Tô morrendo de saudade, faz tempo que a gente não se fala.",
    ];

    private static readonly (string Text, string From, string To)[] Texts =
    [
        ("Amanhã de manhã eu vou chegar às sete e meia, então me liga antes de sair.", Languages.PortugueseBrazil, Languages.Russian),
        ("Tô morrendo de saudade, faz tempo que a gente não se fala.", Languages.PortugueseBrazil, Languages.Russian),
        ("A minha filha está estudando russo há três meses e já consegue conversar um pouco.", Languages.PortugueseBrazil, Languages.Russian),
        ("Как у тебя дела с переездом? Уже нашла квартиру?", Languages.Russian, Languages.PortugueseBrazil),
        ("Я вчера весь день работал, поэтому не смог тебе позвонить.", Languages.Russian, Languages.PortugueseBrazil),
        ("Gracias por la llamada, hablamos mañana.", Languages.Spanish, Languages.Ukrainian),
    ];

    [ExtrasFact]
    public async Task Tradutores_lado_a_lado()
    {
        using var factory = Factory();
        var settings = Settings();
        var options = factory.Availability(settings);
        foreach (var translator in Enum.GetValues<TranslatorOption>().Where(options.Translators.Contains))
        {
            var choice = EngineChoice.For(settings) with { Translator = translator };
            await factory.RetainAsync(choice);
            var engine = factory.Create(settings, choice)!;
            await engine.TranslateTextAsync("Aquecimento.", Languages.PortugueseBrazil, Languages.Russian, CancellationToken.None);
            output.WriteLine($"== {EngineChoice.Label(translator)}");
            long total = 0;
            foreach (var (text, from, to) in Texts)
            {
                var watch = Stopwatch.StartNew();
                var translated = await engine.TranslateTextAsync(text, from, to, CancellationToken.None);
                total += watch.ElapsedMilliseconds;
                output.WriteLine($"   {watch.ElapsedMilliseconds,5} ms  {text}  ⇒  {translated}");
                Assert.False(string.IsNullOrWhiteSpace(translated));
            }
            output.WriteLine($"   total {total} ms");
        }
    }

    [ExtrasFact]
    public async Task Combinacoes_em_tempo_real()
    {
        using var factory = Factory();
        var settings = Settings();
        var options = factory.Availability(settings);
        var standard = EngineChoice.For(settings);
        var combinations = new List<EngineChoice>
        {
            standard,
            standard with { Detector = DetectorOption.Silero, PauseMs = 500 },
            standard with { Translator = TranslatorOption.Nllb13Gpu },
            standard with { Translator = TranslatorOption.OpusGpu },
            standard with { Detector = DetectorOption.Silero, PauseMs = 500, Translator = TranslatorOption.Nllb13Gpu },
            standard with { Recognizer = RecognizerOption.FasterWhisperGpu },
            standard with { Recognizer = RecognizerOption.WhisperLarge },
        };
        var audio = Silence(500).Concat(Speak(Speech[0])).Concat(Silence(1_300)).Concat(Speak(Speech[1])).Concat(Silence(2_500)).ToArray();

        foreach (var choice in combinations.Where(c =>
            options.Detectors.Contains(c.Detector) && options.Recognizers.Contains(c.Recognizer) && options.Translators.Contains(c.Translator)))
        {
            await factory.RetainAsync(choice);
            var engine = factory.Create(settings, choice)!;
            var load = Stopwatch.StartNew();
            await using var stream = await engine.StartAsync(new SpeechStreamOptions(Languages.PortugueseBrazil, Languages.Russian, []), CancellationToken.None);
            // Aquecimento fora da medida: o primeiro uso da placa de vídeo é lento.
            await engine.TranslateTextAsync("Aquecimento.", Languages.PortugueseBrazil, Languages.Russian, CancellationToken.None);
            var loadMs = load.ElapsedMilliseconds;

            var finals = new List<SpeechUpdate>();
            stream.Updated += update =>
            {
                if (update.Final)
                {
                    lock (finals)
                    {
                        finals.Add(update);
                    }
                }
            };
            foreach (var block in Blocks(audio))
            {
                stream.Write(block);
                await Task.Delay(100);
            }
            for (var i = 0; i < 100 && Count(finals) < 2; i++)
            {
                await Task.Delay(100);
            }

            output.WriteLine($"== {Describe(choice)} (carregou em {loadMs} ms)");
            lock (finals)
            {
                foreach (var final in finals)
                {
                    output.WriteLine($"   reconhecimento {final.Timing?.RecognitionMs,5} ms · tradução {final.Timing?.TranslationMs,5} ms · legenda {final.Timing?.AfterSpeechMs,5} ms após a fala");
                    output.WriteLine($"   {final.SourceText}  ⇒  {final.TranslatedText}");
                }
                if (finals.Count > 0)
                {
                    output.WriteLine($"   média após a fala: {finals.Average(f => f.Timing?.AfterSpeechMs ?? 0):0} ms");
                }
            }
            Assert.NotEmpty(finals);
        }
    }

    private static string Describe(EngineChoice choice) =>
        $"{EngineChoice.Label(choice.Detector)}, {choice.PauseMs} ms · {EngineChoice.Label(choice.Recognizer)} · {EngineChoice.Label(choice.Translator)}";

    private static int Count(List<SpeechUpdate> finals)
    {
        lock (finals)
        {
            return finals.Count;
        }
    }

    private static SpeechEngineFactory Factory() => new(LocalModelsFactAttribute.Folder, ExtrasFactAttribute.Extras);

    private static TranslationSettings Settings() =>
        new(TranslationProvider.Local, null, null, WhisperModel.Turbo, LocalModelsFactAttribute.Folder.Path);

    private static byte[] Silence(int milliseconds) => new byte[milliseconds * 32];

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
}
