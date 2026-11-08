using System.Buffers.Binary;

namespace DinoTranslator.Desktop.Services.Translation.Local;

/// <summary>Trecho de fala pronto para reconhecer: provisório (ainda falando) ou final.</summary>
public sealed record SpeechSegment(int Index, float[] Samples, bool Final);

/// <summary>
/// Separa a fala em trechos pelo volume, para o reconhecimento local (que não é streaming):
/// o trecho abre com voz, gera uma versão provisória a cada segundo e fecha após uma pausa.
/// </summary>
public sealed class SpeechSegmenter
{
    public const int SampleRate = 16_000;

    /// <summary>
    /// Pausa que fecha a frase. Pausas menores (respirar, pensar no meio da frase) não cortam:
    /// frase picotada é traduzida pedaço por pedaço, sem o sentido do todo.
    /// </summary>
    public const int PauseToCloseMs = 800;
    public const int MaxSegmentMs = 12_000;
    public const int PartialEveryMs = 1_000;
    public const int MinVoiceMs = 300;
    public const int PreRollMs = 300;

    private const double MinThreshold = 0.01;
    private const double NoiseMultiplier = 3.0;

    private readonly List<float> _segment = [];
    private readonly Queue<float[]> _preRoll = new();
    private int _preRollSamples;
    private double _noiseFloor = MinThreshold / NoiseMultiplier;
    private bool _speaking;
    private double _voiceMs;
    private double _silenceMs;
    private double _sincePartialMs;

    public int CurrentIndex { get; private set; }

    /// <summary>Recebe um bloco PCM 16 kHz mono 16 bits; devolve um trecho quando há algo a reconhecer.</summary>
    public SpeechSegment? Append(ReadOnlySpan<byte> pcm)
    {
        var block = ToFloat(pcm);
        if (block.Length == 0)
        {
            return null;
        }

        var durationMs = block.Length * 1000.0 / SampleRate;
        var level = Rms(block);
        var voice = level > Math.Max(MinThreshold, _noiseFloor * NoiseMultiplier);

        if (!_speaking)
        {
            if (!voice)
            {
                // Ruído de fundo: o limiar acompanha o ambiente devagar.
                _noiseFloor = (_noiseFloor * 0.95) + (level * 0.05);
                KeepPreRoll(block);
                return null;
            }
            _speaking = true;
            foreach (var earlier in _preRoll)
            {
                _segment.AddRange(earlier);
            }
            _preRoll.Clear();
            _preRollSamples = 0;
            _voiceMs = 0;
            _silenceMs = 0;
            _sincePartialMs = 0;
        }

        _segment.AddRange(block);
        _sincePartialMs += durationMs;
        if (voice)
        {
            _voiceMs += durationMs;
            _silenceMs = 0;
        }
        else
        {
            _silenceMs += durationMs;
        }

        var lengthMs = _segment.Count * 1000.0 / SampleRate;
        if (_silenceMs >= PauseToCloseMs || lengthMs >= MaxSegmentMs)
        {
            return Close();
        }
        // Provisória só com fala nova: na pausa antes de fechar não há o que atualizar.
        if (voice && _sincePartialMs >= PartialEveryMs && _voiceMs >= MinVoiceMs)
        {
            _sincePartialMs = 0;
            return new SpeechSegment(CurrentIndex, [.. _segment], Final: false);
        }
        return null;
    }

    private SpeechSegment? Close()
    {
        var samples = _segment.ToArray();
        var hadSpeech = _voiceMs >= MinVoiceMs;
        _segment.Clear();
        _speaking = false;
        if (!hadSpeech)
        {
            // Estalo ou ruído curto: não vira legenda.
            return null;
        }
        return new SpeechSegment(CurrentIndex++, samples, Final: true);
    }

    private void KeepPreRoll(float[] block)
    {
        _preRoll.Enqueue(block);
        _preRollSamples += block.Length;
        while (_preRollSamples - _preRoll.Peek().Length >= PreRollMs * SampleRate / 1000)
        {
            _preRollSamples -= _preRoll.Dequeue().Length;
        }
    }

    private static float[] ToFloat(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]) / 32768f;
        }
        return samples;
    }

    private static double Rms(float[] samples)
    {
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }
        return Math.Sqrt(sum / samples.Length);
    }
}
