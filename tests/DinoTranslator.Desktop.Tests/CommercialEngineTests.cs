using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Local;

namespace DinoTranslator.Desktop.Tests;

/// <summary>
/// A versão Plus é paga: só entra o que tem licença comercial. O NLLB-200 (CC BY-NC 4.0) fica de
/// fora, inclusive como reserva; o MarianMT (CC BY 4.0) e o Whisper (MIT) entram.
/// </summary>
public sealed class CommercialEngineTests
{
    private static readonly TranslationSettings Local = new(TranslationProvider.Local, null, null, WhisperModel.Turbo);

    private static EngineAvailability Available(params TranslatorOption[] translators) => new(
        new HashSet<DetectorOption> { DetectorOption.Volume, DetectorOption.Silero },
        new HashSet<RecognizerOption> { RecognizerOption.WhisperTurbo, RecognizerOption.WhisperLarge },
        new HashSet<TranslatorOption>(translators));

    [Fact]
    public void So_o_MarianMT_e_comercial()
    {
        Assert.All(Enum.GetValues<TranslatorOption>(), option =>
            Assert.Equal(option is TranslatorOption.OpusGpu or TranslatorOption.OpusCpu, EngineChoice.IsCommercial(option)));
    }

    [Fact]
    public void Plus_usa_o_MarianMT_na_placa_quando_ha()
    {
        var choice = EngineChoice.ForPlus(Local, Available(TranslatorOption.Nllb600, TranslatorOption.Nllb13Gpu, TranslatorOption.OpusCpu, TranslatorOption.OpusGpu));

        Assert.NotNull(choice);
        Assert.Equal(TranslatorOption.OpusGpu, choice.Translator);
        Assert.True(choice.Commercial);
    }

    [Fact]
    public void Plus_mantem_o_reconhecimento_preferido_e_troca_so_o_tradutor()
    {
        var preferred = new EngineChoice(DetectorOption.Silero, 1_200, RecognizerOption.WhisperLarge, TranslatorOption.Nllb13Gpu, Careful: false);

        var choice = EngineChoice.ForPlus(Local, Available(TranslatorOption.OpusCpu), preferred)!;

        Assert.Equal(DetectorOption.Silero, choice.Detector);
        Assert.Equal(1_200, choice.PauseMs);
        Assert.Equal(RecognizerOption.WhisperLarge, choice.Recognizer);
        Assert.Equal(TranslatorOption.OpusCpu, choice.Translator);
        Assert.False(choice.Careful);
    }

    [Fact]
    public void Sem_MarianMT_nao_ha_traducao_local_paga()
    {
        Assert.Null(EngineChoice.ForPlus(Local, Available(TranslatorOption.Nllb600, TranslatorOption.Nllb600Gpu, TranslatorOption.Nllb13Gpu, TranslatorOption.Nllb13Cpu)));
    }

    [Fact]
    public void Escolha_comercial_sobrevive_a_limpeza()
    {
        var choice = EngineChoice.ForPlus(Local, Available(TranslatorOption.OpusGpu))!;

        Assert.True(choice.Sanitized(Local).Commercial);
    }
}
