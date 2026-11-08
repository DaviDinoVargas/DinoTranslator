using DinoTranslator.Desktop.Services.Translation.Local;

namespace DinoTranslator.Desktop.Tests;

public class BeamSearchTests
{
    private const int Vocabulary = 10;
    private const int End = (int)NllbVocabulary.EndOfSentence;

    [Fact]
    public void Acha_a_frase_mais_provavel_mesmo_quando_o_comeco_engana()
    {
        // A palavra 4 é a mais provável no começo, mas depois dela tudo é incerto; a 5 começa
        // menos provável e termina com certeza. A escolha palavra a palavra ficaria com a 4.
        var search = new BeamSearch(width: 2, maxTokens: 5);

        search.Advance(Rows(Row((4, 0.2f), (5, 0f))), positions: 1, Vocabulary);
        Assert.Equal([4L, 5L], search.LastTokens());

        // Depois da 4: tudo igualmente provável. Depois da 5: fim de frase, com certeza.
        search.Advance(Rows(Row((4, 0f), (5, 0f), (6, 0f), (7, 0f), (8, 0f), (9, 0f), (End, 0f)), Row((End, 12f))), positions: 1, Vocabulary);
        search.Advance(Rows(Row((End, 12f)), Row((End, 12f))), positions: 1, Vocabulary);

        Assert.True(search.Done);
        Assert.Equal([5L], search.Best());
    }

    [Fact]
    public void Nunca_escolhe_codigo_de_idioma_nem_palavra_desconhecida()
    {
        var row = new float[NllbVocabulary.FirstLanguage + 10];
        row[NllbVocabulary.FirstLanguage + 3] = 50f;
        row[NllbVocabulary.Unknown] = 40f;
        row[7] = 5f;
        row[8] = 4f;
        var search = new BeamSearch(width: 2, maxTokens: 5);

        search.Advance(row, positions: 1, row.Length);

        Assert.Equal([7L, 8L], search.LastTokens());
    }

    [Fact]
    public void Para_no_limite_de_palavras()
    {
        var search = new BeamSearch(width: 2, maxTokens: 2);

        search.Advance(Rows(Row((4, 3f), (6, 2f))), positions: 1, Vocabulary);
        Assert.False(search.Done);
        search.Advance(Rows(Row((9, 3f)), Row((9, 3f))), positions: 1, Vocabulary);

        Assert.True(search.Done);
        Assert.Equal([4L, 9L], search.Best());
    }

    [Fact]
    public void Usa_a_ultima_posicao_de_cada_caminho()
    {
        // Primeiro passo com duas posições (início de frase e idioma): vale a segunda.
        var search = new BeamSearch(width: 2, maxTokens: 5);

        search.Advance(Rows(Row((9, 5f)), Row((6, 5f), (7, 4f))), positions: 2, Vocabulary);

        Assert.Equal([6L, 7L], search.LastTokens());
    }

    /// <summary>Linha de pontuações: bem baixa em tudo (sem empates), menos nas palavras indicadas.</summary>
    private static float[] Row(params (int Token, float Logit)[] values)
    {
        var row = Enumerable.Repeat(-10f, Vocabulary).ToArray();
        foreach (var (token, logit) in values)
        {
            row[token] = logit;
        }
        return row;
    }

    private static float[] Rows(params float[][] rows) => rows.SelectMany(r => r).ToArray();
}
