using DinoTranslator.Desktop.Services;

namespace DinoTranslator.Desktop.Tests;

public class StageLayoutTests
{
    [Fact]
    public void Sem_telas_a_camera_da_outra_pessoa_fica_em_destaque_sozinha()
    {
        var layout = StageLayout.For(null, peerScreen: false, myScreen: false);

        Assert.Equal(StageSource.PeerCamera, layout.Main);
        Assert.Empty(layout.Thumbnails);
        Assert.False(layout.HasChoice);
    }

    [Fact]
    public void Tela_da_outra_pessoa_vai_para_o_destaque()
    {
        var layout = StageLayout.For(null, peerScreen: true, myScreen: false);

        Assert.Equal(StageSource.PeerScreen, layout.Main);
        Assert.Equal([StageSource.PeerCamera], layout.Thumbnails);
    }

    [Fact]
    public void Sua_tela_aparece_em_miniatura_sem_tirar_a_outra_pessoa_do_destaque()
    {
        var layout = StageLayout.For(null, peerScreen: false, myScreen: true);

        Assert.Equal(StageSource.PeerCamera, layout.Main);
        Assert.Equal([StageSource.MyScreen], layout.Thumbnails);
        Assert.Equal(0, layout.SlotOf(StageSource.MyScreen));
    }

    [Fact]
    public void Com_as_duas_telas_da_para_escolher_qualquer_uma()
    {
        var mine = StageLayout.For(StageSource.MyScreen, peerScreen: true, myScreen: true);
        Assert.Equal(StageSource.MyScreen, mine.Main);
        Assert.Equal([StageSource.PeerCamera, StageSource.PeerScreen], mine.Thumbnails);

        var camera = StageLayout.For(StageSource.PeerCamera, peerScreen: true, myScreen: true);
        Assert.Equal(StageSource.PeerCamera, camera.Main);
        Assert.Equal([StageSource.PeerScreen, StageSource.MyScreen], camera.Thumbnails);
        Assert.Equal(-1, camera.SlotOf(StageSource.PeerCamera));
    }

    [Fact]
    public void Escolha_que_deixou_de_existir_volta_ao_padrao()
    {
        var layout = StageLayout.For(StageSource.MyScreen, peerScreen: true, myScreen: false);

        Assert.Equal(StageSource.PeerScreen, layout.Main);
        Assert.Equal(-1, layout.SlotOf(StageSource.MyScreen));
    }
}
