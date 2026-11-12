using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Social;

namespace DinoTranslator.Desktop.Tests;

/// <summary>Glossário na tradução: termos protegidos por marcadores que voltam no lugar certo.</summary>
public sealed class GlossaryTests
{
    private static readonly Glossary Terms = new(
    [
        new GlossaryEntry("Rosa"),
        new GlossaryEntry("Sprint Review"),
        new GlossaryEntry("kanban"),
        new GlossaryEntry("Pão de Açúcar", "Сахарная Голова", Languages.Russian),
    ]);

    /// <summary>"Tradutor" que só marca o texto, como o motor simulado: mostra o que chegou ao modelo.</summary>
    private static Task<string> Echo(string text) => Task.FromResult($"[ru] {text}");

    [Fact]
    public async Task Nome_fica_como_esta_e_traducao_dada_vale_para_o_idioma()
    {
        var sent = new List<string>();

        var result = await Terms.TranslateAsync("Amanhã a Rosa apresenta o Sprint Review no Pão de Açúcar.", Languages.Russian, text =>
        {
            sent.Add(text);
            return Echo(text);
        });

        Assert.Equal(["Amanhã a X1Q apresenta o X2Q no X3Q."], sent);
        Assert.Equal("[ru] Amanhã a Rosa apresenta o Sprint Review no Сахарная Голова.", result);
    }

    [Fact]
    public void Nome_com_maiuscula_so_casa_escrito_igual_e_termo_minusculo_casa_de_qualquer_jeito()
    {
        var (text, slots) = Terms.Protect("A blusa rosa da Rosa tem um Kanban e um KANBAN.", Languages.English);

        Assert.Equal("A blusa rosa da X1Q tem um X2Q e um X3Q.", text);
        Assert.Equal(["Rosa", "Kanban", "KANBAN"], slots.Select(s => s.Value));
    }

    [Fact]
    public void So_palavra_inteira_e_o_termo_mais_longo_primeiro()
    {
        var glossary = new Glossary([new GlossaryEntry("Rosa"), new GlossaryEntry("Rosa Maria")]);

        var (text, slots) = glossary.Protect("Rosalina e Rosa Maria e Rosa.", Languages.English);

        Assert.Equal("Rosalina e X1Q e X2Q.", text);
        Assert.Equal(["Rosa Maria", "Rosa"], slots.Select(s => s.Value));
    }

    [Fact]
    public void Traducao_de_outro_idioma_nao_protege_o_termo()
    {
        var (text, slots) = Terms.Protect("Vamos ao Pão de Açúcar.", Languages.English);

        Assert.Equal("Vamos ao Pão de Açúcar.", text);
        Assert.Empty(slots);
    }

    [Fact]
    public async Task Marcador_perdido_traduz_de_novo_sem_o_glossario()
    {
        var sent = new List<string>();

        var result = await Terms.TranslateAsync("A Rosa chegou.", Languages.Russian, text =>
        {
            sent.Add(text);
            // O modelo "comeu" o marcador na primeira vez.
            return Task.FromResult(text.Contains("X1Q") ? "Пришла." : $"[ru] {text}");
        });

        Assert.Equal(["A X1Q chegou.", "A Rosa chegou."], sent);
        Assert.Equal("[ru] A Rosa chegou.", result);
    }

    [Fact]
    public void Marcador_repetido_tambem_nao_vale()
    {
        var slots = new[] { new KeyValuePair<string, string>("X1Q", "Rosa") };

        Assert.Null(Glossary.Restore("X1Q e X1Q", slots));
        Assert.Equal("Rosa e Anna", Glossary.Restore("X1Q e Anna", slots));
    }

    [Fact]
    public void Texto_que_ja_tem_algo_parecido_com_marcador_usa_outro_formato()
    {
        var (text, _) = Terms.Protect("O código X1Q é da Rosa.", Languages.English);

        Assert.Equal("O código X1Q é da Z1J.", text);
    }

    [Fact]
    public void Pessoal_vale_por_cima_do_grupo_e_a_chave_muda_com_os_termos()
    {
        var group = new Glossary([new GlossaryEntry("Kanban", "Канбан", Languages.Russian), new GlossaryEntry("Scrum")]);
        var personal = new Glossary([new GlossaryEntry("kanban", "доска", Languages.Russian)]);

        var merged = Glossary.Merge(personal, group);

        Assert.Equal(
            [new GlossaryEntry("kanban", "доска", Languages.Russian), new GlossaryEntry("Scrum")],
            merged.Entries);
        Assert.NotEqual(group.Key, merged.Key);
        Assert.Equal(new Glossary([new GlossaryEntry("Scrum")]).Key, new Glossary([new GlossaryEntry("Scrum")]).Key);
        Assert.Equal(string.Empty, Glossary.Empty.Key);
    }

    [Fact]
    public void Dica_para_o_reconhecimento_tem_os_termos_e_as_traducoes_do_idioma()
    {
        Assert.Equal(["Rosa", "Sprint Review", "kanban", "Pão de Açúcar", "Сахарная Голова"], Terms.Phrases(Languages.Russian));
        Assert.Equal(["Rosa", "Sprint Review", "kanban", "Pão de Açúcar"], Terms.Phrases(Languages.English));
    }
}
