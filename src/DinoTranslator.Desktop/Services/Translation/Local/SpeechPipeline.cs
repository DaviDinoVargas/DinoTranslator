using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace DinoTranslator.Desktop.Services.Translation.Local;

/// <summary>
/// Linha de montagem da fala de uma pessoa: o reconhecimento (placa de vídeo) e a tradução
/// (processador) trabalham ao mesmo tempo, cada um na sua fila.
/// <list type="bullet">
/// <item>O texto reconhecido aparece assim que sai, sem esperar a tradução.</item>
/// <item>A tradução vai sendo adiantada com as provisórias (só a mais nova; as atrasadas são puladas).</item>
/// <item>No fim do trecho, se o texto final for o mesmo da última provisória traduzida, a tradução
/// já está pronta e a legenda final sai na hora.</item>
/// </list>
/// </summary>
public sealed class SpeechPipeline : IAsyncDisposable
{
    private readonly Func<SpeechSegment, CancellationToken, Task<string>> _recognize;
    private readonly Func<string, CancellationToken, Task<string>>? _translate;
    private readonly Channel<SpeechSegment> _segments = Channel.CreateUnbounded<SpeechSegment>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _emit = new();
    private readonly Dictionary<int, int> _revisions = [];
    private readonly Dictionary<int, string> _latestText = [];
    private readonly Dictionary<int, string> _latestTranslation = [];
    private readonly HashSet<int> _finished = [];
    private readonly Task _recognition;
    private readonly Task _translation;
    private (int Segment, string Key, string Translation)? _cache;
    private CancellationTokenSource? _inFlight;
    private (int Segment, string Key)? _inFlightText;
    private int _pendingSegments;
    private int _pendingJobs;

    /// <param name="recognize">Recebe o trecho inteiro: a versão final pode ser reconhecida com mais cuidado.</param>
    /// <param name="translate"><c>null</c> quando os dois falam o mesmo idioma.</param>
    public SpeechPipeline(Func<SpeechSegment, CancellationToken, Task<string>> recognize, Func<string, CancellationToken, Task<string>>? translate)
    {
        _recognize = recognize;
        _translate = translate;
        _recognition = Task.Run(RecognizeLoopAsync);
        _translation = translate is null ? Task.CompletedTask : Task.Run(TranslateLoopAsync);
    }

    /// <summary>Disparado nas threads de trabalho; revisões só crescem dentro de cada trecho.</summary>
    public event Action<SpeechUpdate>? Updated;

    public event Action? Failed;

    public void Enqueue(SpeechSegment segment)
    {
        if (_segments.Writer.TryWrite(segment))
        {
            Interlocked.Increment(ref _pendingSegments);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _segments.Writer.TryComplete();
        _jobs.Writer.TryComplete();
        await _stop.CancelAsync();
        try
        {
            await Task.WhenAll(_recognition, _translation).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Encerrando: trecho em andamento é descartado.
        }
        _stop.Dispose();
    }

    /// <summary>Chave para comparar textos: sem maiúsculas, pontuação nem espaços repetidos.</summary>
    public static string Key(string text)
    {
        var key = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c))
            {
                key.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else if (char.IsWhiteSpace(c) && key.Length > 0 && key[^1] != ' ')
            {
                key.Append(' ');
            }
        }
        return key.ToString().Trim();
    }

    private async Task RecognizeLoopAsync()
    {
        try
        {
            await foreach (var segment in _segments.Reader.ReadAllAsync(_stop.Token))
            {
                // Provisória atrasada: se já chegou coisa mais nova, ela fica para trás.
                if (Interlocked.Decrement(ref _pendingSegments) > 0 && !segment.Final)
                {
                    continue;
                }
                var text = await _recognize(segment, _stop.Token);
                if (text.Length == 0)
                {
                    string? last;
                    lock (_emit)
                    {
                        last = _latestText.GetValueOrDefault(segment.Index);
                    }
                    if (segment.Final && last is not null)
                    {
                        // Fechou sem texto novo: a última provisória vira a final.
                        FinishWith(segment.Index, last);
                    }
                    continue;
                }

                if (_translate is null)
                {
                    Emit(segment.Index, text, null, segment.Final);
                    continue;
                }
                // O texto aparece já, com a última tradução conhecida; a nova chega em seguida.
                Emit(segment.Index, text, translation: null, final: false);
                Post(new Job(segment.Index, text, segment.Final));
            }
        }
        catch (OperationCanceledException)
        {
            // Chamada encerrada.
        }
        catch (Exception)
        {
            Failed?.Invoke();
        }
    }

    private void FinishWith(int segment, string text)
    {
        if (_translate is null)
        {
            Emit(segment, text, null, final: true);
        }
        else
        {
            Post(new Job(segment, text, Final: true));
        }
    }

    private void Post(Job job)
    {
        if (_jobs.Writer.TryWrite(job))
        {
            Interlocked.Increment(ref _pendingJobs);
        }
        if (job.Final)
        {
            // A frase acabou: a tradução provisória em andamento só interessa se for do mesmo
            // texto (aí ela vira a final, pelo cache); senão é abandonada.
            lock (_emit)
            {
                if (_inFlightText != (job.Segment, Key(job.Text)))
                {
                    _inFlight?.Cancel();
                }
            }
        }
    }

    private async Task TranslateLoopAsync()
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(_stop.Token))
            {
                // Tradução de provisória atrasada é pulada; a do trecho final nunca.
                if (Interlocked.Decrement(ref _pendingJobs) > 0 && !job.Final)
                {
                    continue;
                }
                var key = Key(job.Text);
                string translation;
                if (_cache is { } cached && cached.Segment == job.Segment && cached.Key == key)
                {
                    translation = cached.Translation;
                }
                else
                {
                    using var attempt = job.Final ? null : CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    lock (_emit)
                    {
                        _inFlight = attempt;
                        _inFlightText = (job.Segment, key);
                    }
                    try
                    {
                        translation = await _translate!(job.Text, attempt?.Token ?? _stop.Token);
                    }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                    {
                        // Provisória abandonada porque o trecho fechou: segue para a final.
                        continue;
                    }
                    finally
                    {
                        lock (_emit)
                        {
                            _inFlight = null;
                            _inFlightText = null;
                        }
                    }
                    _cache = (job.Segment, key, translation);
                }
                Emit(job.Segment, job.Text, translation, job.Final);
            }
        }
        catch (OperationCanceledException)
        {
            // Chamada encerrada.
        }
        catch (Exception)
        {
            Failed?.Invoke();
        }
    }

    /// <summary>
    /// Uma atualização por vez, com a revisão sempre maior. O texto é sempre o reconhecido mais
    /// novo do trecho e a tradução, a mais nova pronta (numa provisória ela pode vir um pouco atrás).
    /// </summary>
    private void Emit(int segment, string text, string? translation, bool final)
    {
        SpeechUpdate update;
        lock (_emit)
        {
            if (_finished.Contains(segment))
            {
                return;
            }
            if (final)
            {
                _finished.Add(segment);
                _latestText[segment] = text;
            }
            else if (translation is null)
            {
                _latestText[segment] = text;
            }
            if (translation is not null)
            {
                _latestTranslation[segment] = translation;
            }
            var revision = _revisions.GetValueOrDefault(segment);
            _revisions[segment] = revision + 1;
            update = new SpeechUpdate(
                segment,
                revision,
                final ? text : _latestText.GetValueOrDefault(segment, text),
                _translate is null ? null : _latestTranslation.GetValueOrDefault(segment),
                final);
            Forget(segment);
        }
        Updated?.Invoke(update);
    }

    /// <summary>Guarda só os trechos recentes.</summary>
    private void Forget(int current)
    {
        foreach (var old in _revisions.Keys.Where(k => k < current - 8).ToList())
        {
            _revisions.Remove(old);
            _latestText.Remove(old);
            _latestTranslation.Remove(old);
            _finished.Remove(old);
        }
    }

    private sealed record Job(int Segment, string Text, bool Final);
}
