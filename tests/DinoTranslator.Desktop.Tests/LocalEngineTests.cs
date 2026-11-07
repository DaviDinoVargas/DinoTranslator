using System.IO;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Local;
using DinoTranslator.Shared;

namespace DinoTranslator.Desktop.Tests;

public class LocalEngineTests
{
    [Theory]
    [InlineData("pt-BR", 256_141)]
    [InlineData("en-US", 256_047)]
    [InlineData("uk-UA", 256_188)]
    [InlineData("ru-RU", 256_147)]
    [InlineData("es-ES", 256_161)]
    public void Codigo_de_idioma_do_tradutor(string language, long expected)
    {
        Assert.Equal(expected, NllbVocabulary.LanguageId(language));
    }

    [Fact]
    public void Todos_os_idiomas_tem_codigo_no_tradutor()
    {
        Assert.All(Languages.All, l => Assert.True(NllbVocabulary.LanguageId(l.Code) >= NllbVocabulary.FirstLanguage));
    }

    [Fact]
    public void Entrada_tem_idioma_texto_e_fim()
    {
        var source = NllbVocabulary.Source([0, 10, 25], Languages.Russian);

        Assert.Equal([256_147, NllbVocabulary.Unknown, 11, 26, NllbVocabulary.EndOfSentence], source);
    }

    [Fact]
    public void Entrada_longa_e_cortada_no_limite()
    {
        var source = NllbVocabulary.Source(Enumerable.Range(10, 1_000).ToList(), Languages.English);

        Assert.Equal(NllbVocabulary.MaxSourceTokens, source.Length);
        Assert.Equal(NllbVocabulary.EndOfSentence, source[^1]);
    }

    [Fact]
    public void Codigos_de_idioma_e_especiais_nao_viram_texto()
    {
        Assert.False(NllbVocabulary.IsText(NllbVocabulary.EndOfSentence));
        Assert.False(NllbVocabulary.IsText(NllbVocabulary.Unknown));
        Assert.False(NllbVocabulary.IsText(NllbVocabulary.LanguageId(Languages.Spanish)));
        Assert.True(NllbVocabulary.IsText(4));
        Assert.Equal(10, NllbVocabulary.ToPieceId(NllbVocabulary.ToModelId(10)));
    }

    [Fact]
    public void Repeticao_em_laco_e_detectada()
    {
        Assert.True(NllbVocabulary.IsLooping([1, 2, 3, 4, 1, 2, 3, 4]));
        Assert.False(NllbVocabulary.IsLooping([1, 2, 3, 4, 1, 2, 3, 5]));
        Assert.False(NllbVocabulary.IsLooping([7, 7, 7]));
    }

    [Theory]
    [InlineData("Legendas pela comunidade Amara.org")]
    [InlineData("Субтитры сделал DimaTorzok")]
    [InlineData("Продолжение следует...")]
    [InlineData("Subtítulos realizados por la comunidad de Amara.org")]
    [InlineData("[Música]")]
    [InlineData("(risos)")]
    [InlineData(" ... ")]
    [InlineData("♪")]
    public void Artefatos_do_reconhecimento_sao_descartados(string text)
    {
        Assert.Equal(string.Empty, LocalSpeechRuntime.RemoveArtifacts(text));
    }

    [Theory]
    [InlineData(" Oi, tudo bem? ", "Oi, tudo bem?")]
    [InlineData("Привет! Как дела?", "Привет! Как дела?")]
    [InlineData("Vamos ver as legendas do filme.", "Vamos ver as legendas do filme.")]
    public void Fala_de_verdade_passa(string text, string expected)
    {
        Assert.Equal(expected, LocalSpeechRuntime.RemoveArtifacts(text));
    }

    [Theory]
    [InlineData("pt-BR", "pt")]
    [InlineData("uk-UA", "uk")]
    [InlineData("es-ES", "es")]
    public void Idioma_do_reconhecimento(string language, string expected)
    {
        Assert.Equal(expected, LocalSpeechRuntime.WhisperLanguage(language));
    }

    [Fact]
    public void Motor_local_so_fica_pronto_com_os_modelos_baixados()
    {
        var factory = new SpeechEngineFactory(new ModelFolder(Path.Combine(Path.GetTempPath(), $"sem-modelos-{Guid.NewGuid():N}")));
        var local = new TranslationSettings(TranslationProvider.Local, null, null, WhisperModel.Small);

        Assert.False(factory.IsReady(local));
        Assert.Null(factory.Create(local));
        Assert.Null(TranslationSettings.Validate(local));
        Assert.NotNull(TranslationSettings.Validate(local with { LocalModel = null }));
    }
}
