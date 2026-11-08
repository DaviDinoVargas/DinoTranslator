using System.Buffers.Binary;
using DinoTranslator.Desktop.Services.Translation.Local;

namespace DinoTranslator.Desktop.Tests;

public class SpeechSegmenterTests
{
    /// <summary>Silêncio que fecha o trecho, arredondado para os blocos de 100 ms.</summary>
    private const int Pause = SpeechSegmenter.PauseToCloseMs + 100;

    private static List<SpeechSegment> Feed(SpeechSegmenter segmenter, params (bool Voice, int Ms)[] parts)
    {
        var segments = new List<SpeechSegment>();
        foreach (var (voice, ms) in parts)
        {
            for (var done = 0; done < ms; done += 100)
            {
                if (segmenter.Append(Block(voice, 100)) is { } segment)
                {
                    segments.Add(segment);
                }
            }
        }
        return segments;
    }

    private static byte[] Block(bool voice, int ms)
    {
        var samples = SpeechSegmenter.SampleRate * ms / 1000;
        var pcm = new byte[samples * 2];
        for (var i = 0; voice && i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 220 * i / SpeechSegmenter.SampleRate) * 6_000));
        }
        return pcm;
    }

    [Fact]
    public void Silencio_nao_gera_trecho()
    {
        Assert.Empty(Feed(new SpeechSegmenter(), (false, 5_000)));
    }

    [Fact]
    public void Fala_com_pausa_gera_provisorias_e_um_final()
    {
        var segments = Feed(new SpeechSegmenter(), (false, 500), (true, 2_500), (false, Pause));

        Assert.Equal(2, segments.Count(s => !s.Final));
        var final = Assert.Single(segments, s => s.Final);
        Assert.Equal(0, final.Index);
        Assert.All(segments, s => Assert.Equal(0, s.Index));
    }

    [Fact]
    public void Trecho_guarda_um_pouco_de_audio_antes_da_voz()
    {
        var final = Assert.Single(Feed(new SpeechSegmenter(), (false, 1_000), (true, 1_000), (false, Pause)), s => s.Final);

        var expectedMs = SpeechSegmenter.PreRollMs + 1_000 + SpeechSegmenter.PauseToCloseMs;
        Assert.InRange(final.Samples.Length * 1000 / SpeechSegmenter.SampleRate, expectedMs - 100, expectedMs + 100);
    }

    [Fact]
    public void Estalo_curto_e_descartado()
    {
        Assert.Empty(Feed(new SpeechSegmenter(), (true, 100), (false, 1_000)));
    }

    [Fact]
    public void Fala_longa_e_fechada_aos_doze_segundos()
    {
        var segmenter = new SpeechSegmenter();
        var segments = Feed(segmenter, (true, 13_000));

        var final = Assert.Single(segments, s => s.Final);
        Assert.Equal(SpeechSegmenter.MaxSegmentMs, final.Samples.Length * 1000 / SpeechSegmenter.SampleRate);
        Assert.Equal(1, segmenter.CurrentIndex);
    }

    [Fact]
    public void Trechos_seguidos_tem_indices_seguidos()
    {
        var finals = Feed(new SpeechSegmenter(), (true, 800), (false, Pause), (true, 800), (false, Pause)).Where(s => s.Final).ToList();

        Assert.Equal([0, 1], finals.Select(s => s.Index));
    }
}
