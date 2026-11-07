using DinoTranslator.Shared;
using DinoTranslator.Shared.Call;
using Microsoft.Extensions.Time.Testing;

namespace DinoTranslator.Desktop.Tests;

public class CaptionBoardTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero));

    private static Caption Mine(string segment, int revision, string text, bool final = false) =>
        new(CaptionOwner.Me, segment, revision, Languages.PortugueseBrazil, text, Languages.Russian, $"[ru] {text}", final, default);

    private static Caption Theirs(string segment, int revision, string text, bool final = false) =>
        new(CaptionOwner.Other, segment, revision, Languages.Russian, text, Languages.PortugueseBrazil, $"[pt] {text}", final, default);

    [Fact]
    public void Provisoria_e_trocada_pela_revisao_seguinte_e_depois_pela_final()
    {
        var board = new CaptionBoard(_time);

        board.Apply(Mine("s0", 0, "Oi"));
        board.Apply(Mine("s0", 1, "Oi, tudo"));
        board.Apply(Mine("s0", 2, "Oi, tudo bem?", final: true));

        var visible = Assert.Single(board.Visible());
        Assert.Equal("Oi, tudo bem?", visible.SourceText);
        Assert.True(visible.Final);
        Assert.Single(board.Transcript);
    }

    [Fact]
    public void Revisao_atrasada_ou_depois_do_final_e_ignorada()
    {
        var board = new CaptionBoard(_time);
        board.Apply(Mine("s0", 3, "Oi, tudo bem?"));

        Assert.False(board.Apply(Mine("s0", 2, "Oi, tu")));
        Assert.True(board.Apply(Mine("s0", 4, "Oi, tudo bem?", final: true)));
        Assert.False(board.Apply(Mine("s0", 5, "Oi, tudo bem? E")));
        Assert.Equal("Oi, tudo bem?", Assert.Single(board.Visible()).SourceText);
    }

    [Fact]
    public void Mostra_a_fala_mais_recente_de_cada_pessoa_em_ordem()
    {
        var board = new CaptionBoard(_time);
        board.Apply(Mine("s0", 0, "primeira", final: true));
        _time.Advance(TimeSpan.FromSeconds(1));
        board.Apply(Theirs("s0", 0, "resposta"));
        _time.Advance(TimeSpan.FromSeconds(1));
        board.Apply(Mine("s1", 0, "segunda"));

        Assert.Equal(["resposta", "segunda"], board.Visible().Select(c => c.SourceText));
    }

    [Fact]
    public void Legenda_final_some_depois_de_oito_segundos_mas_fica_no_historico()
    {
        var board = new CaptionBoard(_time);
        board.Apply(Theirs("s0", 0, "até logo", final: true));

        _time.Advance(TimeSpan.FromSeconds(9));

        Assert.Empty(board.Visible());
        Assert.Equal("até logo", Assert.Single(board.Transcript).SourceText);
    }

    [Fact]
    public void Fala_em_andamento_nao_some()
    {
        var board = new CaptionBoard(_time);
        board.Apply(Theirs("s0", 0, "uma fala bem longa"));

        _time.Advance(TimeSpan.FromSeconds(20));

        Assert.Single(board.Visible());
    }

    [Fact]
    public void Mesmo_id_de_trecho_de_pessoas_diferentes_nao_se_mistura()
    {
        var board = new CaptionBoard(_time);

        board.Apply(Mine("s0", 5, "minha"));
        board.Apply(Theirs("s0", 0, "dela"));

        Assert.Equal(2, board.Visible().Count);
    }

    [Fact]
    public void Ponto_de_vista_inverte_entre_quem_traduz_e_quem_recebe()
    {
        var mine = Mine("s4", 1, "Bom dia");

        var received = CaptionBoard.FromMessage(CaptionBoard.ToMessage(mine), _time.GetUtcNow());

        Assert.Equal(CaptionOwner.Other, received.Owner);
        Assert.Equal(CaptionOwner.Me, CaptionBoard.FromMessage(CaptionBoard.ToMessage(Theirs("s1", 0, "x")), _time.GetUtcNow()).Owner);
        Assert.Equal(mine.TranslatedText, received.TranslatedText);
    }
}
