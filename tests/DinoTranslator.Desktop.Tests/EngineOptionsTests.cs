using System.Buffers.Binary;
using System.Collections.Concurrent;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Extras;
using DinoTranslator.Desktop.Services.Translation.Local;
using DinoTranslator.Shared;

namespace DinoTranslator.Desktop.Tests;

public class EngineOptionsTests
{
    public static TheoryData<string, string> Pairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var source in Languages.All)
        {
            foreach (var target in Languages.All.Where(t => t.Code != source.Code))
            {
                data.Add(source.Code, target.Code);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Todo_par_de_idiomas_tem_um_modelo_opus_mt(string source, string target)
    {
        var model = OpusModels.For(source, target);

        Assert.NotNull(model);
        Assert.Contains(model, OpusModels.All);
    }

    [Theory]
    [InlineData(Languages.PortugueseBrazil, Languages.Russian, "opus-pt-zle")]
    [InlineData(Languages.Ukrainian, Languages.PortugueseBrazil, "opus-zle-pt")]
    [InlineData(Languages.Russian, Languages.Ukrainian, "opus-zle-zle")]
    [InlineData(Languages.Spanish, Languages.PortugueseBrazil, "opus-itc-itc")]
    [InlineData(Languages.PortugueseBrazil, Languages.English, "opus-romance-en")]
    public void Modelo_opus_mt_certo_para_o_par(string source, string target, string expected)
    {
        Assert.Equal(expected, OpusModels.For(source, target));
    }

    [Fact]
    public void Padrao_reproduz_o_motor_original()
    {
        var settings = new TranslationSettings(TranslationProvider.Local, null, null, WhisperModel.Small);

        var choice = EngineChoice.For(settings);

        Assert.Equal(new EngineChoice(DetectorOption.Volume, SpeechSegmenter.PauseToCloseMs, RecognizerOption.WhisperSmall, TranslatorOption.Nllb600, Careful: true), choice);
        Assert.True(choice.UsesWhisperCpp);
        Assert.Equal(WhisperModel.Small, choice.WhisperCppModel);
    }

    [Fact]
    public void Escolha_guardada_com_valores_estranhos_volta_ao_padrao()
    {
        var settings = new TranslationSettings(TranslationProvider.Local, null, null, WhisperModel.Turbo);
        var stored = new EngineChoice((DetectorOption)42, 333, (RecognizerOption)99, TranslatorOption.OpusGpu, Careful: false);

        var sane = stored.Sanitized(settings);

        Assert.Equal(new EngineChoice(DetectorOption.Volume, SpeechSegmenter.PauseToCloseMs, RecognizerOption.WhisperTurbo, TranslatorOption.OpusGpu, Careful: false), sane);
    }

    [Fact]
    public void Medias_ficam_nas_ultimas_frases()
    {
        var stats = new EngineStats();
        for (var i = 1; i <= EngineStats.Window + 3; i++)
        {
            stats.Add(new SpeechTiming(i * 10, i * 20, i * 100));
        }

        Assert.Equal(EngineStats.Window, stats.Count);
        // Ficaram as frases 4 a 15: média 9,5.
        Assert.Equal(new SpeechTiming(95, 190, 950), stats.Average);
        stats.Clear();
        Assert.Null(stats.Average);
    }

    [Fact]
    public void Segmentador_usa_o_detector_e_a_pausa_escolhida()
    {
        var detector = new ScriptedDetector();
        var segmenter = new SpeechSegmenter(detector, pauseMs: 500);
        var segments = new List<SpeechSegment>();

        // Volume alto sem voz (o detector diz que é ruído): nada abre.
        detector.Voice = false;
        Feed(segmenter, segments, loud: true, blocks: 10);
        Assert.Empty(segments);

        // Voz baixinha, mas o detector reconhece: abre, e fecha com 0,5 s de pausa.
        detector.Voice = true;
        Feed(segmenter, segments, loud: false, blocks: 10);
        detector.Voice = false;
        Feed(segmenter, segments, loud: false, blocks: 5);

        Assert.Single(segments, s => s.Final);
    }

    [Fact]
    public async Task Final_traz_os_tempos_da_frase()
    {
        var updates = new ConcurrentQueue<SpeechUpdate>();
        await using var pipeline = new SpeechPipeline(
            async (segment, token) =>
            {
                await Task.Delay(30, token);
                return "olá";
            },
            async (text, careful, token) =>
            {
                await Task.Delay(20, token);
                return "привет";
            },
            pauseMs: 500);
        pipeline.Updated += updates.Enqueue;

        pipeline.Enqueue(new SpeechSegment(0, [0f], Final: true));

        for (var i = 0; i < 200 && !updates.Any(u => u.Final); i++)
        {
            await Task.Delay(10);
        }
        var timing = Assert.Single(updates, u => u.Final).Timing;
        Assert.NotNull(timing);
        Assert.InRange(timing.RecognitionMs, 25, 2_000);
        Assert.InRange(timing.TranslationMs, 15, 2_000);
        Assert.True(timing.AfterSpeechMs >= 500 + 45, $"{timing.AfterSpeechMs} ms");
        Assert.All(updates.Where(u => !u.Final), u => Assert.Null(u.Timing));
    }

    private static void Feed(SpeechSegmenter segmenter, List<SpeechSegment> segments, bool loud, int blocks)
    {
        for (var i = 0; i < blocks; i++)
        {
            var pcm = new byte[SpeechSegmenter.SampleRate / 10 * 2];
            for (var s = 0; s < pcm.Length / 2; s++)
            {
                var value = (short)(Math.Sin(2 * Math.PI * 220 * s / SpeechSegmenter.SampleRate) * (loud ? 12_000 : 40));
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(s * 2), value);
            }
            if (segmenter.Append(pcm) is { } segment)
            {
                segments.Add(segment);
            }
        }
    }

    private sealed class ScriptedDetector : IVoiceDetector
    {
        public bool Voice { get; set; }

        public bool IsVoice(ReadOnlySpan<float> block) => Voice;
    }
}
