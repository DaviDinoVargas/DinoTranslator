using System.Globalization;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Call;

namespace DinoTranslator.Desktop.Services.Translation;

public enum TranslatorSide
{
    /// <summary>Nenhum dos dois tem motor configurado: a chamada segue sem legendas.</summary>
    None,

    /// <summary>Este computador reconhece e traduz as duas falas.</summary>
    Here,

    /// <summary>O computador da outra pessoa traduz e manda as legendas.</summary>
    There,
}

public static class TranslatorChoice
{
    /// <summary>
    /// O anfitrião traduz se tiver motor; senão, o convidado. Os dois lados chegam à mesma
    /// resposta sem combinar nada além da apresentação.
    /// </summary>
    public static TranslatorSide Decide(CallRole role, bool canTranslateHere, bool canTranslateThere)
    {
        var (hostCan, guestCan) = role == CallRole.Host
            ? (canTranslateHere, canTranslateThere)
            : (canTranslateThere, canTranslateHere);
        var (host, guest) = role == CallRole.Host
            ? (TranslatorSide.Here, TranslatorSide.There)
            : (TranslatorSide.There, TranslatorSide.Here);
        return hostCan ? host : guestCan ? guest : TranslatorSide.None;
    }
}

/// <summary>
/// Tradução ao vivo no computador que traduz: a fala de cada pessoa vira legenda no idioma
/// da outra. A sua fala entra pelo microfone; a da outra pessoa, pelo áudio da chamada.
/// </summary>
/// <param name="generation">
/// Quantas vezes a tradução já recomeçou nesta chamada (troca de motores): entra no id dos trechos,
/// que senão repetiriam os da tradução anterior e seriam recusados como já finalizados.
/// </param>
public sealed class LiveTranslation(ISpeechTranslationEngine engine, Profile me, Profile peer, TimeProvider time, int generation = 0) : IAsyncDisposable
{
    private ISpeechTranslationStream? _mine;
    private ISpeechTranslationStream? _theirs;

    /// <summary>Disparado na thread do motor.</summary>
    public event Action<Caption>? CaptionReady;

    /// <summary>Tempos de cada frase final do motor local (disparado na thread do motor).</summary>
    public event Action<SpeechTiming>? Measured;

    public event Action<string>? Failed;

    public async Task StartAsync(IReadOnlyList<string> glossary, CancellationToken cancellationToken)
    {
        _mine = await OpenAsync(me.Language, peer.Language, CaptionOwner.Me, glossary, cancellationToken);
        _theirs = await OpenAsync(peer.Language, me.Language, CaptionOwner.Other, glossary, cancellationToken);
    }

    public bool CanTranslateText => engine.CanTranslateText;

    /// <summary>
    /// Texto escrito (ou corrigido) por uma das pessoas, no idioma da outra; <c>null</c> se os
    /// dois falam o mesmo idioma ou o motor não traduz texto.
    /// </summary>
    public async Task<string?> TranslateTextAsync(string text, CaptionOwner author, CancellationToken cancellationToken)
    {
        var (source, target) = author == CaptionOwner.Me ? (me.Language, peer.Language) : (peer.Language, me.Language);
        if (!Languages.NeedsTranslation(source, target))
        {
            return null;
        }
        return await engine.TranslateTextAsync(text, source, target, cancellationToken) is { } translated ? Clean(translated) : null;
    }

    /// <param name="source">"local" (microfone) ou "remote" (áudio da outra pessoa).</param>
    public void WriteAudio(string source, byte[] pcm)
    {
        var stream = source switch
        {
            "local" => _mine,
            "remote" => _theirs,
            _ => null,
        };
        stream?.Write(pcm);
    }

    public async ValueTask DisposeAsync()
    {
        if (_mine is not null)
        {
            await _mine.DisposeAsync();
        }
        if (_theirs is not null)
        {
            await _theirs.DisposeAsync();
        }
    }

    /// <summary>Texto do motor pronto para a tela e para o canal: sem controle e no tamanho máximo.</summary>
    public static string Clean(string text)
    {
        var cleaned = string.Concat(text.Select(c =>
            char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c)).Trim();
        // Fala longa demais: fica o final, que é o que a pessoa está dizendo agora.
        return cleaned.Length <= CaptionMessage.MaxTextLength ? cleaned : cleaned[^CaptionMessage.MaxTextLength..];
    }

    private async Task<ISpeechTranslationStream> OpenAsync(
        string source, string target, CaptionOwner owner, IReadOnlyList<string> glossary, CancellationToken cancellationToken)
    {
        var stream = await engine.StartAsync(new SpeechStreamOptions(source, target, glossary), cancellationToken);
        var translate = Languages.NeedsTranslation(source, target);
        stream.Updated += update =>
        {
            CaptionReady?.Invoke(new Caption(
                owner,
                generation == 0 ? $"s{update.Segment}" : $"s{generation}-{update.Segment}",
                update.Revision,
                source,
                Clean(update.SourceText),
                target,
                translate && update.TranslatedText is { } translated ? Clean(translated) : null,
                update.Final,
                time.GetUtcNow()));
            if (update.Timing is { } timing)
            {
                Measured?.Invoke(timing);
            }
        };
        stream.Failed += message => Failed?.Invoke(message);
        return stream;
    }
}
