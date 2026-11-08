using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DinoTranslator.Desktop.Services.Translation.Local;

/// <summary>Diz se um bloco de áudio (16 kHz) tem voz. O segmentador usa o volume quando não há detector.</summary>
public interface IVoiceDetector
{
    bool IsVoice(ReadOnlySpan<float> block);
}

/// <summary>
/// Modelo do Silero VAD (MIT), carregado uma vez e usado por todos os detectores: uma rede neural
/// pequena que separa fala de ruído (teclado, música, ventilador) muito melhor que o volume.
/// </summary>
public sealed class SileroModel : IDisposable
{
    private readonly InferenceSession _session;

    public SileroModel(string path)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _session = new InferenceSession(path, options);
    }

    /// <summary>Probabilidade de fala de um quadro (contexto + 512 amostras); atualiza o estado recorrente.</summary>
    internal float Run(float[] input, float[] state)
    {
        using var results = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, [1, input.Length])),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(state, [2, 1, 128])),
            NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new[] { 16_000L }, [])),
        ]);
        var probability = results.First(r => r.Name == "output").AsTensor<float>().GetValue(0);
        var next = results.First(r => r.Name == "stateN").AsTensor<float>();
        for (var i = 0; i < state.Length; i++)
        {
            state[i] = next.GetValue(i);
        }
        return probability;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Detector de voz de uma pessoa com o Silero: quadros de 32 ms (512 amostras, com as 64 anteriores
/// de contexto, como o modelo espera) e histerese — começa a ouvir acima de 0,5 e só considera
/// silêncio abaixo de 0,35, para não picotar a fala.
/// </summary>
public sealed class SileroVoiceDetector(SileroModel model) : IVoiceDetector
{
    public const int FrameSamples = 512;
    private const int ContextSamples = 64;
    private const float StartThreshold = 0.5f;
    private const float EndThreshold = 0.35f;

    private readonly float[] _state = new float[2 * 128];
    private readonly float[] _input = new float[ContextSamples + FrameSamples];
    private readonly float[] _pending = new float[FrameSamples];
    private int _pendingCount;
    private bool _speaking;

    public bool IsVoice(ReadOnlySpan<float> block)
    {
        var voice = false;
        while (block.Length > 0)
        {
            var take = Math.Min(FrameSamples - _pendingCount, block.Length);
            block[..take].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += take;
            block = block[take..];
            if (_pendingCount < FrameSamples)
            {
                break;
            }
            _pendingCount = 0;
            // Entrada = 64 amostras do quadro anterior + o quadro novo.
            _input.AsSpan(FrameSamples, ContextSamples).CopyTo(_input.AsSpan(0, ContextSamples));
            _pending.CopyTo(_input.AsSpan(ContextSamples));
            var probability = model.Run(_input, _state);
            _speaking = _speaking ? probability >= EndThreshold : probability >= StartThreshold;
            voice |= _speaking;
        }
        return voice || _speaking;
    }
}
