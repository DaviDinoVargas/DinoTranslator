namespace DinoTranslator.Shared.Call;

/// <summary>De quem é a fala, do ponto de vista de quem está olhando a tela.</summary>
public enum CaptionOwner
{
    Me,
    Other,
}

/// <param name="TranslatedText"><c>null</c> quando os dois falam o mesmo idioma (ou não há tradução).</param>
/// <param name="Edited">A pessoa corrigiu o texto depois que o trecho ficou final.</param>
public sealed record Caption(
    CaptionOwner Owner,
    string SegmentId,
    int Revision,
    string SourceLanguage,
    string SourceText,
    string TargetLanguage,
    string? TranslatedText,
    bool Final,
    DateTimeOffset UpdatedAt,
    bool Edited = false)
{
    /// <summary>Mensagem escrita no chat (e não falada).</summary>
    public bool Written => SegmentId.StartsWith(TextMessage.IdPrefix, StringComparison.Ordinal);
}

/// <summary>
/// Legendas da chamada: a fala em andamento e as recentes de cada pessoa, mais a lista de
/// trechos finais (o histórico do painel Falas).
/// </summary>
public sealed class CaptionBoard(TimeProvider time)
{
    public const int MaxTranscript = 2_000;

    /// <summary>Por quanto tempo uma legenda final continua na tela.</summary>
    public static readonly TimeSpan VisibleFor = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan Forget = TimeSpan.FromMinutes(1);

    private readonly Dictionary<(CaptionOwner Owner, string SegmentId), Caption> _segments = [];
    private readonly List<Caption> _transcript = [];

    public IReadOnlyList<Caption> Transcript => _transcript;

    /// <summary>
    /// Aplica uma atualização; revisões antigas e qualquer coisa depois do final são ignoradas,
    /// menos a correção de um trecho final (que substitui o texto no histórico).
    /// </summary>
    public bool Apply(Caption update)
    {
        if (update.Edited)
        {
            return ApplyCorrection(update);
        }
        var now = time.GetUtcNow();
        var key = (update.Owner, update.SegmentId);
        if (_segments.TryGetValue(key, out var current) && (current.Final || update.Revision <= current.Revision))
        {
            return false;
        }

        var stamped = update with { UpdatedAt = now };
        _segments[key] = stamped;
        if (stamped.Final)
        {
            _transcript.Add(stamped);
            if (_transcript.Count > MaxTranscript)
            {
                _transcript.RemoveAt(0);
            }
        }

        foreach (var old in _segments.Where(s => now - s.Value.UpdatedAt > Forget).Select(s => s.Key).ToList())
        {
            _segments.Remove(old);
        }
        return true;
    }

    /// <summary>A legenda mais recente de cada pessoa: em andamento, ou final há menos de 8 segundos.</summary>
    public IReadOnlyList<Caption> Visible()
    {
        var now = time.GetUtcNow();
        return _segments.Values
            .GroupBy(c => c.Owner)
            .Select(g => g.MaxBy(c => c.UpdatedAt)!)
            .Where(c => !c.Final || now - c.UpdatedAt <= VisibleFor)
            .OrderBy(c => c.UpdatedAt)
            .ToList();
    }

    /// <summary>O trecho final de uma pessoa no histórico, se ainda estiver lá.</summary>
    public Caption? Find(CaptionOwner owner, string segmentId)
    {
        for (var i = _transcript.Count - 1; i >= 0; i--)
        {
            if (_transcript[i].Owner == owner && _transcript[i].SegmentId == segmentId)
            {
                return _transcript[i];
            }
        }
        return null;
    }

    public void Clear()
    {
        _segments.Clear();
        _transcript.Clear();
    }

    private bool ApplyCorrection(Caption update)
    {
        if (!update.Final)
        {
            return false;
        }
        for (var i = _transcript.Count - 1; i >= 0; i--)
        {
            var current = _transcript[i];
            if (current.Owner != update.Owner || current.SegmentId != update.SegmentId)
            {
                continue;
            }
            if (update.Revision <= current.Revision)
            {
                return false;
            }
            // Fica no mesmo lugar da conversa, com o horário em que foi dito.
            var corrected = update with { UpdatedAt = current.UpdatedAt };
            _transcript[i] = corrected;
            var key = (update.Owner, update.SegmentId);
            if (_segments.ContainsKey(key))
            {
                _segments[key] = corrected;
            }
            return true;
        }
        return false;
    }

    /// <summary>Legenda recebida de quem traduz, trazida para o ponto de vista de quem recebe.</summary>
    public static Caption FromMessage(CaptionMessage message, DateTimeOffset now) => new(
        message.Speaker == CaptionSpeaker.Sender ? CaptionOwner.Other : CaptionOwner.Me,
        message.SegmentId,
        message.Revision,
        message.SourceLanguage,
        message.SourceText,
        message.TargetLanguage,
        message.TranslatedText,
        message.Final,
        now,
        message.Edited);

    /// <summary>Legenda feita neste computador, no ponto de vista de quem vai recebê-la.</summary>
    public static CaptionMessage ToMessage(Caption caption) => new(
        caption.Owner == CaptionOwner.Me ? CaptionSpeaker.Sender : CaptionSpeaker.Receiver,
        caption.SegmentId,
        caption.Revision,
        caption.SourceLanguage,
        caption.SourceText,
        caption.TargetLanguage,
        caption.TranslatedText,
        caption.Final,
        caption.Edited);
}
