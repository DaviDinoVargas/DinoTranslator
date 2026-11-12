using System.Collections.Concurrent;
using System.Threading.Channels;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Shared;

namespace DinoTranslator.Desktop.Services.Plus;

/// <summary>
/// Traduz as mensagens do chat da versão paga para o idioma de quem lê, neste computador, com o
/// motor configurado. Uma por vez, numa fila, sem travar a tela; o resultado fica guardado
/// enquanto o app está aberto.
/// </summary>
public sealed class MessageTranslator : IAsyncDisposable
{
    private readonly SpeechEngineFactory _engines;
    private readonly TranslationSettingsStore _settings;
    private readonly ConcurrentDictionary<string, string> _done = new();
    private readonly ConcurrentDictionary<string, byte> _queued = new();
    private readonly Channel<(string Key, string Text, string Source, string Target)> _queue =
        Channel.CreateBounded<(string, string, string, string)>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    public MessageTranslator(SpeechEngineFactory engines, TranslationSettingsStore settings)
    {
        _engines = engines;
        _settings = settings;
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>Uma tradução ficou pronta (o app redesenha o chat).</summary>
    public event Action? Translated;

    /// <summary>Só motores com licença comercial (a versão Plus é paga): sem o MarianMT, nada de NLLB.</summary>
    public bool Available => _engines.CreateForPlus(_settings.Current) is not null;

    public string? Problem => _engines.PlusTranslationProblem(_settings.Current);

    /// <summary>A tradução, se já estiver pronta; senão, pede e devolve null.</summary>
    public string? Get(long messageId, DateTimeOffset? editedAt, string text, string source, string target)
    {
        if (source == target || string.IsNullOrWhiteSpace(text) || !Languages.IsSupported(source) || !Languages.IsSupported(target))
        {
            return null;
        }
        var key = $"{messageId}:{editedAt?.UtcTicks ?? 0}:{target}";
        if (_done.TryGetValue(key, out var translated))
        {
            return translated;
        }
        if (Available && _queued.TryAdd(key, 0))
        {
            _queue.Writer.TryWrite((key, text, source, target));
        }
        return null;
    }

    /// <summary>Traduz um texto avulso (a própria mensagem antes de mandar, por exemplo).</summary>
    public async Task<string?> TranslateAsync(string text, string source, string target, CancellationToken cancellationToken)
    {
        if (_engines.CreateForPlus(_settings.Current) is not { CanTranslateText: true } engine)
        {
            return null;
        }
        return await engine.TranslateTextAsync(text, source, target, cancellationToken);
    }

    private async Task WorkAsync()
    {
        await foreach (var (key, text, source, target) in _queue.Reader.ReadAllAsync(_stop.Token))
        {
            try
            {
                if (await TranslateAsync(text, source, target, _stop.Token) is { Length: > 0 } result)
                {
                    _done[key] = LiveTranslation.Clean(result);
                    Translated?.Invoke();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Motor indisponível agora: a mensagem fica só no original e pode ser pedida de novo.
                _queued.TryRemove(key, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}
