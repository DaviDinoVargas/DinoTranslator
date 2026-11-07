using DinoTranslator.Shared;
using DinoTranslator.Shared.Call;

namespace DinoTranslator.Desktop.Tests;

public class AppMessagesTests
{
    private static readonly CaptionMessage Caption = new(
        CaptionSpeaker.Sender, "s3", 2, Languages.PortugueseBrazil, "Oi, tudo bem?", Languages.Russian, "Привет, как дела?", Final: false);

    public static TheoryData<AppMessage> ValidMessages => new()
    {
        new HelloMessage(CanTranslate: true),
        new ByeMessage(),
        Caption,
        Caption with { TranslatedText = null, Final = true },
    };

    [Theory]
    [MemberData(nameof(ValidMessages))]
    public void Mensagens_vao_e_voltam(AppMessage message)
    {
        Assert.Equal(message, AppMessages.TryParse(AppMessages.Serialize(message)));
    }

    [Fact]
    public void Tipo_vai_no_campo_type()
    {
        Assert.StartsWith("""{"type":"caption",""", AppMessages.Serialize(Caption));
        Assert.Equal("""{"type":"bye"}""", AppMessages.Serialize(new ByeMessage()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("""{"type":"apagar-tudo"}""")]
    [InlineData("""{"tipo":"bye"}""")]
    [InlineData("""{"canTranslate":true}""")]
    [InlineData("""["bye"]""")]
    [InlineData("""{"type":"hello","canTranslate":true,"admin":true}""")]
    public void Mensagem_invalida_ou_desconhecida_e_descartada(string? text)
    {
        Assert.Null(AppMessages.TryParse(text));
    }

    [Fact]
    public void Mensagem_grande_demais_e_descartada()
    {
        var text = "{\"type\":\"bye\",\"x\":\"" + new string('a', AppMessages.MaxLength) + "\"}";

        Assert.Null(AppMessages.TryParse(text));
    }

    public static TheoryData<CaptionMessage> InvalidCaptions => new()
    {
        Caption with { SegmentId = "S3" },
        Caption with { SegmentId = "s3<script>" },
        Caption with { SegmentId = new string('a', 41) },
        Caption with { Revision = -1 },
        Caption with { SourceLanguage = "fr-FR" },
        Caption with { TargetLanguage = "pt" },
        Caption with { SourceText = new string('a', CaptionMessage.MaxTextLength + 1) },
        Caption with { SourceText = "texto‮invertido" },
        Caption with { TranslatedText = "linha\nquebrada" },
        Caption with { Speaker = (CaptionSpeaker)7 },
    };

    [Theory]
    [MemberData(nameof(InvalidCaptions))]
    public void Legenda_com_campo_invalido_e_descartada(CaptionMessage caption)
    {
        Assert.Null(AppMessages.TryParse(AppMessages.Serialize(caption)));
    }

    [Fact]
    public void Legenda_em_cirilico_e_aceita()
    {
        var caption = Caption with { SourceLanguage = Languages.Ukrainian, SourceText = "Доброго дня! Як справи?" };

        Assert.Equal(caption, AppMessages.TryParse(AppMessages.Serialize(caption)));
    }
}
