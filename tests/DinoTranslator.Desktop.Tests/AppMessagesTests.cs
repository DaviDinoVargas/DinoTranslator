using DinoTranslator.Shared.Call;

namespace DinoTranslator.Desktop.Tests;

public class AppMessagesTests
{
    [Fact]
    public void Encerramento_vai_e_volta()
    {
        var text = AppMessages.Serialize(new AppMessage(AppMessageTypes.Bye));

        Assert.Equal(new AppMessage(AppMessageTypes.Bye), AppMessages.TryParse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("""{"type":"apagar-tudo"}""")]
    [InlineData("""{"tipo":"bye"}""")]
    [InlineData("""["bye"]""")]
    [InlineData("""{"type":null}""")]
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
}
