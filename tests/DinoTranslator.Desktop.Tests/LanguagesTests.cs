using DinoTranslator.Shared;

namespace DinoTranslator.Desktop.Tests;

public class LanguagesTests
{
    [Fact]
    public void Lista_tem_os_cinco_idiomas_na_ordem_da_interface()
    {
        Assert.Equal(["pt-BR", "en-US", "uk-UA", "ru-RU", "es-ES"], Languages.All.Select(l => l.Code));
    }

    [Theory]
    [InlineData("pt-BR", "Português (Brasil)", "PT")]
    [InlineData("en-US", "English", "EN")]
    [InlineData("uk-UA", "Українська", "UK")]
    [InlineData("ru-RU", "Русский", "RU")]
    [InlineData("es-ES", "Español", "ES")]
    public void Cada_idioma_tem_nome_nativo_e_sigla(string code, string nativeName, string tag)
    {
        var language = Languages.Get(code);

        Assert.Equal(nativeName, language.NativeName);
        Assert.Equal(tag, language.Tag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr-FR")]
    [InlineData("PT-BR")]
    [InlineData("pt")]
    public void Codigo_fora_da_lista_nao_e_aceito(string? code)
    {
        Assert.False(Languages.IsSupported(code));
    }

    [Fact]
    public void Mesmo_idioma_dispensa_traducao()
    {
        Assert.False(Languages.NeedsTranslation(Languages.Ukrainian, Languages.Ukrainian));
        Assert.True(Languages.NeedsTranslation(Languages.Ukrainian, Languages.Spanish));
    }
}
