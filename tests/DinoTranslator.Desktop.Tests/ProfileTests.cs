using System.IO;
using DinoTranslator.Desktop.Services;
using DinoTranslator.Shared;

namespace DinoTranslator.Desktop.Tests;

public sealed class ProfileTests : IDisposable
{
    private static readonly Profile Valid = new("Anna", Languages.Russian, Gender.Female, Formality.Informal, "Europe/Moscow");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dinotranslator-perfil-{Guid.NewGuid():N}");

    private string ProfilePath => Path.Combine(_folder, "perfil.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Perfil_valido_passa()
    {
        Assert.Null(Profile.Validate(Valid));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Anna")]
    [InlineData("Anna ")]
    [InlineData("Anna‮txt.exe")]
    [InlineData("Anna\nOutra")]
    [InlineData("um nome muito comprido que passa de quarenta caracteres")]
    public void Nome_invalido_e_recusado(string name)
    {
        Assert.NotNull(Profile.Validate(Valid with { Name = name }));
    }

    [Theory]
    [InlineData("Ганна")]
    [InlineData("José María")]
    [InlineData("Dino")]
    public void Nomes_em_qualquer_alfabeto_sao_aceitos(string name)
    {
        Assert.Null(Profile.Validate(Valid with { Name = name }));
    }

    [Fact]
    public void Idioma_genero_tratamento_e_fuso_invalidos_sao_recusados()
    {
        Assert.NotNull(Profile.Validate(Valid with { Language = "fr-FR" }));
        Assert.NotNull(Profile.Validate(Valid with { Gender = (Gender)9 }));
        Assert.NotNull(Profile.Validate(Valid with { Formality = (Formality)9 }));
        Assert.NotNull(Profile.Validate(Valid with { TimeZone = "Lua/Base_Alpha" }));
    }

    [Fact]
    public void Perfil_salvo_e_lido_de_volta()
    {
        var store = new ProfileStore(ProfilePath);
        Profile? notified = null;
        store.Changed += p => notified = p;

        store.Save(Valid);

        Assert.Equal(Valid, notified);
        Assert.Equal(Valid, new ProfileStore(ProfilePath).Current);
    }

    [Fact]
    public void Perfil_invalido_nao_e_salvo()
    {
        var store = new ProfileStore(ProfilePath);

        Assert.Throws<ArgumentException>(() => store.Save(Valid with { Name = "" }));
        Assert.False(File.Exists(ProfilePath));
    }

    [Theory]
    [InlineData("isto não é json")]
    [InlineData("""{"name":"","language":"ru-RU","gender":"Female","formality":"Informal","timeZone":"Europe/Moscow"}""")]
    public void Arquivo_corrompido_ou_adulterado_e_ignorado(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(ProfilePath, content);

        Assert.Null(new ProfileStore(ProfilePath).Current);
    }

    [Fact]
    public void Fuso_local_e_iana_e_valido()
    {
        var zone = ProfileStore.LocalTimeZone();

        Assert.Null(Profile.Validate(Valid with { TimeZone = zone }));
        Assert.DoesNotContain("Standard Time", zone);
    }
}
