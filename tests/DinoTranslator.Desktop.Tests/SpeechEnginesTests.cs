using System.Buffers.Binary;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Shared;
using Microsoft.CognitiveServices.Speech;

namespace DinoTranslator.Desktop.Tests;

public class SpeechEnginesTests
{
    [Theory]
    [InlineData("pt-BR", "pt")]
    [InlineData("en-US", "en")]
    [InlineData("uk-UA", "uk")]
    [InlineData("ru-RU", "ru")]
    [InlineData("es-ES", "es")]
    public void Todos_os_idiomas_tem_destino_de_traducao_no_azure(string language, string target)
    {
        Assert.Equal(target, AzureSpeechTranslationEngine.TranslationTarget(language));
    }

    [Fact]
    public void Lista_de_idiomas_e_destinos_do_azure_estao_em_dia()
    {
        Assert.All(Languages.All, language => AzureSpeechTranslationEngine.TranslationTarget(language.Code));
    }

    [Theory]
    [InlineData(CancellationErrorCode.AuthenticationFailure, "chave")]
    [InlineData(CancellationErrorCode.TooManyRequests, "Limite")]
    [InlineData(CancellationErrorCode.ConnectionFailure, "conexão")]
    [InlineData(CancellationErrorCode.RuntimeError, "parou")]
    public void Erro_do_servico_vira_frase_para_a_pessoa(CancellationErrorCode code, string expected)
    {
        Assert.Contains(expected, AzureSpeechTranslationEngine.Describe(code));
    }

    [Fact]
    public async Task Motor_simulado_gera_provisorias_e_fecha_o_trecho_depois_da_pausa()
    {
        var engine = SpeechTranslationEngines.Create(new TranslationSettings(TranslationProvider.Simulated, null, null), TimeProvider.System)!;
        await using var stream = await engine.StartAsync(
            new SpeechStreamOptions(Languages.PortugueseBrazil, Languages.Ukrainian, []), CancellationToken.None);
        var updates = new List<SpeechUpdate>();
        stream.Updated += updates.Add;

        for (var i = 0; i < 6; i++)
        {
            stream.Write(Tone(milliseconds: 100));
        }
        for (var i = 0; i < 7; i++)
        {
            stream.Write(Silence(milliseconds: 100));
        }

        Assert.Contains(updates, u => !u.Final);
        var final = Assert.Single(updates, u => u.Final);
        Assert.StartsWith("[pt-BR] trecho 0:", final.SourceText);
        Assert.StartsWith("[uk-UA] trecho 0:", final.TranslatedText);
        Assert.Equal(Enumerable.Range(0, updates.Count), updates.Select(u => u.Revision));
    }

    [Fact]
    public async Task Motor_simulado_nao_inventa_fala_no_silencio()
    {
        var engine = SpeechTranslationEngines.Create(new TranslationSettings(TranslationProvider.Simulated, null, null), TimeProvider.System)!;
        await using var stream = await engine.StartAsync(
            new SpeechStreamOptions(Languages.Russian, Languages.Spanish, []), CancellationToken.None);
        var updates = 0;
        stream.Updated += _ => updates++;

        for (var i = 0; i < 20; i++)
        {
            stream.Write(Silence(milliseconds: 100));
        }

        Assert.Equal(0, updates);
    }

    private static byte[] Tone(int milliseconds)
    {
        var samples = 16 * milliseconds;
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / 16_000) * 8_000);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
        }
        return pcm;
    }

    private static byte[] Silence(int milliseconds) => new byte[16 * milliseconds * 2];
}
