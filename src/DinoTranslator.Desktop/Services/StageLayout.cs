namespace DinoTranslator.Desktop.Services;

/// <summary>O que pode ficar em destaque no palco da chamada.</summary>
public enum StageSource
{
    /// <summary>A câmera da outra pessoa.</summary>
    PeerCamera,

    /// <summary>A tela que a outra pessoa está compartilhando.</summary>
    PeerScreen,

    /// <summary>A tela que você está compartilhando (para conferir o que ela vê).</summary>
    MyScreen,
}

/// <summary>
/// Quem fica em destaque e quem vira miniatura (na ordem de cima para baixo; a sua câmera vem
/// sempre depois delas). A escolha da pessoa vale enquanto a fonte existir; sem escolha, a tela da
/// outra pessoa tem a preferência, porque é o que ela quer mostrar.
/// </summary>
public sealed record StageLayout(StageSource Main, IReadOnlyList<StageSource> Thumbnails)
{
    public static StageLayout For(StageSource? chosen, bool peerScreen, bool myScreen)
    {
        var available = new List<StageSource> { StageSource.PeerCamera };
        if (peerScreen)
        {
            available.Add(StageSource.PeerScreen);
        }
        if (myScreen)
        {
            available.Add(StageSource.MyScreen);
        }
        var main = chosen is { } choice && available.Contains(choice)
            ? choice
            : peerScreen ? StageSource.PeerScreen : StageSource.PeerCamera;
        return new StageLayout(main, available.Where(source => source != main).ToList());
    }

    /// <summary>Há mais de uma coisa para ver: as miniaturas viram botões de escolha.</summary>
    public bool HasChoice => Thumbnails.Count > 0;

    /// <summary>Posição da miniatura (0 = a de cima) ou -1 se a fonte estiver em destaque ou ausente.</summary>
    public int SlotOf(StageSource source)
    {
        for (var i = 0; i < Thumbnails.Count; i++)
        {
            if (Thumbnails[i] == source)
            {
                return i;
            }
        }
        return -1;
    }
}
