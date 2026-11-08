using System.Collections.Concurrent;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Local;

namespace DinoTranslator.Desktop.Tests;

public sealed class SpeechPipelineTests
{
    /// <summary>"Áudio" de teste: o primeiro valor diz qual texto o reconhecedor devolve.</summary>
    private static readonly string[] Texts = ["", "Olá Anna", "Olá Anna, hoje o dia", "Olá Anna, hoje o dia está lindo.", "Tudo bem?"];

    [Fact]
    public async Task Texto_aparece_sem_esperar_a_traducao_e_a_final_vem_traduzida()
    {
        var gate = new TaskCompletionSource();
        var updates = new ConcurrentQueue<SpeechUpdate>();
        await using var pipeline = new SpeechPipeline(Recognize(), async (text, careful, token) =>
        {
            await gate.Task.WaitAsync(token);
            return Translated(text, careful);
        });
        pipeline.Updated += updates.Enqueue;

        pipeline.Enqueue(Segment(0, text: 1, final: false));
        pipeline.Enqueue(Segment(0, text: 3, final: true));
        await WaitUntil(() => updates.Any(u => u.SourceText == Texts[3]));

        // Tradução ainda presa, e o texto final já está na tela (como provisória).
        Assert.DoesNotContain(updates, u => u.Final);
        gate.SetResult();

        var final = await WaitFor(updates, u => u.Final);
        Assert.Equal(Texts[3], final.SourceText);
        Assert.Equal(Translated(Texts[3], careful: true), final.TranslatedText);
        Assert.Single(updates, u => u.Final);
        Assert.Equal(Enumerable.Range(0, updates.Count), updates.Select(u => u.Revision));
    }

    [Fact]
    public async Task Final_igual_a_ultima_provisoria_mostra_a_rapida_e_termina_com_a_cuidadosa()
    {
        var calls = new ConcurrentQueue<(string Text, bool Careful)>();
        var updates = new ConcurrentQueue<SpeechUpdate>();
        await using var pipeline = new SpeechPipeline(Recognize(), (text, careful, _) =>
        {
            calls.Enqueue((text, careful));
            return Task.FromResult(Translated(text, careful));
        });
        pipeline.Updated += updates.Enqueue;

        pipeline.Enqueue(Segment(0, text: 2, final: false));
        await WaitUntil(() => updates.Any(u => u.TranslatedText is not null));
        pipeline.Enqueue(Segment(0, text: 2, final: true));

        var final = await WaitFor(updates, u => u.Final);
        Assert.Equal(Translated(Texts[2], careful: true), final.TranslatedText);
        Assert.Contains(updates, u => !u.Final && u.TranslatedText == Translated(Texts[2], careful: false));
        Assert.Equal([(Texts[2], false), (Texts[2], true)], calls);
    }

    [Fact]
    public async Task Final_repetida_reaproveita_a_traducao_cuidadosa()
    {
        var calls = new ConcurrentQueue<(string Text, bool Careful)>();
        var updates = new ConcurrentQueue<SpeechUpdate>();
        await using var pipeline = new SpeechPipeline(Recognize(), (text, careful, _) =>
        {
            calls.Enqueue((text, careful));
            return Task.FromResult(Translated(text, careful));
        });
        pipeline.Updated += updates.Enqueue;

        // Reconhecimento sem texto novo no fim: a última provisória fecha o trecho, traduzida com cuidado.
        pipeline.Enqueue(Segment(0, text: 2, final: false));
        await WaitUntil(() => updates.Any(u => u.TranslatedText is not null));
        pipeline.Enqueue(Segment(0, text: 0, final: true));

        var final = await WaitFor(updates, u => u.Final);
        Assert.Equal(Translated(Texts[2], careful: true), final.TranslatedText);
        Assert.Single(calls, c => c.Careful);
    }

    [Fact]
    public async Task Provisorias_atrasadas_sao_puladas_mas_o_final_nunca()
    {
        var release = new TaskCompletionSource();
        var calls = new ConcurrentQueue<string>();
        var updates = new ConcurrentQueue<SpeechUpdate>();
        await using var pipeline = new SpeechPipeline(Recognize(), async (text, careful, token) =>
        {
            calls.Enqueue(text);
            await release.Task.WaitAsync(token);
            return Translated(text, careful);
        });
        pipeline.Updated += updates.Enqueue;

        pipeline.Enqueue(Segment(0, text: 1, final: false));
        await WaitUntil(() => !calls.IsEmpty);
        pipeline.Enqueue(Segment(0, text: 2, final: false));
        pipeline.Enqueue(Segment(0, text: 3, final: true));
        pipeline.Enqueue(Segment(1, text: 4, final: true));
        await WaitUntil(() => updates.Any(u => u.SourceText == Texts[4]));
        release.SetResult();

        await WaitUntil(() => updates.Count(u => u.Final) == 2);
        Assert.DoesNotContain(Texts[2], calls);
        Assert.Equal([Texts[1], Texts[3], Texts[4]], calls);
        Assert.Equal([Translated(Texts[3], careful: true), Translated(Texts[4], careful: true)], updates.Where(u => u.Final).Select(u => u.TranslatedText));
    }

    [Fact]
    public async Task Final_cancela_a_provisoria_de_outro_texto_mas_aproveita_a_do_mesmo()
    {
        var started = new ConcurrentQueue<string>();
        var cancelled = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource();
        var updates = new ConcurrentQueue<SpeechUpdate>();
        await using var pipeline = new SpeechPipeline(Recognize(), async (text, careful, token) =>
        {
            started.Enqueue(text);
            try
            {
                await release.Task.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                cancelled.Enqueue(text);
                throw;
            }
            return Translated(text, careful);
        });
        pipeline.Updated += updates.Enqueue;

        // Trecho 0: a provisória (texto 1) é abandonada quando o final traz outro texto.
        pipeline.Enqueue(Segment(0, text: 1, final: false));
        await WaitUntil(() => started.Count == 1);
        pipeline.Enqueue(Segment(0, text: 3, final: true));
        await WaitUntil(() => !cancelled.IsEmpty);
        release.SetResult();
        await WaitUntil(() => updates.Any(u => u.Final));
        Assert.Equal([Texts[1]], cancelled);

        // Trecho 1: o final repete o texto da provisória, que fica na tela até a cuidadosa chegar.
        var before = started.Count;
        pipeline.Enqueue(Segment(1, text: 4, final: false));
        await WaitUntil(() => updates.Any(u => u.Segment == 1 && u.TranslatedText is not null));
        pipeline.Enqueue(Segment(1, text: 4, final: true));
        var final = await WaitFor(updates, u => u.Segment == 1 && u.Final);
        Assert.Equal(Translated(Texts[4], careful: true), final.TranslatedText);
        Assert.Equal(before + 2, started.Count);
        Assert.Empty(cancelled.Skip(1));
    }

    [Fact]
    public async Task Mesmo_idioma_a_final_sai_direto_sem_traducao()
    {
        var updates = new ConcurrentQueue<SpeechUpdate>();
        await using var pipeline = new SpeechPipeline(Recognize(), translate: null);
        pipeline.Updated += updates.Enqueue;

        pipeline.Enqueue(Segment(0, text: 1, final: false));
        pipeline.Enqueue(Segment(0, text: 3, final: true));

        var final = await WaitFor(updates, u => u.Final);
        Assert.Equal(Texts[3], final.SourceText);
        Assert.All(updates, u => Assert.Null(u.TranslatedText));
    }

    [Fact]
    public async Task Final_sem_texto_novo_fecha_com_a_ultima_provisoria()
    {
        var updates = new ConcurrentQueue<SpeechUpdate>();
        await using var pipeline = new SpeechPipeline(Recognize(), (text, careful, _) => Task.FromResult(Translated(text, careful)));
        pipeline.Updated += updates.Enqueue;

        pipeline.Enqueue(Segment(0, text: 2, final: false));
        await WaitUntil(() => updates.Any(u => u.SourceText == Texts[2]));
        pipeline.Enqueue(Segment(0, text: 0, final: true));

        var final = await WaitFor(updates, u => u.Final);
        Assert.Equal(Texts[2], final.SourceText);
        Assert.Equal(Translated(Texts[2], careful: true), final.TranslatedText);
    }

    [Theory]
    [InlineData("Olá, Anna!", "olá anna")]
    [InlineData("  Olá   Anna. ", "olá anna")]
    [InlineData("ПРИВЕТ, мир", "привет мир")]
    public void Chave_ignora_pontuacao_e_maiusculas(string text, string expected)
    {
        Assert.Equal(expected, SpeechPipeline.Key(text));
    }

    /// <summary>Tradução de mentira que mostra se foi a rápida ou a cuidadosa.</summary>
    private static string Translated(string text, bool careful) => (careful ? "[ru+] " : "[ru] ") + text;

    private static Func<SpeechSegment, CancellationToken, Task<string>> Recognize() =>
        async (segment, token) =>
        {
            await Task.Delay(5, token);
            return Texts[(int)segment.Samples[0]];
        };

    private static SpeechSegment Segment(int index, int text, bool final) => new(index, [text], final);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    private static async Task<SpeechUpdate> WaitFor(ConcurrentQueue<SpeechUpdate> updates, Func<SpeechUpdate, bool> match)
    {
        await WaitUntil(() => updates.Any(match));
        return updates.First(match);
    }
}
