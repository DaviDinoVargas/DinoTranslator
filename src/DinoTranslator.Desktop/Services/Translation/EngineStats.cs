namespace DinoTranslator.Desktop.Services.Translation;

/// <summary>
/// Médias das últimas frases com a combinação atual, para comparar as opções durante a chamada:
/// tempo do reconhecimento final, da tradução final e da hora em que a pessoa parou de falar até
/// a legenda final aparecer.
/// </summary>
public sealed class EngineStats
{
    public const int Window = 12;

    private readonly Queue<SpeechTiming> _timings = new();
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _timings.Count;
            }
        }
    }

    public SpeechTiming? Average
    {
        get
        {
            lock (_gate)
            {
                if (_timings.Count == 0)
                {
                    return null;
                }
                return new SpeechTiming(
                    (int)_timings.Average(t => t.RecognitionMs),
                    (int)_timings.Average(t => t.TranslationMs),
                    (int)_timings.Average(t => t.AfterSpeechMs));
            }
        }
    }

    public void Add(SpeechTiming timing)
    {
        lock (_gate)
        {
            _timings.Enqueue(timing);
            while (_timings.Count > Window)
            {
                _timings.Dequeue();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _timings.Clear();
        }
    }
}
